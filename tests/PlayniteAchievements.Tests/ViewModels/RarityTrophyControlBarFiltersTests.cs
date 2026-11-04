using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.Tests.ViewModels
{
    [TestClass]
    public class RarityTrophyControlBarFiltersTests
    {
        private static string L(string key) => Playnite.SDK.ResourceProvider.GetString(key);

        [TestMethod]
        public void SelectedLabels_FollowMenuOrder_WithCapstoneLast()
        {
            var filters = new RarityTrophyControlBarFilters(null);
            filters.UpdateRows(new[] { new AchievementDisplayItem { TrophyType = "bronze" } });

            filters.SetRaritySelected(L("LOCPlayAch_Dynamic_Capstone"), true);
            filters.SetRaritySelected(L("LOCPlayAch_Rarity_UltraRare"), true);
            filters.SetRaritySelected(L("LOCPlayAch_Rarity_Common"), true);
            filters.SetTrophySelected(L("LOCPlayAch_Trophy_Bronze"), true);
            filters.SetTrophySelected(L("LOCPlayAch_Trophy_Platinum"), true);

            CollectionAssert.AreEqual(
                new[] { L("LOCPlayAch_Rarity_Common"), L("LOCPlayAch_Rarity_UltraRare"), L("LOCPlayAch_Dynamic_Capstone") },
                filters.SelectedRarityLabels.ToArray());
            CollectionAssert.AreEqual(
                new[] { L("LOCPlayAch_Trophy_Platinum"), L("LOCPlayAch_Trophy_Bronze") },
                filters.SelectedTrophyLabels.ToArray());
        }

        [TestMethod]
        public void RarityAndTrophy_MatchIndependently_AndReportTheirOwnSelection()
        {
            var filters = new RarityTrophyControlBarFilters(null);
            Assert.IsFalse(filters.HasRaritySelection);
            Assert.IsFalse(filters.HasTrophySelection);

            filters.SetRaritySelected(L("LOCPlayAch_Dynamic_Capstone"), true);
            Assert.IsTrue(filters.HasRaritySelection);
            Assert.IsFalse(filters.HasTrophySelection);
            Assert.IsTrue(filters.HasSelection);

            var goldCapstone = new AchievementDisplayItem { Rarity = RarityTier.Rare, IsCapstone = true, TrophyType = "gold" };
            var gold = new AchievementDisplayItem { Rarity = RarityTier.Rare, TrophyType = "gold" };
            Assert.IsTrue(filters.MatchesRarity(goldCapstone));
            Assert.IsFalse(filters.MatchesRarity(gold));
            Assert.IsTrue(filters.MatchesTrophy(gold), "no trophy picked keeps every row");

            filters.SetTrophySelected(L("LOCPlayAch_Trophy_Silver"), true);
            Assert.IsTrue(filters.MatchesRarity(goldCapstone));
            Assert.IsFalse(filters.MatchesTrophy(goldCapstone));
            Assert.IsFalse(filters.Matches(goldCapstone));
            Assert.IsFalse(filters.MatchesRarity(null));
        }

        [TestMethod]
        public void Keys_AreStableForTheSamePicks_AndEmptyForNone()
        {
            var first = new RarityTrophyControlBarFilters(null);
            var second = new RarityTrophyControlBarFilters(null);
            Assert.AreEqual(string.Empty, first.RarityKey);
            Assert.AreEqual(string.Empty, first.TrophyKey);

            first.SetRaritySelected(L("LOCPlayAch_Rarity_Rare"), true);
            first.SetRaritySelected(L("LOCPlayAch_Dynamic_Capstone"), true);
            second.SetRaritySelected(L("LOCPlayAch_Dynamic_Capstone"), true);
            second.SetRaritySelected(L("LOCPlayAch_Rarity_Rare"), true);
            Assert.AreEqual(first.RarityKey, second.RarityKey);

            second.SetRaritySelected(L("LOCPlayAch_Dynamic_Capstone"), false);
            Assert.AreNotEqual(first.RarityKey, second.RarityKey, "capstone is part of the key");

            first.SetTrophySelected(L("LOCPlayAch_Trophy_Gold"), true);
            first.SetTrophySelected(L("LOCPlayAch_Trophy_Bronze"), true);
            second.SetTrophySelected(L("LOCPlayAch_Trophy_Bronze"), true);
            second.SetTrophySelected(L("LOCPlayAch_Trophy_Gold"), true);
            Assert.AreEqual(first.TrophyKey, second.TrophyKey);
        }

        [TestMethod]
        public void Clear_DropsEveryPick_OneChangeNoticeEach()
        {
            var changes = 0;
            var filters = new RarityTrophyControlBarFilters(() => changes++);
            filters.SetRaritySelected(L("LOCPlayAch_Rarity_Rare"), true);
            filters.SetRaritySelected(L("LOCPlayAch_Dynamic_Capstone"), true);
            filters.SetTrophySelected(L("LOCPlayAch_Trophy_Gold"), true);
            Assert.AreEqual(3, changes);

            filters.ClearRarity();
            Assert.AreEqual(5, changes);
            Assert.IsFalse(filters.HasRaritySelection);
            Assert.IsTrue(filters.HasTrophySelection);

            filters.ClearTrophy();
            Assert.AreEqual(6, changes);
            Assert.IsFalse(filters.HasSelection);

            filters.ClearRarity();
            Assert.AreEqual(6, changes, "nothing to drop, nothing raised");
        }

        [TestMethod]
        public void UpdateRows_OffersTrophiesOnlyWhileARowHasAType()
        {
            var filters = new RarityTrophyControlBarFilters(null);
            Assert.IsFalse(filters.UpdateRows(new[] { new AchievementDisplayItem { TrophyType = " " } }));
            Assert.AreEqual(0, filters.TrophyOptions.Count);

            Assert.IsTrue(filters.UpdateRows(new[] { new AchievementDisplayItem { TrophyType = "gold" } }));
            Assert.AreEqual(4, filters.TrophyOptions.Count);
            Assert.IsFalse(filters.UpdateRows(new[] { new AchievementDisplayItem { TrophyType = "silver" } }), "still offered");

            Assert.IsTrue(filters.UpdateRows(null));
            Assert.AreEqual(0, filters.TrophyOptions.Count);
        }
    }
}
