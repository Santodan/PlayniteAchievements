using System;

namespace PlayniteAchievements.Services.Achievements
{
    public readonly struct NiceScaleResult
    {
        public NiceScaleResult(double max, double step)
        {
            Max = max;
            Step = step;
        }

        /// <summary>Axis ceiling, a whole multiple of <see cref="Step"/> and at least 1.</summary>
        public double Max { get; }

        /// <summary>Gridline spacing on a 1-2-5 progression, at least 1.</summary>
        public double Step { get; }
    }

    /// <summary>
    /// Picks an integer axis ceiling and gridline step for a count axis so gridlines land on whole
    /// numbers and an all-zero series still gets a 0..1 axis instead of a collapsed range.
    /// </summary>
    public static class NiceScale
    {
        public const int TargetIntervals = 4;

        private const double E10 = 7.0710678118654755; // sqrt(50)
        private const double E5 = 3.1622776601683795;  // sqrt(10)
        private const double E2 = 1.4142135623730951;  // sqrt(2)

        public static NiceScaleResult ForMax(int max)
        {
            if (max <= 0)
            {
                return new NiceScaleResult(1, 1);
            }

            var raw = (double)max / TargetIntervals;
            var power = Math.Pow(10, Math.Floor(Math.Log10(raw)));
            var error = raw / power;
            var step = power * (error >= E10 ? 10 : error >= E5 ? 5 : error >= E2 ? 2 : 1);
            step = Math.Max(1, Math.Round(step));
            var ceiling = Math.Ceiling(max / step) * step;
            return new NiceScaleResult(Math.Max(1, ceiling), step);
        }
    }
}
