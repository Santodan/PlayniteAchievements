using System;
using System.Collections.Generic;

namespace PlayniteAchievements.Services.Recording
{
    /// <summary>
    /// Decides, packet by packet, how much silence to stand in for audio the engine dropped
    /// before the packet at hand. Pure arithmetic over the values <c>IAudioCaptureClient.GetBuffer</c>
    /// reports — no clocks, no COM, no NAudio — so tests can drive it deterministically.
    /// <para>
    /// Gaps are measured from the packets' QPC stamps, never from <c>devicePosition</c> deltas.
    /// The stamps are in a fixed unit (100 ns), while a field log proved the position counter's
    /// unit cannot be assumed: on one machine a forced-48kHz endpoint capture (AUTOCONVERTPCM)
    /// advanced the counter at exactly 4x the frames delivered — a 192 kHz native mix format —
    /// so treating the delta as capture frames padded 3 s of silence per real second, overflowed
    /// any ring, and shredded every clip. The position counter still serves two lesser roles:
    /// the gap measure for packets whose stamp is unusable (preserving the old arithmetic
    /// exactly), and a corroborating witness that bounds what a single wild stamp can inject.
    /// </para>
    /// </summary>
    internal sealed class AudioGapTracker
    {
        private readonly int _sampleRate;
        private readonly long _maxGapFrames;
        private readonly long _jitterThreshold100ns;
        private readonly bool _stampsDisabled;

        // QPC chain: where the next packet's stamp should sit if the stream is contiguous.
        private long _expectedQpc100ns = long.MinValue;

        // Previous packet, for the position-based fallback and the corroborating witness.
        private long _lastDevicePosition = -1;
        private long _lastFrames;

        // Rate witness: first/last (qpc, position) pairs over the capture, restarted when the
        // counter runs backwards (a stream rebuild resets it).
        private long _rateFirstQpc100ns = long.MinValue;
        private long _rateFirstPosition;
        private long _rateLastQpc100ns = long.MinValue;
        private long _rateLastPosition = -1;

        private long _deliveredFrames;
        private long _paddedGapFrames;
        private long _impossibleGapFrames;

        /// <summary>How far a stamp may land early without being read back as a gap by the
        /// packet after it. One engine period of slack; also the pad-injection threshold.</summary>
        private const long DefaultJitterThresholdMs = 15;

        /// <summary>The rate witness only speaks after this much stamped time, so a couple of
        /// startup packets cannot fabricate a rate.</summary>
        private const long MinRateSpan100ns = 3 * TicksPerSecond;

        private const long TicksPerSecond = 10_000_000L;

        public AudioGapTracker(
            int sampleRate,
            int maxGapSeconds,
            long jitterThresholdMs = DefaultJitterThresholdMs,
            bool stampsDisabled = false)
        {
            if (sampleRate <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(sampleRate));
            }

            _sampleRate = sampleRate;
            _maxGapFrames = (long)sampleRate * maxGapSeconds;
            _jitterThreshold100ns = jitterThresholdMs * TicksPerSecond / 1000;
            _stampsDisabled = stampsDisabled;
        }

        /// <summary>Frames of silence stood in for engine drops so far.</summary>
        public long PaddedGapFrames => _paddedGapFrames;

        /// <summary>Padding refused because it exceeded what the wall clock allows.</summary>
        public long ImpossibleGapFrames => _impossibleGapFrames;

        /// <summary>Frames the engine actually handed over.</summary>
        public long DeliveredFrames => _deliveredFrames;

        /// <summary>
        /// How fast <c>devicePosition</c> advances, in position units per second, measured
        /// against the packets' own QPC stamps. Zero until enough stamped time has passed.
        /// Equal to the capture rate when the counter counts capture frames; the endpoint's
        /// native rate when it does not.
        /// </summary>
        public double MeasuredDevicePositionRate
        {
            get
            {
                var span = _rateLastQpc100ns - _rateFirstQpc100ns;
                if (_rateFirstQpc100ns == long.MinValue || span < MinRateSpan100ns)
                {
                    return 0;
                }

                return (_rateLastPosition - _rateFirstPosition) * (double)TicksPerSecond / span;
            }
        }

        /// <summary>Forgets every chain and counter. Call when the underlying stream restarts —
        /// neither the position counter nor the stamps carry across a stop/start.</summary>
        public void Reset()
        {
            _expectedQpc100ns = long.MinValue;
            _lastDevicePosition = -1;
            _lastFrames = 0;
            _rateFirstQpc100ns = long.MinValue;
            _rateLastQpc100ns = long.MinValue;
            _rateLastPosition = -1;
            _deliveredFrames = 0;
            _paddedGapFrames = 0;
            _impossibleGapFrames = 0;
        }

        /// <summary>
        /// Frames of silence to deliver before this packet, and advances every chain past it.
        /// <paramref name="stampUsable"/> is the caller's verdict on <paramref name="qpcPosition100ns"/>
        /// (positive, plausibly recent, and not flagged AUDCLNT_BUFFERFLAGS_TIMESTAMP_ERROR).
        /// <paramref name="elapsedFrames"/> is real time since the capture started, in frames —
        /// the ceiling no amount of delivered-plus-padded audio can truthfully exceed.
        /// </summary>
        public long TakeGapBefore(
            long devicePosition,
            uint framesAvailable,
            long qpcPosition100ns,
            bool stampUsable,
            long elapsedFrames)
        {
            if (_stampsDisabled)
            {
                stampUsable = false;
            }

            long gapFrames;
            if (stampUsable)
            {
                gapFrames = TakeStampedGap(devicePosition, framesAvailable, qpcPosition100ns);
            }
            else
            {
                gapFrames = TakeFallbackGap(devicePosition);
                if (gapFrames > _maxGapFrames)
                {
                    gapFrames = _maxGapFrames;
                }

                // Advance the QPC chain across the stampless span — delivered frames plus
                // whatever this fallback pads — so the next stamped packet does not measure
                // the same hole a second time.
                if (_expectedQpc100ns != long.MinValue)
                {
                    _expectedQpc100ns += FramesToTicks(gapFrames + framesAvailable);
                }
            }

            _deliveredFrames += framesAvailable;

            // What the wall clock says can possibly be missing: elapsed real frames, less
            // everything already delivered or padded. Never negative, never more than the gap.
            var allowed = elapsedFrames - _deliveredFrames - _paddedGapFrames;
            if (allowed < 0)
            {
                allowed = 0;
            }

            if (gapFrames > allowed)
            {
                _impossibleGapFrames += gapFrames - allowed;
                gapFrames = allowed;
            }

            _paddedGapFrames += gapFrames;
            _lastDevicePosition = devicePosition;
            _lastFrames = framesAvailable;
            return gapFrames;
        }

        /// <summary>
        /// The stamped path: a gap is this stamp landing later than the previous packet's end.
        /// The chain anchor is clamped into [stamp, stamp + jitter threshold] before advancing,
        /// so one early stamp cannot drag the baseline back and read the next on-time stamp as
        /// a gap, while clock drift can never bank more than one threshold of credit.
        /// </summary>
        private long TakeStampedGap(long devicePosition, uint framesAvailable, long qpc100ns)
        {
            long gapFrames = 0;
            if (_expectedQpc100ns != long.MinValue)
            {
                var gapTicks = qpc100ns - _expectedQpc100ns;
                if (gapTicks > _jitterThreshold100ns)
                {
                    gapFrames = TicksToFrames(gapTicks);
                    if (gapFrames > _maxGapFrames)
                    {
                        gapFrames = _maxGapFrames;
                    }

                    gapFrames = CorroborateWithPosition(devicePosition, gapFrames);
                }
            }

            var anchor = _expectedQpc100ns;
            if (anchor < qpc100ns)
            {
                anchor = qpc100ns;
            }
            else if (anchor > qpc100ns + _jitterThreshold100ns)
            {
                anchor = qpc100ns + _jitterThreshold100ns;
            }

            _expectedQpc100ns = anchor + FramesToTicks(framesAvailable);
            UpdateRateWitness(devicePosition, qpc100ns);
            return gapFrames;
        }

        /// <summary>
        /// The stampless path is the pre-stamp arithmetic verbatim: the position delta beyond
        /// the previous packet's frames, read as capture frames. Wrong by the counter's unit
        /// ratio on the machines this class exists for, but those machines have stamps; a
        /// driver with no usable stamps gets exactly the behavior it always had.
        /// </summary>
        private long TakeFallbackGap(long devicePosition)
        {
            if (_lastDevicePosition < 0)
            {
                return 0;
            }

            var expected = _lastDevicePosition + _lastFrames;
            return devicePosition > expected ? devicePosition - expected : 0;
        }

        /// <summary>
        /// Bounds a stamped gap by what the position counter saw, once the counter's own rate
        /// has been measured. A real dropout and a silent passage advance both witnesses in
        /// step; they disagree only when one of them glitches, which is exactly when the
        /// smaller claim is the safe one. A backwards counter (stream rebuild) abstains.
        /// </summary>
        private long CorroborateWithPosition(long devicePosition, long gapFrames)
        {
            var rate = MeasuredDevicePositionRate;
            if (rate <= 0 || _lastDevicePosition < 0 || devicePosition < _lastDevicePosition)
            {
                return gapFrames;
            }

            var positionAdvance = devicePosition - _lastDevicePosition;
            var advanceFrames = (long)(positionAdvance * _sampleRate / rate);
            var positionGapFrames = advanceFrames - _lastFrames;
            if (positionGapFrames < 0)
            {
                positionGapFrames = 0;
            }

            return Math.Min(gapFrames, positionGapFrames);
        }

        private void UpdateRateWitness(long devicePosition, long qpc100ns)
        {
            if (_rateFirstQpc100ns == long.MinValue || devicePosition < _rateLastPosition)
            {
                _rateFirstQpc100ns = qpc100ns;
                _rateFirstPosition = devicePosition;
            }

            _rateLastQpc100ns = qpc100ns;
            _rateLastPosition = devicePosition;
        }

        private long FramesToTicks(long frames)
        {
            return frames * TicksPerSecond / _sampleRate;
        }

        private long TicksToFrames(long ticks)
        {
            return ticks * _sampleRate / TicksPerSecond;
        }
    }

    /// <summary>
    /// Derives the UTC represented by frame zero from several stamped packets. Each packet votes
    /// for <c>packetUtc - framesAlreadyDelivered / rate</c>; the median makes one plausible but
    /// wrong startup stamp harmless. This is deliberately independent of packet arrival time.
    /// A capture pump may already have buffered several packets before a stamp enters the strict
    /// plausibility window, and anchoring those samples to that later packet shifts the whole
    /// session by exactly the buffered duration.
    /// </summary>
    internal sealed class AudioTimelineAnchorConsensus
    {
        internal const int RequiredSamples = 9;
        private const int MaximumSamples = 15;
        private const long MinimumOutlierRadiusTicks = 2 * TimeSpan.TicksPerMillisecond;

        private readonly int _sampleRate;
        private readonly object _gate = new object();
        private readonly List<long> _originTicks = new List<long>(MaximumSamples);

        public AudioTimelineAnchorConsensus(int sampleRate)
        {
            if (sampleRate <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(sampleRate));
            }

            _sampleRate = sampleRate;
        }

        /// <summary>Adds one usable packet stamp and its exact position in the delivered stream.</summary>
        public void Observe(DateTime packetUtc, long framesBeforePacket)
        {
            if (framesBeforePacket < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(framesBeforePacket));
            }

            var offsetTicks = FramesToTicks(framesBeforePacket, _sampleRate);
            var originTicks = packetUtc.Ticks - offsetTicks;
            if (originTicks < DateTime.MinValue.Ticks || originTicks > DateTime.MaxValue.Ticks)
            {
                return;
            }

            lock (_gate)
            {
                if (_originTicks.Count < MaximumSamples)
                {
                    _originTicks.Add(originTicks);
                }
            }
        }

        /// <summary>
        /// Returns the outlier-rejected median. Ordinary callers wait for nine votes; a bounded
        /// startup timeout may accept the best partial consensus instead of discarding every
        /// packet that was already buffered.
        /// </summary>
        public bool TryGet(
            bool allowPartial,
            out DateTime originUtc,
            out int samples,
            out double spreadMilliseconds)
        {
            long[] values;
            lock (_gate)
            {
                samples = _originTicks.Count;
                if (samples == 0 || (!allowPartial && samples < RequiredSamples))
                {
                    originUtc = default(DateTime);
                    spreadMilliseconds = 0;
                    return false;
                }

                values = _originTicks.ToArray();
            }

            Array.Sort(values);
            var median = Median(values);
            var deviations = new long[values.Length];
            for (var i = 0; i < values.Length; i++)
            {
                deviations[i] = Math.Abs(values[i] - median);
            }

            Array.Sort(deviations);
            var medianDeviation = Median(deviations);
            var outlierRadius = Math.Max(
                MinimumOutlierRadiusTicks,
                medianDeviation > long.MaxValue / 6 ? long.MaxValue : medianDeviation * 6);
            var inliers = new List<long>(values.Length);
            foreach (var value in values)
            {
                if (Math.Abs(value - median) <= outlierRadius)
                {
                    inliers.Add(value);
                }
            }

            // The median itself always survives. Re-taking it over the inliers removes the pull
            // from startup stamps that were plausible enough for WASAPI but not part of the chain.
            var inlierValues = inliers.ToArray();
            Array.Sort(inlierValues);
            var chosen = Median(inlierValues);
            var spreadTicks = inlierValues[inlierValues.Length - 1] - inlierValues[0];
            originUtc = new DateTime(chosen, DateTimeKind.Utc);
            spreadMilliseconds = spreadTicks / (double)TimeSpan.TicksPerMillisecond;
            return true;
        }

        private static long FramesToTicks(long frames, int sampleRate)
        {
            var seconds = frames / sampleRate;
            var remainder = frames % sampleRate;
            return checked(
                seconds * TimeSpan.TicksPerSecond +
                remainder * TimeSpan.TicksPerSecond / sampleRate);
        }

        private static long Median(long[] sorted)
        {
            var middle = sorted.Length / 2;
            if ((sorted.Length & 1) != 0)
            {
                return sorted[middle];
            }

            // Avoid overflow when two DateTime ticks near MaxValue are averaged.
            return sorted[middle - 1] + (sorted[middle] - sorted[middle - 1]) / 2;
        }
    }
}
