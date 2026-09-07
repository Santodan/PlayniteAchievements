using System;

namespace PlayniteAchievements.Services.Sound
{
    /// <summary>
    /// Backoff for relaunching the sound host after it exits unexpectedly. A run that stayed alive
    /// long enough resets the count; after three quick failures the host stays down until the next
    /// explicit start (a settings save or a play request).
    /// </summary>
    internal static class SoundHostRestartPolicy
    {
        public static readonly TimeSpan HealthyRunThreshold = TimeSpan.FromSeconds(60);

        private static readonly TimeSpan[] Delays =
        {
            TimeSpan.FromMilliseconds(500),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(8),
        };

        /// <summary>Delay before the next automatic restart, or null to stop retrying.</summary>
        public static TimeSpan? NextDelay(int consecutiveFailures)
        {
            if (consecutiveFailures <= 0)
            {
                return Delays[0];
            }

            return consecutiveFailures <= Delays.Length ? Delays[consecutiveFailures - 1] : (TimeSpan?)null;
        }

        /// <summary>The failure count after a run of the given length ended.</summary>
        public static int NextFailureCount(int consecutiveFailures, TimeSpan runLength)
        {
            return runLength >= HealthyRunThreshold ? 1 : consecutiveFailures + 1;
        }
    }
}
