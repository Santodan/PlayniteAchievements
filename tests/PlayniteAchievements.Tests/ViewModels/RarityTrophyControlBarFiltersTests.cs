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
            filters.UpdateGames(new[] { new GameSummaryItem { TrophyBronzeTotal = 1 } });

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

            var goldCapstone = new AchievementTally(null, RarityTier.Rare, "gold", true, 1, 1);
            var gold = new AchievementTally(null, RarityTier.Rare, "gold", false, 1, 1);
            Assert.IsTrue(filters.MatchesRarity(goldCapstone));
            Assert.IsFalse(filters.MatchesRarity(gold));
            Assert.IsTrue(filters.MatchesTrophy(gold), "no trophy picked keeps every row");

            filters.SetTrophySelected(L("LOCPlayAch_Trophy_Silver"), true);
            Assert.IsTrue(filters.MatchesRarity(goldCapstone));
            Assert.IsFalse(filters.MatchesTrophy(goldCapstone));
            Assert.IsFalse(filters.Matches(goldCapstone));
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
        public void UpdateGames_OffersTrophiesOnlyWhileAGameHasThem()
        {
            var filters = new RarityTrophyControlBarFilters(null);
            Assert.IsFalse(filters.UpdateGames(new[] { new GameSummaryItem() }));
            Assert.AreEqual(0, filters.TrophyOptions.Count);

            Assert.IsTrue(filters.UpdateGames(new[] { new GameSummaryItem { TrophyGoldTotal = 1 } }));
            Assert.AreEqual(4, filters.TrophyOptions.Count);
            Assert.IsFalse(filters.UpdateGames(new[] { new GameSummaryItem { TrophySilverTotal = 2 } }), "still offered");

            Assert.IsTrue(filters.UpdateGames(null));
            Assert.AreEqual(0, filters.TrophyOptions.Count);
        }

        [TestMethod]
        public void ApplyTo_NarrowsTheTiersToCapstones_OrJoinsThemWhenAsked()
        {
            var filters = new RarityTrophyControlBarFilters(null);
            filters.SetRaritySelected(L("LOCPlayAch_Rarity_Rare"), true);
            filters.SetRaritySelected(L("LOCPlayAch_Dynamic_Capstone"), true);
            filters.SetTrophySelected(L("LOCPlayAch_Trophy_Gold"), true);

            var narrowed = filters.ApplyTo(PlayniteAchievements.Services.Database.AchievementQuery.CurrentUser);
            CollectionAssert.AreEqual(new[] { RarityTier.Rare }, narrowed.Rarities.ToArray());
            Assert.IsTrue(narrowed.CapstonesOnly);
            Assert.IsFalse(narrowed.CapstoneOrRarity);
            CollectionAssert.AreEqual(new[] { "gold" }, narrowed.TrophyTypes.ToArray());

            filters.CapstoneJoinsTiers = true;
            var joined = filters.ApplyTo(PlayniteAchievements.Services.Database.AchievementQuery.CurrentUser);
            Assert.IsTrue(joined.CapstonesOnly && joined.CapstoneOrRarity);
            CollectionAssert.AreEqual(new[] { RarityTier.Rare }, joined.Rarities.ToArray());
        }
    }
}
