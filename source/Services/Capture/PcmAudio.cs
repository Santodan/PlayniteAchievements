using System;

namespace PlayniteAchievements.Services.Capture
{
    /// <summary>
    /// Pure 16-bit PCM helpers for the clip export: mixing the recorded chime into a clip's
    /// audio. Kept free of Media Foundation so it unit-tests directly.
    /// </summary>
    internal static class PcmAudio
    {
        /// <summary>Bytes per second of the export PCM format (48 kHz, stereo, 16-bit).</summary>
        public const int BytesPerSecond = 48000 * 2 * 2;

        /// <summary>Sample-frame alignment in bytes (stereo 16-bit).</summary>
        public const int BlockAlign = 4;

        // Two independent application-loopback clients share the endpoint clock but can begin a
        // handful of packets apart. Search a bounded neighbourhood before cancellation so that a
        // sub-packet offset cannot leave the game behind as a comb-filtered echo.
        private const int MaxCancellationLagFrames = 2400; // 50 ms at 48 kHz
        private const int CorrelationStrideFrames = 8;
        private const int CorrelationWindowFrames = 24000; // score 0.5 s, then cancel the whole window
        private const double MinimumCancellationCorrelation = 0.55;
        private const double MinimumCancellationGain = 0.80;
        private const double MaximumCancellationGain = 1.20;
        private const double SilentReferenceRms = 16.0; // about -66 dBFS

        /// <summary>Converts a 100-ns tick offset to a block-aligned byte offset.</summary>
        public static long TicksToAlignedBytes(long ticks)
        {
            var bytes = (long)(ticks / 10_000_000.0 * BytesPerSecond);
            return bytes & ~(long)(BlockAlign - 1);
        }

        /// <summary>
        /// Applies a linear fade-out over the final <paramref name="seconds"/> of a 16-bit PCM
        /// buffer in place, so a chime cut mid-ring ends silently instead of clicking.
        /// </summary>
        public static void FadeOutTail(byte[] pcm, double seconds)
        {
            if (pcm == null || pcm.Length < BlockAlign || seconds <= 0)
            {
                return;
            }

            var fadeBytes = Math.Min((long)pcm.Length & ~(long)(BlockAlign - 1), TicksToAlignedBytes((long)(seconds * 10_000_000)));
            if (fadeBytes < BlockAlign)
            {
                return;
            }

            var start = pcm.Length - fadeBytes;
            for (long i = start; i + 1 < pcm.Length; i += 2)
            {
                var scale = 1.0 - ((i - start) / (double)fadeBytes);
                var value = (short)(pcm[i] | (pcm[i + 1] << 8));
                var faded = (short)(value * scale);
                pcm[i] = (byte)(faded & 0xff);
                pcm[i + 1] = (byte)((faded >> 8) & 0xff);
            }
        }

        /// <summary>
        /// Saturating add of 16-bit little-endian source samples into the destination in place.
        /// Offsets and count are in bytes and are clamped to both buffers; odd trailing bytes are
        /// ignored (16-bit samples only move in pairs).
        /// </summary>
        public static void MixInto(byte[] dest, long destOffset, byte[] source, long sourceOffset, long byteCount)
        {
            if (dest == null || source == null || destOffset < 0 || sourceOffset < 0)
            {
                return;
            }

            var count = Math.Min(byteCount, Math.Min(dest.Length - destOffset, source.Length - sourceOffset));
            count &= ~1L;
            if (count <= 0)
            {
                return;
            }

            for (long i = 0; i + 1 < count; i += 2)
            {
                var d = (short)(dest[destOffset + i] | (dest[destOffset + i + 1] << 8));
                var s = (short)(source[sourceOffset + i] | (source[sourceOffset + i + 1] << 8));
                var mixed = d + s;
                if (mixed > short.MaxValue)
                {
                    mixed = short.MaxValue;
                }
                else if (mixed < short.MinValue)
                {
                    mixed = short.MinValue;
                }

                dest[destOffset + i] = (byte)(mixed & 0xff);
                dest[destOffset + i + 1] = (byte)((mixed >> 8) & 0xff);
            }
        }

        /// <summary>
        /// Removes a simultaneously captured game-only reference from a Playnite-tree mixture in
        /// place. The two process-loopback clients are aligned by bounded normalized correlation;
        /// a fitted gain must confirm their shared unity scale before exact subtraction. Returns
        /// false without modifying the mixture when the signals are not demonstrably the same game
        /// track; callers must omit the chime rather than risk mixing unidentified audio into the
        /// clip.
        /// </summary>
        public static bool TryCancelCorrelated(
            byte[] mixture,
            byte[] gameReference,
            out int lagFrames,
            out double correlation)
        {
            lagFrames = 0;
            correlation = 0;
            if (mixture == null || gameReference == null ||
                mixture.Length < BlockAlign || gameReference.Length < BlockAlign)
            {
                return false;
            }

            var mixtureFrames = mixture.Length / BlockAlign;
            var referenceFrames = gameReference.Length / BlockAlign;
            var maxLag = Math.Min(
                MaxCancellationLagFrames,
                Math.Max(0, Math.Min(mixtureFrames, referenceFrames) / 4));
            var analysisStart = FindLoudestWindowStart(gameReference, referenceFrames);

            var best = default(CorrelationScore);
            best.Value = double.NegativeInfinity;
            for (var lag = -maxLag; lag <= maxLag; lag++)
            {
                var score = ScoreCorrelation(mixture, gameReference, lag, analysisStart);
                if (score.Value > best.Value)
                {
                    best = score;
                }
            }

            lagFrames = best.LagFrames;
            correlation = best.Value;
            if (best.Count <= 0)
            {
                return false;
            }

            var referenceRms = best.ReferenceEnergy <= 0
                ? 0
                : Math.Sqrt(best.ReferenceEnergy / best.Count);
            if (referenceRms <= SilentReferenceRms)
            {
                // There is no audible game signal to leak out of the sidecar. Treat it as already
                // clean so a chime over a silent/loading scene is not discarded for low correlation.
                correlation = 1;
                lagFrames = 0;
                return true;
            }

            if (best.ReferenceEnergy <= 0)
            {
                return false;
            }

            var measuredGain = best.Dot / best.ReferenceEnergy;
            if (correlation < MinimumCancellationCorrelation ||
                measuredGain < MinimumCancellationGain || measuredGain > MaximumCancellationGain)
            {
                return false;
            }

            var mixStart = Math.Max(0, -lagFrames);
            var mixEnd = Math.Min(mixtureFrames, referenceFrames - lagFrames);
            for (var frame = mixStart; frame < mixEnd; frame++)
            {
                var referenceFrame = frame + lagFrames;
                var mixByte = frame * BlockAlign;
                var referenceByte = referenceFrame * BlockAlign;
                for (var channel = 0; channel < 2; channel++)
                {
                    var mixOffset = mixByte + channel * 2;
                    var referenceOffset = referenceByte + channel * 2;
                    var mixed = ReadInt16(mixture, mixOffset);
                    var reference = ReadInt16(gameReference, referenceOffset);
                    var cancelled = mixed - reference;
                    cancelled = Math.Max(short.MinValue, Math.Min(short.MaxValue, cancelled));
                    WriteInt16(mixture, mixOffset, (short)cancelled);
                }
            }

            return true;
        }

        private static int FindLoudestWindowStart(byte[] reference, int referenceFrames)
        {
            var windowFrames = Math.Min(CorrelationWindowFrames, referenceFrames);
            var lastStart = referenceFrames - windowFrames;
            var step = Math.Max(1, windowFrames / 2);
            var bestStart = 0;
            var bestEnergy = -1d;

            for (var start = 0; ; start = Math.Min(start + step, lastStart))
            {
                double energy = 0;
                for (var frame = start; frame < start + windowFrames; frame += CorrelationStrideFrames)
                {
                    var offset = frame * BlockAlign;
                    for (var channel = 0; channel < 2; channel++)
                    {
                        var sample = ReadInt16(reference, offset + channel * 2);
                        energy += sample * (double)sample;
                    }
                }

                if (energy > bestEnergy)
                {
                    bestEnergy = energy;
                    bestStart = start;
                }

                if (start == lastStart)
                {
                    return bestStart;
                }
            }
        }

        private static CorrelationScore ScoreCorrelation(
            byte[] mixture,
            byte[] reference,
            int lagFrames,
            int analysisStart)
        {
            var mixtureFrames = mixture.Length / BlockAlign;
            var referenceFrames = reference.Length / BlockAlign;
            var referenceStart = Math.Max(analysisStart, lagFrames);
            var referenceEnd = Math.Min(
                Math.Min(referenceFrames, analysisStart + CorrelationWindowFrames),
                mixtureFrames + lagFrames);
            double dot = 0;
            double mixtureEnergy = 0;
            double referenceEnergy = 0;
            long count = 0;

            for (var referenceFrame = referenceStart;
                 referenceFrame < referenceEnd;
                 referenceFrame += CorrelationStrideFrames)
            {
                var mixtureFrame = referenceFrame - lagFrames;
                var mixtureByte = mixtureFrame * BlockAlign;
                var referenceByte = referenceFrame * BlockAlign;
                for (var channel = 0; channel < 2; channel++)
                {
                    var mixed = ReadInt16(mixture, mixtureByte + channel * 2);
                    var source = ReadInt16(reference, referenceByte + channel * 2);
                    dot += mixed * (double)source;
                    mixtureEnergy += mixed * (double)mixed;
                    referenceEnergy += source * (double)source;
                    count++;
                }
            }

            var denominator = Math.Sqrt(mixtureEnergy * referenceEnergy);
            return new CorrelationScore
            {
                LagFrames = lagFrames,
                Dot = dot,
                ReferenceEnergy = referenceEnergy,
                Count = count,
                Value = denominator > 0 ? dot / denominator : 0,
            };
        }

        private static short ReadInt16(byte[] bytes, long offset)
        {
            return (short)(bytes[offset] | (bytes[offset + 1] << 8));
        }

        private static void WriteInt16(byte[] bytes, long offset, short value)
        {
            bytes[offset] = (byte)(value & 0xff);
            bytes[offset + 1] = (byte)((value >> 8) & 0xff);
        }

        private struct CorrelationScore
        {
            public int LagFrames;
            public double Dot;
            public double ReferenceEnergy;
            public long Count;
            public double Value;
        }
    }
}
