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
            var safeScore = Math.Max(0, score);
            settings = AchievementLevelCurveSettings.Normalize(settings);
            if (!settings.RepeatsAfterMax)
            {
                var single = CalculatePass(safeScore, settings);
                single.PassLevel = single.Level;
                return single;
            }

            // Mastery: the ladder repeats every cycleLength points. The pass is computed on the
            // score inside the current cycle, which never reaches the cap level, then shifted back
            // onto the running total so levels and score thresholds stay absolute.
            var cycleLength = GetCycleLength(settings);
            var mastery = safeScore <= 0 ? 0 : (safeScore - 1) / cycleLength;
            var offset = (long)mastery * cycleLength;
            var snapshot = CalculatePass((int)(safeScore - offset), settings);
            var levelOffset = (long)mastery * settings.MaxDisplayLevel;

            snapshot.Mastery = mastery;
            snapshot.PassLevel = snapshot.Level;
            snapshot.Level = ClampToInt(snapshot.Level + levelOffset);
            snapshot.DisplayLevel = ClampToInt(snapshot.DisplayLevel + levelOffset);
            snapshot.RankStartLevel = ClampToInt(snapshot.RankStartLevel + levelOffset);
            snapshot.RankEndLevel = ClampToInt(snapshot.RankEndLevel + levelOffset);
            snapshot.CurrentLevelStartScore = ClampToInt(snapshot.CurrentLevelStartScore + offset);
            snapshot.CurrentLevelEndScore = ClampToInt(snapshot.CurrentLevelEndScore + offset);

            if (string.IsNullOrEmpty(snapshot.NextRank))
            {
                // Last rank of the pass: the next rank is the first rank of the next mastery.
                var firstRank = GetOrderedRankThresholds(settings)[0].Rank;
                var nextPassStart = offset + cycleLength + 1;
                snapshot.NextRankValue = firstRank;
                snapshot.NextRank = firstRank.ToString();
                snapshot.NextRankScoreThreshold = ClampToInt(nextPassStart);
                snapshot.PointsUntilNextRank = ClampToInt(Math.Max(0, nextPassStart - safeScore));
            }
            else
            {
                snapshot.NextRankScoreThreshold = ClampToInt(snapshot.NextRankScoreThreshold + offset);
            }

            return snapshot;
        }

        /// <summary>
        /// Start score of an absolute level, counting mastery passes on a repeating curve. The
        /// inverse of <see cref="Calculate(int, AchievementLevelCurveSettings)"/>'s Level.
        /// </summary>
        public static int GetScoreForLevel(int level, AchievementLevelCurveSettings settings = null)
        {
            settings = AchievementLevelCurveSettings.Normalize(settings);
            var safeLevel = Math.Max(0, level);
            if (!settings.RepeatsAfterMax)
            {
                return GetLevelRange(safeLevel, settings).StartScore;
            }

            var mastery = safeLevel / settings.MaxDisplayLevel;
            var passLevel = safeLevel % settings.MaxDisplayLevel;
            return ClampToInt((long)mastery * GetCycleLength(settings) +
                GetLevelRange(passLevel, settings).StartScore);
        }

        /// <summary>Points in one full pass: everything below the cap level's start score.</summary>
        private static int GetCycleLength(AchievementLevelCurveSettings settings)
        {
            var key = new CycleKey(
                settings.InitialLevelSize,
                settings.BaseLevelGrowth,
                settings.TopEndEaseStartLevel,
                settings.TopEndGrowthMultiplier,
                settings.MaxDisplayLevel);
            var cached = _cycleCache;
            if (cached != null && cached.Key.Equals(key))
            {
                return cached.Length;
            }

            var length = Math.Max(1, GetLevelRange(settings.MaxDisplayLevel, settings).StartScore - 1);
            _cycleCache = new CycleCacheEntry(key, length);
            return length;
        }

        private struct CycleKey : IEquatable<CycleKey>
        {
            private readonly int _initial;
            private readonly int _growth;
            private readonly int _easeStart;
            private readonly double _multiplier;
            private readonly int _maxLevel;

            public CycleKey(int initial, int growth, int easeStart, double multiplier, int maxLevel)
            {
                _initial = initial;
                _growth = growth;
                _easeStart = easeStart;
                _multiplier = multiplier;
                _maxLevel = maxLevel;
            }

            public bool Equals(CycleKey other)
            {
                return _initial == other._initial &&
                    _growth == other._growth &&
                    _easeStart == other._easeStart &&
                    _multiplier.Equals(other._multiplier) &&
                    _maxLevel == other._maxLevel;
            }
        }

        private sealed class CycleCacheEntry
        {
            public CycleCacheEntry(CycleKey key, int length)
            {
                Key = key;
                Length = length;
            }

            public CycleKey Key { get; }

            public int Length { get; }
        }

        private static volatile CycleCacheEntry _cycleCache;

        private static AchievementLevelSnapshot CalculatePass(
            int safeScore,
            AchievementLevelCurveSettings settings)
        {
            var snapshot = new AchievementLevelSnapshot();
            var range = FindLevelRangeForScore(safeScore, settings);
            var rank = RankFromLevelValue(range.Level, settings);
            var maxInternalLevel = GetMaxInternalLevel(settings);
            var isMaxLevel = range.Level >= maxInternalLevel;

            snapshot.Level = range.Level;
            snapshot.DisplayLevel = settings.MaxDisplayLevel == int.MaxValue
                ? range.Level + 1
                : Math.Min(settings.MaxDisplayLevel, range.Level);
            snapshot.LevelProgress = isMaxLevel ? 100 : CalculateProgress(safeScore, range);
            snapshot.CurrentLevelStartScore = range.StartScore;
            snapshot.CurrentLevelEndScore = range.EndScore;
            snapshot.CurrentLevelTotalPoints = range.Size;
            snapshot.CurrentLevelPoints = CalculateCurrentLevelPoints(safeScore, range);
            snapshot.PointsUntilNextLevel = isMaxLevel
                ? 0
                : Math.Max(0, range.EndScore - safeScore + 1);
            snapshot.IsMaxLevel = isMaxLevel;
            snapshot.RankValue = rank;
            snapshot.Rank = rank.ToString();
            GetRankLevelBounds(range.Level, settings, out var rankStartLevel, out var rankEndLevel);
            snapshot.RankStartLevel = rankStartLevel;
            snapshot.RankEndLevel = rankEndLevel;
            snapshot.LevelsInRank = rankEndLevel == int.MaxValue
                ? 1
                : Math.Max(1, rankEndLevel - rankStartLevel + 1);
            snapshot.LevelsCompletedInRank = Math.Max(
                0,
                Math.Min(snapshot.LevelsInRank, range.Level - rankStartLevel));
            snapshot.LevelsUntilNextRank = isMaxLevel
                ? 0
                : snapshot.LevelsInRank - snapshot.LevelsCompletedInRank;
            if (!isMaxLevel && TryGetNextRankInfo(range.Level, settings, out var nextRank, out var nextRankStartLevel))
            {
                var nextRankRange = GetLevelRange(nextRankStartLevel, settings);
                snapshot.NextRankValue = nextRank;
                snapshot.NextRank = nextRank.ToString();
                snapshot.NextRankScoreThreshold = nextRankRange.StartScore;
                snapshot.PointsUntilNextRank = Math.Max(0, nextRankRange.StartScore - safeScore);
            }

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
            var normalizedLevel = Math.Min(GetMaxInternalLevel(settings), Math.Max(0, level));
            foreach (var threshold in GetOrderedRankThresholds(settings))
            {
                if (normalizedLevel <= threshold.MaxLevel)
                {
                    return threshold.Rank;
                }
            }

            var fallback = GetOrderedRankThresholds(settings).LastOrDefault();
            return fallback?.Rank ?? AchievementRank.Bronze5;
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
            var maxInternalLevel = GetMaxInternalLevel(settings);
            while (score > range.EndScore &&
                range.EndScore < int.MaxValue &&
                range.Level < maxInternalLevel)
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
            var targetLevel = Math.Min(GetMaxInternalLevel(settings), Math.Max(0, level));
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

        private static int CalculateCurrentLevelPoints(
            int score,
            AchievementLevelRange range)
        {
            if (score < range.StartScore)
            {
                return 0;
            }

            var clampedScore = Math.Min(score, range.EndScore);
            return Math.Max(0, Math.Min(range.Size, clampedScore - range.StartScore + 1));
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

        /// <summary>
        /// First and last level of the rank containing <paramref name="level"/>. A level past the
        /// final threshold (the cap level, when MaxDisplayLevel runs beyond the table) reports the
        /// final rank's own span so it reads as that rank completed rather than a rank of its own.
        /// </summary>
        private static void GetRankLevelBounds(
            int level,
            AchievementLevelCurveSettings settings,
            out int startLevel,
            out int endLevel)
        {
            var thresholds = GetOrderedRankThresholds(settings);
            var normalizedLevel = Math.Min(GetMaxInternalLevel(settings), Math.Max(0, level));
            var start = 0;

            for (var i = 0; i < thresholds.Count; i++)
            {
                var maxLevel = thresholds[i].MaxLevel;
                if (normalizedLevel <= maxLevel || i == thresholds.Count - 1)
                {
                    startLevel = start;
                    endLevel = Math.Max(start, maxLevel);
                    return;
                }

                start = AddClamped(maxLevel, 1);
            }

            startLevel = 0;
            endLevel = normalizedLevel;
        }

        private static bool TryGetNextRankInfo(
            int currentLevel,
            AchievementLevelCurveSettings settings,
            out AchievementRank nextRank,
            out int nextRankStartLevel)
        {
            var thresholds = GetOrderedRankThresholds(settings);
            var normalizedLevel = Math.Min(GetMaxInternalLevel(settings), Math.Max(0, currentLevel));
            for (var i = 0; i < thresholds.Count; i++)
            {
                var threshold = thresholds[i];
                if (normalizedLevel > threshold.MaxLevel)
                {
                    continue;
                }

                if (i >= thresholds.Count - 1 || threshold.MaxLevel == int.MaxValue)
                {
                    break;
                }

                nextRankStartLevel = threshold.MaxLevel + 1;
                if (nextRankStartLevel > GetMaxInternalLevel(settings))
                {
                    break;
                }

                nextRank = thresholds[i + 1].Rank;
                return true;
            }

            nextRank = AchievementRank.Bronze5;
            nextRankStartLevel = 0;
            return false;
        }

        private static int GetMaxInternalLevel(AchievementLevelCurveSettings settings)
        {
            if (settings.MaxDisplayLevel == int.MaxValue)
            {
                return int.MaxValue;
            }

            return Math.Max(0, settings.MaxDisplayLevel);
        }

        private static int AddClamped(int current, int value)
        {
            var result = (long)current + value;
            return result > int.MaxValue ? int.MaxValue : (int)result;
        }

        private static int ClampToInt(long value)
        {
            return value > int.MaxValue ? int.MaxValue : (int)Math.Max(0, value);
        }
    }
}
