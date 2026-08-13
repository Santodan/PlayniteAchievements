using System;

namespace PlayniteAchievements.Views.Helpers
{
    /// <summary>
    /// Maps a horizontal slider-track coordinate directly to a value. The calculation deliberately
    /// has no dependency on the slider's current value or the thumb's last arranged position, so
    /// the same pixel always produces the same result.
    /// </summary>
    internal static class SliderTrackValueMath
    {
        public static double FromHorizontalPoint(
            double x,
            double trackWidth,
            double thumbWidth,
            double minimum,
            double maximum,
            bool isDirectionReversed)
        {
            if (trackWidth <= 0 || double.IsNaN(trackWidth) || double.IsInfinity(trackWidth))
            {
                return minimum;
            }

            // A malformed/custom template must not make the travel negative. With no available
            // travel, fall back to the full track instead of dividing by zero.
            var effectiveThumbWidth = Math.Max(0, Math.Min(thumbWidth, trackWidth));
            var travel = trackWidth - effectiveThumbWidth;
            var fraction = travel > 0
                ? (x - (effectiveThumbWidth / 2)) / travel
                : x / trackWidth;

            fraction = Math.Max(0, Math.Min(1, fraction));
            if (isDirectionReversed)
            {
                fraction = 1 - fraction;
            }

            return minimum + (fraction * (maximum - minimum));
        }
    }
}
