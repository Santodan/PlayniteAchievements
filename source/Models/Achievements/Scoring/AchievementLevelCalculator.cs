using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Models.Achievements.Scoring
{
    public static class AchievementLevelCalculator
    {
        private struct AchievementLevelRange
        {
            public int Level { get; set; }

            public int StartScore { get; set; }

            public int EndScore { get; set; }

            public int Size { get; set; }
        }

        public static AchievementLevelSnapshot Calculate(int score)
        {
            return Calculate(score, AchievementLevelCurveSettings.ModernDefault);
        }

        public static AchievementLevelSnapshot CalculateModern(int score)
        {
            return Calculate(score, AchievementLevelCurveSettings.ModernDefault);
        }

        public static AchievementLevelSnapshot CalculateLegacy(int score)
        {
            return Calculate(score, AchievementLevelCurveSettings.LegacyCompatible);
        }

        public static AchievementLevelSnapshot Calculate(
            int score,
            AchievementLevelCurveSettings settings)
        {
            var snapshot = new AchievementLevelSnapshot();
            if (score <= 0)
            {
                return snapshot;
            }

            settings = AchievementLevelCurveSettings.Normalize(settings);
            var range = FindLevelRangeForScore(score, settings);
            var rank = RankFromLevelValue(range.Level, settings);

            snapshot.Level = range.Level;
            snapshot.DisplayLevel = range.Level + 1;
            snapshot.LevelProgress = CalculateProgress(score, range);
            snapshot.RankValue = rank;
            snapshot.Rank = rank.ToString();
            return snapshot;
        }

        public static string RankFromLevel(int level)
        {
            return RankFromLevelValue(level, AchievementLevelCurveSettings.ModernDefault).ToString();
        }

        public static AchievementRank RankFromLevelValue(
            int level,
            AchievementLevelCurveSettings settings = null)
        {
            settings = AchievementLevelCurveSettings.Normalize(settings);
            var normalizedLevel = Math.Max(0, level);
            foreach (var threshold in GetOrderedRankThresholds(settings))
            {
                if (normalizedLevel <= threshold.MaxLevel)
                {
                    return threshold.Rank;
                }
            }

            return AchievementRank.Plat;
        }

        public static IReadOnlyList<AchievementRankDebugRow> BuildRankDebugTable(
            AchievementLevelCurveSettings settings = null)
        {
            settings = AchievementLevelCurveSettings.Normalize(settings);
            var rows = new List<AchievementRankDebugRow>();
            var minLevel = 0;
            var previousMinScore = 0;

            foreach (var threshold in GetOrderedRankThresholds(settings))
            {
                var minRange = GetLevelRange(minLevel, settings);
                var minScore = minRange.StartScore;
                var maxScore = threshold.MaxLevel == int.MaxValue
                    ? int.MaxValue
                    : GetLevelRange(threshold.MaxLevel, settings).EndScore;

                rows.Add(new AchievementRankDebugRow
                {
                    Rank = threshold.Rank,
                    MinLevel = minLevel,
                    MaxLevel = threshold.MaxLevel,
                    MinScore = minScore,
                    MaxScore = maxScore,
                    ScoreNeededFromPreviousRank = rows.Count == 0
                        ? 0
                        : Math.Max(0, minScore - previousMinScore)
                });

                previousMinScore = minScore;
                if (threshold.MaxLevel == int.MaxValue)
                {
                    break;
                }

                minLevel = threshold.MaxLevel + 1;
            }

            return rows.AsReadOnly();
        }

        private static AchievementLevelRange FindLevelRangeForScore(
            int score,
            AchievementLevelCurveSettings settings)
        {
            var range = GetInitialLevelRange(settings);
            while (score > range.EndScore && range.EndScore < int.MaxValue)
            {
                range = GetNextLevelRange(range, settings);
            }

            return range;
        }

        private static AchievementLevelRange GetLevelRange(
            int level,
            AchievementLevelCurveSettings settings)
        {
            var range = GetInitialLevelRange(settings);
            var targetLevel = Math.Max(0, level);
            while (range.Level < targetLevel && range.EndScore < int.MaxValue)
            {
                range = GetNextLevelRange(range, settings);
            }

            return range;
        }

        private static AchievementLevelRange GetInitialLevelRange(
            AchievementLevelCurveSettings settings)
        {
            return new AchievementLevelRange
            {
                Level = 0,
                StartScore = 1,
                EndScore = settings.InitialLevelSize,
                Size = settings.InitialLevelSize
            };
        }

        private static AchievementLevelRange GetNextLevelRange(
            AchievementLevelRange current,
            AchievementLevelCurveSettings settings)
        {
            var nextSize = AddClamped(current.Size, GetGrowthForNextLevel(current.Level, settings));
            var nextStart = current.EndScore == int.MaxValue
                ? int.MaxValue
                : current.EndScore + 1;
            var nextEnd = AddClamped(nextStart, nextSize - 1);

            return new AchievementLevelRange
            {
                Level = AddClamped(current.Level, 1),
                StartScore = nextStart,
                EndScore = nextEnd,
                Size = nextSize
            };
        }

        private static int GetGrowthForNextLevel(
            int currentLevel,
            AchievementLevelCurveSettings settings)
        {
            if (currentLevel < settings.TopEndEaseStartLevel)
            {
                return settings.BaseLevelGrowth;
            }

            var easedGrowth = settings.BaseLevelGrowth * settings.TopEndGrowthMultiplier;
            return Math.Max(1, (int)Math.Round(easedGrowth, MidpointRounding.AwayFromZero));
        }

        private static int CalculateProgress(
            int score,
            AchievementLevelRange range)
        {
            var span = Math.Max(1, range.EndScore - range.StartScore + 1);
            var progress = (double)(score - range.StartScore) / span;
            return ClampPercent(progress);
        }

        private static int ClampPercent(double progress)
        {
            if (double.IsNaN(progress) || double.IsInfinity(progress))
            {
                return 0;
            }

            var percent = (int)(progress * 100);
            return Math.Max(0, Math.Min(100, percent));
        }

        private static IReadOnlyList<AchievementRankThreshold> GetOrderedRankThresholds(
            AchievementLevelCurveSettings settings)
        {
            var thresholds = settings.RankThresholds;
            if (thresholds == null || thresholds.Count == 0)
            {
                thresholds = AchievementLevelCurveSettings.CreateDefaultRankThresholds();
            }

            return thresholds
                .Where(threshold => threshold != null)
                .OrderBy(threshold => threshold.MaxLevel)
                .ToList()
                .AsReadOnly();
        }

        private static int AddClamped(int current, int value)
        {
            var result = (long)current + value;
            return result > int.MaxValue ? int.MaxValue : (int)result;
        }
    }
}
