using System;

namespace PlayniteAchievements.Services.Capture
{
    /// <summary>
    /// Small, pure guardrails for the live recorder's GPU workload. Kept separate from WGC/MF so
    /// the thresholds and their boundary behavior can be regression-tested without native devices.
    /// </summary>
    internal static class CaptureWorkloadPolicy
    {
        // Normal scheduler jitter is still filled on the encoder's constant-rate grid. Once the
        // recorder is this far behind, backfilling every missed slot becomes a burst large enough
        // to prolong the contention that caused it. The exporter already spans an inter-segment gap
        // by holding the preceding frame, so opening a fresh segment is both cheaper and more honest.
        internal const int MaximumCatchUpMilliseconds = 250;

        // WGC runs slightly ahead of the consumer so independent scheduler phases cannot alternate
        // between a fresh frame and an avoidable duplicate. This is still a large reduction from a
        // 120/144/165/240 Hz source while preserving every configured output slot.
        internal const double CaptureSourceRateHeadroom = 1.10;

        /// <summary>The precise capture/encode interval for a configured whole-number frame rate.</summary>
        public static TimeSpan FrameInterval(int fps)
        {
            return TimeSpan.FromTicks(TimeSpan.TicksPerSecond / Math.Max(1, fps));
        }

        /// <summary>WGC producer interval with modest phase/jitter headroom over the encoder rate.</summary>
        public static TimeSpan CaptureSourceInterval(int fps)
        {
            var sourceRate = Math.Max(1, fps) * CaptureSourceRateHeadroom;
            return TimeSpan.FromTicks((long)(TimeSpan.TicksPerSecond / sourceRate));
        }

        /// <summary>
        /// Largest overdue frame count that remains a small catch-up rather than a segment resync.
        /// Rounded up so the permitted wall-clock debt is never shorter than the configured limit.
        /// </summary>
        public static int MaximumCatchUpFrames(int fps)
        {
            var rate = Math.Max(1, fps);
            return Math.Max(1, (int)Math.Ceiling(rate * (MaximumCatchUpMilliseconds / 1000d)));
        }

        public static bool ShouldResynchronize(long dueFrameCount, long writtenFrameCount, int fps)
        {
            var overdue = dueFrameCount - writtenFrameCount;
            return overdue > MaximumCatchUpFrames(fps);
        }
    }
}
