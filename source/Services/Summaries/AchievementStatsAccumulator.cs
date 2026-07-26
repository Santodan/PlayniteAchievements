using System;
using System.Collections.Generic;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.Services.Summaries
{
    internal static class AchievementStatsAccumulator
    {
        public static AchievementGameStats FromAchievements(IEnumerable<AchievementDetail> achievements)
        {
            var stats = new AchievementGameStats();
            foreach (var achievement in achievements ?? Array.Empty<AchievementDetail>())
            {
                Add(stats, achievement);
            }

            return stats;
        }

        public static AchievementGameStats FromDisplayItems(
            IEnumerable<AchievementDisplayItem> achievements,
            bool treatItemsAsUnlocked = false)
        {
            var stats = new AchievementGameStats();
            foreach (var achievement in achievements ?? Array.Empty<AchievementDisplayItem>())
            {
                Add(stats, achievement, treatItemsAsUnlocked);
            }

            return stats;
        }

        public static void Add(AchievementGameStats stats, AchievementDetail achievement)
        {
            if (stats == null || achievement == null)
            {
                return;
            }

            stats.TotalAchievements++;
            AddRarityTotal(stats, achievement.Rarity);
            stats.CollectionScoreTotal = AchievementGameStats.AddClamped(
                stats.CollectionScoreTotal,
                achievement.CollectionScore);
            stats.PrestigeScoreTotal = AchievementGameStats.AddClamped(
                stats.PrestigeScoreTotal,
                achievement.PrestigeScore);
            AddTrophyTotal(stats, achievement.TrophyType);

            if (!achievement.Unlocked)
            {
                return;
            }

            AddUnlocked(
                stats,
                achievement.Rarity,
                achievement.TrophyType,
                achievement.CollectionScore,
                achievement.PrestigeScore,
                achievement.Points ?? 0,
                achievement.UnlockTimeUtc);
        }

        public static void Add(
            AchievementGameStats stats,
            AchievementDisplayItem achievement,
            bool treatItemAsUnlocked = false)
        {
            if (stats == null || achievement == null)
            {
                return;
            }

            stats.TotalAchievements++;
            AddRarityTotal(stats, achievement.Rarity);
            stats.CollectionScoreTotal = AchievementGameStats.AddClamped(
                stats.CollectionScoreTotal,
                achievement.CollectionScore);
            stats.PrestigeScoreTotal = AchievementGameStats.AddClamped(
                stats.PrestigeScoreTotal,
                achievement.PrestigeScore);
            AddTrophyTotal(stats, achievement.TrophyType);

            if (!treatItemAsUnlocked && !achievement.Unlocked)
            {
                return;
            }

            AddUnlocked(
                stats,
                achievement.Rarity,
                achievement.TrophyType,
                achievement.CollectionScore,
                achievement.PrestigeScore,
                achievement.Points,
                achievement.UnlockTimeUtc);
        }

        public static void AddRarityStats(AchievementRarityStats target, AchievementRarityStats source)
        {
            if (target == null || source == null)
            {
                return;
            }

            target.Total = AchievementGameStats.AddClamped(target.Total, source.Total);
            target.Unlocked = AchievementGameStats.AddClamped(target.Unlocked, source.Unlocked);
            target.Locked = AchievementGameStats.AddClamped(target.Locked, source.Locked);
        }

        private static void AddUnlocked(
            AchievementGameStats stats,
            RarityTier rarity,
            string trophyType,
            int collectionScore,
            int prestigeScore,
            int points,
            DateTime? unlockTimeUtc)
        {
            stats.UnlockedAchievements++;
            AddRarityUnlocked(stats, rarity);
            AddTrophyUnlocked(stats, trophyType);
            stats.CollectionScore = AchievementGameStats.AddClamped(stats.CollectionScore, collectionScore);
            stats.PrestigeScore = AchievementGameStats.AddClamped(stats.PrestigeScore, prestigeScore);
            stats.Points = AchievementGameStats.AddClamped(stats.Points, points);

            if (!unlockTimeUtc.HasValue)
            {
                return;
            }

            var normalized = AchievementGameStats.NormalizeUtc(unlockTimeUtc.Value);
            if (!stats.LastUnlockUtc.HasValue || normalized > stats.LastUnlockUtc.Value)
            {
                stats.LastUnlockUtc = normalized;
            }

            AchievementGameStats.IncrementBy(stats.UnlockCountsByDate, normalized.Date, 1);
        }

        private static void AddRarityTotal(AchievementGameStats stats, RarityTier rarity)
        {
            switch (rarity)
            {
                case RarityTier.UltraRare:
                    stats.TotalUltraRarePossible++;
                    break;
                case RarityTier.Rare:
                    stats.TotalRarePossible++;
                    break;
                case RarityTier.Uncommon:
                    stats.TotalUncommonPossible++;
                    break;
                default:
                    stats.TotalCommonPossible++;
                    break;
            }
        }

        private static void AddRarityUnlocked(AchievementGameStats stats, RarityTier rarity)
        {
            switch (rarity)
            {
                case RarityTier.UltraRare:
                    stats.UltraRareCount++;
                    break;
                case RarityTier.Rare:
                    stats.RareCount++;
                    break;
                case RarityTier.Uncommon:
                    stats.UncommonCount++;
                    break;
                default:
                    stats.CommonCount++;
                    break;
            }
        }

        private static void AddTrophyTotal(AchievementGameStats stats, string trophyType)
        {
            switch (NormalizeTrophyType(trophyType))
            {
                case "platinum":
                    stats.TrophyPlatinumTotal++;
                    break;
                case "gold":
                    stats.TrophyGoldTotal++;
                    break;
                case "silver":
                    stats.TrophySilverTotal++;
                    break;
                case "bronze":
                    stats.TrophyBronzeTotal++;
                    break;
            }
        }

        private static void AddTrophyUnlocked(AchievementGameStats stats, string trophyType)
        {
            switch (NormalizeTrophyType(trophyType))
            {
                case "platinum":
                    stats.TrophyPlatinumCount++;
                    break;
                case "gold":
                    stats.TrophyGoldCount++;
                    break;
                case "silver":
                    stats.TrophySilverCount++;
                    break;
                case "bronze":
                    stats.TrophyBronzeCount++;
                    break;
            }
        }

        private static string NormalizeTrophyType(string trophyType)
        {
            return string.IsNullOrWhiteSpace(trophyType)
                ? null
                : trophyType.Trim().ToLowerInvariant();
        }
    }
}
