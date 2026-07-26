using System;
using System.Collections.Generic;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.Services.Summaries
{
    internal sealed class AchievementGameStats
    {
        public int TotalAchievements { get; set; }
        public int UnlockedAchievements { get; set; }

        public int CommonCount { get; set; }
        public int UncommonCount { get; set; }
        public int RareCount { get; set; }
        public int UltraRareCount { get; set; }

        public int TotalCommonPossible { get; set; }
        public int TotalUncommonPossible { get; set; }
        public int TotalRarePossible { get; set; }
        public int TotalUltraRarePossible { get; set; }

        public int TrophyPlatinumCount { get; set; }
        public int TrophyGoldCount { get; set; }
        public int TrophySilverCount { get; set; }
        public int TrophyBronzeCount { get; set; }

        public int TrophyPlatinumTotal { get; set; }
        public int TrophyGoldTotal { get; set; }
        public int TrophySilverTotal { get; set; }
        public int TrophyBronzeTotal { get; set; }

        public int CollectionScore { get; set; }
        public int PrestigeScore { get; set; }
        public int CollectionScoreTotal { get; set; }
        public int PrestigeScoreTotal { get; set; }
        public int Points { get; set; }

        public DateTime? LastUnlockUtc { get; set; }

        public Dictionary<DateTime, int> UnlockCountsByDate { get; } =
            new Dictionary<DateTime, int>();

        public int LockedAchievements => Math.Max(0, TotalAchievements - UnlockedAchievements);

        public int ProgressPercent =>
            AchievementCompletionPercentCalculator.ComputeRoundedPercent(
                UnlockedAchievements,
                TotalAchievements);

        public AchievementRarityStats CommonStats => CreateRarityStats(CommonCount, TotalCommonPossible);
        public AchievementRarityStats UncommonStats => CreateRarityStats(UncommonCount, TotalUncommonPossible);
        public AchievementRarityStats RareStats => CreateRarityStats(RareCount, TotalRarePossible);
        public AchievementRarityStats UltraRareStats => CreateRarityStats(UltraRareCount, TotalUltraRarePossible);

        public void AddTo(AchievementGameStats target)
        {
            if (target == null)
            {
                return;
            }

            target.TotalAchievements = AddClamped(target.TotalAchievements, TotalAchievements);
            target.UnlockedAchievements = AddClamped(target.UnlockedAchievements, UnlockedAchievements);
            target.CommonCount = AddClamped(target.CommonCount, CommonCount);
            target.UncommonCount = AddClamped(target.UncommonCount, UncommonCount);
            target.RareCount = AddClamped(target.RareCount, RareCount);
            target.UltraRareCount = AddClamped(target.UltraRareCount, UltraRareCount);
            target.TotalCommonPossible = AddClamped(target.TotalCommonPossible, TotalCommonPossible);
            target.TotalUncommonPossible = AddClamped(target.TotalUncommonPossible, TotalUncommonPossible);
            target.TotalRarePossible = AddClamped(target.TotalRarePossible, TotalRarePossible);
            target.TotalUltraRarePossible = AddClamped(target.TotalUltraRarePossible, TotalUltraRarePossible);
            target.TrophyPlatinumCount = AddClamped(target.TrophyPlatinumCount, TrophyPlatinumCount);
            target.TrophyGoldCount = AddClamped(target.TrophyGoldCount, TrophyGoldCount);
            target.TrophySilverCount = AddClamped(target.TrophySilverCount, TrophySilverCount);
            target.TrophyBronzeCount = AddClamped(target.TrophyBronzeCount, TrophyBronzeCount);
            target.TrophyPlatinumTotal = AddClamped(target.TrophyPlatinumTotal, TrophyPlatinumTotal);
            target.TrophyGoldTotal = AddClamped(target.TrophyGoldTotal, TrophyGoldTotal);
            target.TrophySilverTotal = AddClamped(target.TrophySilverTotal, TrophySilverTotal);
            target.TrophyBronzeTotal = AddClamped(target.TrophyBronzeTotal, TrophyBronzeTotal);
            target.CollectionScore = AddClamped(target.CollectionScore, CollectionScore);
            target.PrestigeScore = AddClamped(target.PrestigeScore, PrestigeScore);
            target.CollectionScoreTotal = AddClamped(target.CollectionScoreTotal, CollectionScoreTotal);
            target.PrestigeScoreTotal = AddClamped(target.PrestigeScoreTotal, PrestigeScoreTotal);
            target.Points = AddClamped(target.Points, Points);

            if (LastUnlockUtc.HasValue &&
                (!target.LastUnlockUtc.HasValue || LastUnlockUtc.Value > target.LastUnlockUtc.Value))
            {
                target.LastUnlockUtc = LastUnlockUtc.Value;
            }

            foreach (var kvp in UnlockCountsByDate)
            {
                IncrementBy(target.UnlockCountsByDate, kvp.Key, kvp.Value);
            }
        }

        public void ApplyTo(GameSummaryItem item)
        {
            if (item == null)
            {
                return;
            }

            item.TotalAchievements = TotalAchievements;
            item.UnlockedAchievements = UnlockedAchievements;
            item.CommonCount = CommonCount;
            item.UncommonCount = UncommonCount;
            item.RareCount = RareCount;
            item.UltraRareCount = UltraRareCount;
            item.CollectionScore = CollectionScore;
            item.PrestigeScore = PrestigeScore;
            item.CollectionScoreTotal = CollectionScoreTotal;
            item.PrestigeScoreTotal = PrestigeScoreTotal;
            item.Points = Points;
            item.TotalCommonPossible = TotalCommonPossible;
            item.TotalUncommonPossible = TotalUncommonPossible;
            item.TotalRarePossible = TotalRarePossible;
            item.TotalUltraRarePossible = TotalUltraRarePossible;
            item.TrophyPlatinumCount = TrophyPlatinumCount;
            item.TrophyGoldCount = TrophyGoldCount;
            item.TrophySilverCount = TrophySilverCount;
            item.TrophyBronzeCount = TrophyBronzeCount;
            item.TrophyPlatinumTotal = TrophyPlatinumTotal;
            item.TrophyGoldTotal = TrophyGoldTotal;
            item.TrophySilverTotal = TrophySilverTotal;
            item.TrophyBronzeTotal = TrophyBronzeTotal;
            item.LastUnlockUtc = LastUnlockUtc;
        }

        public static AchievementRarityStats CreateRarityStats(int unlocked, int total)
        {
            var normalizedTotal = Math.Max(0, total);
            var normalizedUnlocked = Math.Max(0, Math.Min(unlocked, normalizedTotal));
            return new AchievementRarityStats
            {
                Total = normalizedTotal,
                Unlocked = normalizedUnlocked,
                Locked = normalizedTotal - normalizedUnlocked
            };
        }

        public static int AddClamped(int current, int value)
        {
            if (value <= 0)
            {
                return current;
            }

            return current > int.MaxValue - value
                ? int.MaxValue
                : current + value;
        }

        internal static void IncrementBy(Dictionary<DateTime, int> dict, DateTime date, int count)
        {
            if (dict == null || count <= 0)
            {
                return;
            }

            if (dict.TryGetValue(date, out var existing))
            {
                dict[date] = AddClamped(existing, count);
            }
            else
            {
                dict[date] = count;
            }
        }

        internal static DateTime NormalizeUtc(DateTime timestamp)
        {
            return DateTimeUtilities.AsUtcKind(timestamp);
        }
    }
}
