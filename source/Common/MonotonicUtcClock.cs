using System;
using System.Diagnostics;

namespace PlayniteAchievements.Common
{
    /// <summary>One QPC-backed UTC timeline shared by capture, detection, toast, audio, and export.</summary>
    internal static class CaptureTimelineClock
    {
        private static readonly DateTime OriginUtc;
        private static readonly long OriginTimestamp;

        static CaptureTimelineClock()
        {
            var before = Stopwatch.GetTimestamp();
            OriginUtc = DateTime.UtcNow;
            var after = Stopwatch.GetTimestamp();
            OriginTimestamp = before + (after - before) / 2;
        }

        public static DateTime UtcNow => Project(
            OriginUtc, OriginTimestamp, Stopwatch.GetTimestamp(), Stopwatch.Frequency);

        /// <summary>
        /// Projects an audio-engine QPC position (already expressed in 100-ns units) onto this
        /// clock's UTC timeline. The age and UTC value come from one Stopwatch sample, so packet
        /// placement cannot inherit the execution delay between two separate clock reads.
        /// </summary>
        internal static DateTime FromQpc100ns(long qpcPosition100ns, out long age100ns)
        {
            var nowTimestamp = Stopwatch.GetTimestamp();
            var frequency = Stopwatch.Frequency;
            var now100ns = TimestampTo100ns(nowTimestamp, frequency);
            age100ns = now100ns - qpcPosition100ns;
            return Project(OriginUtc, OriginTimestamp, nowTimestamp, frequency)
                .AddTicks(-age100ns);
        }

        internal static long TimestampTo100ns(long timestamp, long frequency)
        {
            if (frequency <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(frequency));
            }

            var seconds = timestamp / frequency;
            var remainder = timestamp % frequency;
            return checked(
                seconds * TimeSpan.TicksPerSecond +
                remainder * TimeSpan.TicksPerSecond / frequency);
        }

        internal static DateTime Project(
            DateTime originUtc, long originTimestamp, long timestamp, long frequency)
        {
            if (frequency <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(frequency));
            }

            var elapsed = timestamp - originTimestamp;
            if (elapsed <= 0)
            {
                return originUtc;
            }

            var seconds = elapsed / frequency;
            var remainder = elapsed % frequency;
            return originUtc.AddTicks(
                checked(seconds * TimeSpan.TicksPerSecond) +
                remainder * TimeSpan.TicksPerSecond / frequency);
        }
    }
}
