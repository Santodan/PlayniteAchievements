using System;
using System.Collections.Generic;
using System.Linq;
using PlayniteAchievements.Models.Achievements.Scoring;

namespace PlayniteAchievements.ViewModels.Showcase.Widgets
{
    /// <summary>
    /// A tier-family change (Bronze, Silver, Gold, Platinum, Master) whose start score lies inside
    /// the visible range of a score history chart.
    /// </summary>
    public sealed class ScoreHistoryTierStart
    {
        public ScoreHistoryTierStart(double score, AchievementRank rank)
        {
            Score = score;
            Rank = rank;
        }

        public double Score { get; }

        public AchievementRank Rank { get; }
    }

    /// <summary>Axis floor, ceiling and reference lines for one score card's history chart.</summary>
    public sealed class ScoreHistoryAxisFrame
    {
        public ScoreHistoryAxisFrame(
            double min,
            double max,
            double? currentLevelLine,
            double? nextLevelLine,
            IReadOnlyList<ScoreHistoryTierStart> tierStarts)
        {
            Min = min;
            Max = max;
            CurrentLevelLine = currentLevelLine;
            NextLevelLine = nextLevelLine;
            TierStarts = tierStarts ?? Array.Empty<ScoreHistoryTierStart>();
        }

        /// <summary>Axis floor: the window's first value, or the current level's start when nothing was earned.</summary>
        public double Min { get; }

        /// <summary>
        /// Axis ceiling: the window's last value plus headroom, snapped down to the next level's start
        /// when that lies within the headroom. NaN (auto) only at the true maximum level.
        /// </summary>
        public double Max { get; }

        /// <summary>The current level's start score when it lies strictly inside the axis and is not a tier start.</summary>
        public double? CurrentLevelLine { get; }

        /// <summary>The next level's start score when the ceiling snapped to it.</summary>
        public double? NextLevelLine { get; }

        /// <summary>Tier-family changes reached strictly inside the axis, ascending by score.</summary>
        public IReadOnlyList<ScoreHistoryTierStart> TierStarts { get; }
    }

    /// <summary>
    /// Fits a score history chart's Y axis to the score earned inside the window so the line uses the
    /// chart's full height however wide the current level is, and derives the level and tier reference
    /// lines from the level curve and rank table.
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
            double levelStart = current.CurrentLevelStartScore;
            var nextStart = current.IsMaxLevel || current.CurrentLevelEndScore >= int.MaxValue - 1
                ? double.NaN
                : current.CurrentLevelEndScore + 1d;

            var gain = windowMax - windowMin;
            double min;
            double max;
            double? nextLevelLine = null;
            if (gain > 0)
            {
                min = windowMin;
                var ceiling = windowMax + gain * Headroom;
                if (!double.IsNaN(nextStart) && nextStart <= ceiling)
                {
                    max = nextStart;
                    nextLevelLine = nextStart;
                }
                else
                {
                    max = ceiling;
                }
            }
            else
            {
                // Nothing earned in the window: frame the current level so the flat line still
                // reads as a position within it.
                min = levelStart;
                max = nextStart;
                if (!double.IsNaN(nextStart))
                {
                    nextLevelLine = nextStart;
                }
            }

            var tierStarts = CollectTierStarts(windowMin, effectiveScore, min, max);
            double? currentLevelLine = IsInside(levelStart, min, max) &&
                tierStarts.All(tier => tier.Score != levelStart)
                ? levelStart
                : (double?)null;

            return new ScoreHistoryAxisFrame(min, max, currentLevelLine, nextLevelLine, tierStarts);
        }

        private static bool IsInside(double value, double min, double max)
        {
            return value > min && (double.IsNaN(max) || value < max);
        }

        /// <summary>
        /// Walks the rank table once per mastery pass the window touches. A tier starts at the first
        /// rank whose family differs from the rank before it; the rank before a pass's first entry is
        /// the previous pass's last, so a mastery rollover counts while the ladder's very first rank
        /// does not.
        /// </summary>
        private static IReadOnlyList<ScoreHistoryTierStart> CollectTierStarts(
            int windowMin,
            int effectiveScore,
            double min,
            double max)
        {
            var settings = AchievementLevelCurveSettings.ModernDefault;
            var thresholds = (settings.RankThresholds ?? AchievementLevelCurveSettings.CreateDefaultRankThresholds())
                .Where(threshold => threshold != null)
                .OrderBy(threshold => threshold.MaxLevel)
                .ToList();
            if (thresholds.Count == 0)
            {
                return Array.Empty<ScoreHistoryTierStart>();
            }

            var levelsPerPass = settings.MaxDisplayLevel;
            var firstPass = AchievementLevelCalculator.CalculateModern(windowMin).Mastery;
            var lastPass = AchievementLevelCalculator.CalculateModern(effectiveScore).Mastery;
            var results = new List<ScoreHistoryTierStart>();
            for (var pass = firstPass; pass <= lastPass; pass++)
            {
                for (var i = 0; i < thresholds.Count; i++)
                {
                    string previousFamily;
                    int startLevel;
                    if (i == 0)
                    {
                        if (pass == 0)
                        {
                            continue;
                        }

                        previousFamily = Family(thresholds[thresholds.Count - 1].Rank);
                        startLevel = 0;
                    }
                    else
                    {
                        previousFamily = Family(thresholds[i - 1].Rank);
                        startLevel = thresholds[i - 1].MaxLevel + 1;
                    }

                    if (string.Equals(Family(thresholds[i].Rank), previousFamily, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var absoluteLevel = (long)pass * levelsPerPass + startLevel;
                    if (absoluteLevel > int.MaxValue)
                    {
                        return results;
                    }

                    double score = AchievementLevelCalculator.GetScoreForLevel((int)absoluteLevel);
                    if (score <= effectiveScore && IsInside(score, min, max))
                    {
                        results.Add(new ScoreHistoryTierStart(score, thresholds[i].Rank));
                    }
                }
            }

            return results;
        }

        /// <summary>The rank name without its position digit: Bronze5 and Bronze1 share a family.</summary>
        private static string Family(AchievementRank rank)
        {
            return rank.ToString().TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
        }
    }
}
