using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Services.Summaries;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.Tests.Services.Summaries
{
    [TestClass]
    public class AchievementStatsAccumulatorTests
    {
        [TestMethod]
        public void FromAchievements_AccumulatesCountsScoresTrophiesAndTimeline()
        {
            var firstUnlock = Utc(2026, 5, 1, 12);
            var latestUnlock = Utc(2026, 5, 2, 9);
            var achievements = new List<AchievementDetail>
            {
                Achievement(RarityTier.Common, unlocked: true, percent: 75, trophy: "bronze", points: 10, firstUnlock),
                Achievement(RarityTier.Uncommon, unlocked: false, percent: 35, trophy: "silver", points: 20, null),
                Achievement(RarityTier.Rare, unlocked: true, percent: 8, trophy: "gold", points: 30, latestUnlock),
                Achievement(RarityTier.UltraRare, unlocked: false, percent: 2, trophy: "platinum", points: 40, null)
            };
            var expectedCollectionScore = achievements[0].CollectionScore + achievements[2].CollectionScore;
            var expectedPrestigeScore = achievements[0].PrestigeScore + achievements[2].PrestigeScore;
            var expectedCollectionScoreTotal = 0;
            var expectedPrestigeScoreTotal = 0;
            foreach (var achievement in achievements)
            {
                expectedCollectionScoreTotal += achievement.CollectionScore;
                expectedPrestigeScoreTotal += achievement.PrestigeScore;
            }

            var stats = AchievementStatsAccumulator.FromAchievements(achievements);

            Assert.AreEqual(4, stats.TotalAchievements);
            Assert.AreEqual(2, stats.UnlockedAchievements);
            Assert.AreEqual(1, stats.CommonCount);
            Assert.AreEqual(0, stats.UncommonCount);
            Assert.AreEqual(1, stats.RareCount);
            Assert.AreEqual(0, stats.UltraRareCount);
            Assert.AreEqual(1, stats.TotalCommonPossible);
            Assert.AreEqual(1, stats.TotalUncommonPossible);
            Assert.AreEqual(1, stats.TotalRarePossible);
            Assert.AreEqual(1, stats.TotalUltraRarePossible);
            Assert.AreEqual(0, stats.TrophyPlatinumCount);
            Assert.AreEqual(1, stats.TrophyGoldCount);
            Assert.AreEqual(0, stats.TrophySilverCount);
            Assert.AreEqual(1, stats.TrophyBronzeCount);
            Assert.AreEqual(1, stats.TrophyPlatinumTotal);
            Assert.AreEqual(1, stats.TrophyGoldTotal);
            Assert.AreEqual(1, stats.TrophySilverTotal);
            Assert.AreEqual(1, stats.TrophyBronzeTotal);
            Assert.AreEqual(expectedCollectionScore, stats.CollectionScore);
            Assert.AreEqual(expectedPrestigeScore, stats.PrestigeScore);
            Assert.AreEqual(expectedCollectionScoreTotal, stats.CollectionScoreTotal);
            Assert.AreEqual(expectedPrestigeScoreTotal, stats.PrestigeScoreTotal);
            Assert.AreEqual(10 + 30, stats.Points);
            Assert.AreEqual(latestUnlock, stats.LastUnlockUtc);
            Assert.AreEqual(1, stats.UnlockCountsByDate[firstUnlock.Date]);
            Assert.AreEqual(1, stats.UnlockCountsByDate[latestUnlock.Date]);
            Assert.AreEqual(50, stats.ProgressPercent);
        }

        [TestMethod]
        public void FromDisplayItems_TreatItemsAsUnlockedAccumulatesFriendSummaryStats()
        {
            var items = new List<AchievementDisplayItem>
            {
                DisplayItem(RarityTier.Rare, unlocked: false, percent: 8, trophy: "gold", points: 25, unlockTimeUtc: null),
                DisplayItem(RarityTier.Common, unlocked: true, percent: 75, trophy: "bronze", points: 10, Utc(2026, 4, 2, 8))
            };
            var lockedRare = items[0];
            var unlockedCommon = items[1];

            var stats = AchievementStatsAccumulator.FromDisplayItems(items, treatItemsAsUnlocked: true);

            Assert.AreEqual(2, stats.TotalAchievements);
            Assert.AreEqual(2, stats.UnlockedAchievements);
            Assert.AreEqual(1, stats.CommonCount);
            Assert.AreEqual(1, stats.RareCount);
            Assert.AreEqual(1, stats.TrophyGoldCount);
            Assert.AreEqual(1, stats.TrophyBronzeCount);
            Assert.AreEqual(lockedRare.CollectionScore + unlockedCommon.CollectionScore, stats.CollectionScore);
            Assert.AreEqual(lockedRare.PrestigeScore + unlockedCommon.PrestigeScore, stats.PrestigeScore);
            Assert.AreEqual(35, stats.Points);
        }

        [TestMethod]
        public void ApplyTo_GameSummaryItemCopiesStats()
        {
            var achievements = new List<AchievementDetail>
            {
                Achievement(RarityTier.UltraRare, unlocked: true, percent: 0.5, trophy: "platinum", points: 50, Utc(2026, 6, 1, 10)),
                Achievement(RarityTier.Common, unlocked: false, percent: 80, trophy: "bronze", points: 10, null)
            };
            var stats = AchievementStatsAccumulator.FromAchievements(achievements);
            var item = new GameSummaryItem();

            stats.ApplyTo(item);

            Assert.AreEqual(2, item.TotalAchievements);
            Assert.AreEqual(1, item.UnlockedAchievements);
            Assert.AreEqual(1, item.UltraRareCount);
            Assert.AreEqual(1, item.TotalCommonPossible);
            Assert.AreEqual(1, item.TrophyPlatinumCount);
            Assert.AreEqual(1, item.TrophyBronzeTotal);
            Assert.AreEqual(50, item.Points);
            Assert.AreEqual(stats.CollectionScore, item.CollectionScore);
            Assert.AreEqual(stats.PrestigeScore, item.PrestigeScore);
            Assert.AreEqual(stats.LastUnlockUtc, item.LastUnlockUtc);
        }

        private static AchievementDetail Achievement(
            RarityTier rarity,
            bool unlocked,
            double? percent,
            string trophy,
            int points,
            DateTime? unlockTimeUtc)
        {
            return new AchievementDetail
            {
                ApiName = Guid.NewGuid().ToString("N"),
                DisplayName = "Achievement",
                Rarity = rarity,
                GlobalPercentUnlocked = percent,
                TrophyType = trophy,
                Points = points,
                Unlocked = unlocked,
                UnlockTimeUtc = unlockTimeUtc
            };
        }

        private static AchievementDisplayItem DisplayItem(
            RarityTier rarity,
            bool unlocked,
            double? percent,
            string trophy,
            int points,
            DateTime? unlockTimeUtc)
        {
            return new AchievementDisplayItem
            {
                Rarity = rarity,
                GlobalPercentUnlocked = percent,
                TrophyType = trophy,
                PointsValue = points,
                Unlocked = unlocked,
                UnlockTimeUtc = unlockTimeUtc
            };
        }

        private static DateTime Utc(int year, int month, int day, int hour)
        {
            return new DateTime(year, month, day, hour, 0, 0, DateTimeKind.Utc);
        }
    }
}
