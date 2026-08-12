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
