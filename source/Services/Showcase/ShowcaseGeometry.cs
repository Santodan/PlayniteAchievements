using System;

namespace PlayniteAchievements.Services.Showcase
{
    /// <summary>
    /// Interval-overlap helpers shared by the layout service and the dashboard host.
    /// Ranges are expressed as (start, span) in grid cells.
    /// </summary>
    internal static class ShowcaseGeometry
    {
        /// <summary>True when the half-open cell ranges [start, start + span) overlap.</summary>
        public static bool RangesOverlap(int firstStart, int firstSpan, int secondStart, int secondSpan) =>
            firstStart < secondStart + secondSpan && secondStart < firstStart + firstSpan;

        /// <summary>Number of cells shared by two ranges; 0 when disjoint.</summary>
        public static int Overlap(int firstStart, int firstSpan, int secondStart, int secondSpan) =>
            Math.Max(0, Math.Min(firstStart + firstSpan, secondStart + secondSpan) - Math.Max(firstStart, secondStart));
    }
}
