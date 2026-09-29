using System;
using PlayniteAchievements.Models.Achievements.Scoring;

namespace PlayniteAchievements.ViewModels.Showcase.Widgets
{
    /// <summary>Axis floor, ceiling and reference lines for one score card's history chart.</summary>
    public sealed class ScoreHistoryAxisFrame
    {
        public ScoreHistoryAxisFrame(double min, double max, double? currentTierLine, double? nextTierLine)
        {
            Min = min;
            Max = max;
            CurrentTierLine = currentTierLine;
            NextTierLine = nextTierLine;
        }

        /// <summary>Axis floor: the window's first value, or the current tier's start when nothing was earned.</summary>
        public double Min { get; }

        /// <summary>
        /// Axis ceiling: the window's last value plus headroom, snapped down to the next tier's start
        /// when that lies within the headroom. NaN (auto) only when no next tier exists.
        /// </summary>
        public double Max { get; }

        /// <summary>The current tier's start score when it lies strictly inside the axis.</summary>
        public double? CurrentTierLine { get; }

        /// <summary>The next tier's start score when the ceiling snapped to it.</summary>
        public double? NextTierLine { get; }
    }

    /// <summary>
    /// Fits a score history chart's Y axis to the score earned inside the window so the line uses the
    /// chart's full height however wide the current tier is, and derives the two tier reference lines
    /// from the level curve. A tier here is a rank on the ladder (Bronze V, Bronze IV, ... Master I),
    /// the unit the score card names.
    /// </summary>
    public static class ScoreHistoryAxis
    {
        /// <summary>Share of the window's gain kept above the line so its end never touches the chart top.</summary>
        public const double Headroom = 0.10;

        public static ScoreHistoryAxisFrame Frame(int currentScore, int windowMin, int windowMax)
        {
            windowMin = Math.Max(0, windowMin);
            windowMax = Math.Max(windowMin, windowMax);
            // The last history point should equal the live score, but the frame tolerates the two
            // disagreeing by anchoring on whichever is higher so the line never clips.
            var effectiveScore = Math.Max(currentScore, windowMax);
            var current = AchievementLevelCalculator.CalculateModern(effectiveScore);
            double tierStart = AchievementLevelCalculator.GetScoreForLevel(current.RankStartLevel);
            var nextTierStart = current.NextRankScoreThreshold > effectiveScore
                ? current.NextRankScoreThreshold
                : double.NaN;

            var gain = windowMax - windowMin;
            double min;
            double max;
            double? nextTierLine = null;
            if (gain > 0)
            {
                min = windowMin;
                var ceiling = windowMax + gain * Headroom;
                if (!double.IsNaN(nextTierStart) && nextTierStart <= ceiling)
                {
                    max = nextTierStart;
                    nextTierLine = nextTierStart;
                }
                else
                {
                    max = ceiling;
                }
            }
            else
            {
                // Nothing earned in the window: frame the current tier so the flat line still reads
                // as a position within it, matching the card's segmented bar.
                min = tierStart;
                max = nextTierStart;
                if (!double.IsNaN(nextTierStart))
                {
                    nextTierLine = nextTierStart;
                }
            }

            double? currentTierLine = IsInside(tierStart, min, max) ? tierStart : (double?)null;
            return new ScoreHistoryAxisFrame(min, max, currentTierLine, nextTierLine);
        }

        private static bool IsInside(double value, double min, double max)
        {
            return value > min && (double.IsNaN(max) || value < max);
        }
    }
}
