using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services;
using PlayniteAchievements.ViewModels;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;

namespace PlayniteAchievements.Services.Tests
{
    [TestClass]
    public class AchievementGridSortHelperTests
    {
        [TestMethod]
        public void UnlockTime_TieBreaksByRarityBeforeTrophyType()
        {
            var unlockTime = DateTime.SpecifyKind(new DateTime(2026, 3, 1, 12, 0, 0), DateTimeKind.Utc);
            var items = new List<AchievementDisplayItem>
            {
                CreateItem("Gold Common", unlockTime, raritySortValue: 80, trophyType: "gold", points: 10),
                CreateItem("Bronze Rare", unlockTime, raritySortValue: 8, trophyType: "bronze", points: 10)
            };

            string sortPath = null;
            ListSortDirection? sortDirection = null;

            var handled = AchievementGridSortHelper.TrySortItems(
                items,
                "UnlockTime",
                ListSortDirection.Descending,
                AchievementGridSortScope.GameAchievements,
                ref sortPath,
                ref sortDirection);

            Assert.IsTrue(handled);
            CollectionAssert.AreEqual(
                new[] { "Bronze Rare", "Gold Common" },
                items.Select(item => item.DisplayName).ToArray());
        }

        [TestMethod]
        public void TrophyType_TieBreaksByRarity()
        {
            var items = new List<AchievementDisplayItem>
            {
                CreateItem("Gold Common", DateTime.SpecifyKind(new DateTime(2026, 3, 1, 12, 0, 0), DateTimeKind.Utc), raritySortValue: 80, trophyType: "gold", points: 10),
                CreateItem("Gold Rare", DateTime.SpecifyKind(new DateTime(2026, 3, 2, 12, 0, 0), DateTimeKind.Utc), raritySortValue: 8, trophyType: "gold", points: 10),
                CreateItem("Silver Common", DateTime.SpecifyKind(new DateTime(2026, 3, 3, 12, 0, 0), DateTimeKind.Utc), raritySortValue: 90, trophyType: "silver", points: 10)
            };

            string sortPath = null;
            ListSortDirection? sortDirection = null;

            var handled = AchievementGridSortHelper.TrySortItems(
                items,
                "TrophyType",
                ListSortDirection.Descending,
                AchievementGridSortScope.GameAchievements,
                ref sortPath,
                ref sortDirection);

            Assert.IsTrue(handled);
            CollectionAssert.AreEqual(
                new[] { "Gold Rare", "Gold Common", "Silver Common" },
                items.Select(item => item.DisplayName).ToArray());
        }

        private static AchievementDisplayItem CreateItem(
            string displayName,
            DateTime unlockTimeUtc,
            double raritySortValue,
            string trophyType,
            int points)
        {
            return new AchievementDisplayItem
            {
                DisplayName = displayName,
                UnlockTimeUtc = unlockTimeUtc,
                RaritySortValue = raritySortValue,
                TrophyType = trophyType,
                PointsValue = points,
                Unlocked = true
            };
        }
    }
}
