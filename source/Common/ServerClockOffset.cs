using System;

namespace PlayniteAchievements.Common
{
    /// <summary>
    /// Tracks the offset between a provider's server clock and this machine's capture timeline, so
    /// an unlock stamp reported in server time can be placed on the local timeline the recording
    /// buffer is stamped with.
    ///
    /// Without this, a reported stamp is only usable as a clip anchor when the two clocks happen to
    /// agree: a machine whose clock runs ahead of the provider's seeks the buffer to a position
    /// earlier than the unlock by the full offset, which composites the notification over gameplay
    /// that has not earned the achievement and can push the real moment past the clip's end.
    ///
    /// Each sample comes from one HTTP exchange, using the response's Date header as the server
    /// reading. Following NTP practice the estimate keeps the sample with the smallest round trip,
    /// because round-trip time is the whole uncertainty in a single exchange: a fast exchange
    /// brackets the server reading tightly, a slow one barely constrains it at all.
    /// </summary>
    internal sealed class ServerClockOffset
    {
        // An HTTP Date carries whole seconds, so the true server reading lies somewhere in the
        // second it names. Aim at the middle of that second rather than its start, which would
        // bias every estimate half a second in the same direction.
        private static readonly TimeSpan DateHeaderGranularity = TimeSpan.FromSeconds(1);

        // Beyond this, a change in the measured offset is the local clock being stepped rather than
        // measurement noise: a single exchange's uncertainty is bounded by its round trip plus the
        // Date header's one second, so anything this large is not explicable as noise.
        private static readonly TimeSpan ClockStepThreshold = TimeSpan.FromSeconds(5);

        private readonly object _gate = new object();
        private TimeSpan _offset;
        private TimeSpan _bestRoundTrip = TimeSpan.MaxValue;

        /// <summary>
        /// The current estimate of localTime - serverTime, or null before any sample. Positive
        /// means this machine's clock runs ahead of the server's.
        /// </summary>
        public TimeSpan? Offset
        {
            get
            {
                lock (_gate)
                {
                    return _bestRoundTrip == TimeSpan.MaxValue ? (TimeSpan?)null : _offset;
                }
            }
        }

        /// <summary>The round trip of the exchange the current estimate came from.</summary>
        public TimeSpan? SampleRoundTrip
        {
            get
            {
                lock (_gate)
                {
                    return _bestRoundTrip == TimeSpan.MaxValue ? (TimeSpan?)null : _bestRoundTrip;
                }
            }
        }

        /// <summary>
        /// Records one exchange. <paramref name="sentUtc"/> and <paramref name="receivedUtc"/> are
        /// capture-timeline readings taken either side of the request; <paramref name="serverDate"/>
        /// is the response's Date header. Ignored when the timings are not ordered or the header is
        /// implausible, so a clock change mid-request cannot poison the estimate.
        /// </summary>
        /// <returns>True when this sample became the estimate.</returns>
        public bool Observe(DateTime sentUtc, DateTime receivedUtc, DateTimeOffset? serverDate)
        {
            if (!serverDate.HasValue)
            {
                return false;
            }

            var roundTrip = receivedUtc - sentUtc;
            if (roundTrip < TimeSpan.Zero || roundTrip > TimeSpan.FromMinutes(1))
            {
                return false;
            }

            // The server generated the response somewhere inside the round trip, so the local
            // instant that matches its reading is best approximated by the midpoint.
            var localAtServerReading = sentUtc.AddTicks(roundTrip.Ticks / 2);
            var serverReading = serverDate.Value.UtcDateTime
                .AddTicks(DateHeaderGranularity.Ticks / 2);
            var offset = localAtServerReading - serverReading;

            lock (_gate)
            {
                var hasEstimate = _bestRoundTrip != TimeSpan.MaxValue;

                // A jump far larger than any round trip could explain is the local clock being
                // stepped -- a time sync landing, or the user correcting it after noticing it was
                // wrong. The retained sample describes a clock that no longer exists, so start
                // over from this one rather than holding the stale estimate until restart.
                var stepped = hasEstimate &&
                    (offset - _offset).Duration() > ClockStepThreshold;

                if (hasEstimate && !stepped && roundTrip >= _bestRoundTrip)
                {
                    return false;
                }

                _offset = offset;
                _bestRoundTrip = roundTrip;
                return true;
            }
        }

        /// <summary>
        /// Expresses a server-clock instant on the capture timeline, or null before any sample --
        /// an unconverted foreign stamp must never anchor a clip, so the caller falls back to the
        /// local observation instead of guessing the offset is zero.
        /// </summary>
        public DateTime? ToCaptureTimeline(DateTime serverUtc)
        {
            var offset = Offset;
            if (!offset.HasValue)
            {
                return null;
            }

            return DateTime.SpecifyKind(serverUtc.Add(offset.Value), DateTimeKind.Utc);
        }
    }
}
