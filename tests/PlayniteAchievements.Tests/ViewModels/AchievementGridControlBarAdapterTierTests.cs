using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.Tests.ViewModels
{
    [TestClass]
    public class AchievementGridControlBarAdapterTierTests
    {
        [TestMethod]
        public void RarityItem_OffersTheTiersThenCapstone_WithBadges()
        {
            var adapter = new AchievementGridControlBarAdapter();
            var rarity = adapter.TierFilters.RarityItem;
            var options = rarity.Options.ToList();

            Assert.AreEqual(5, options.Count);
            Assert.AreEqual(
                Playnite.SDK.ResourceProvider.GetString("LOCPlayAch_Dynamic_Capstone"),
                options[4],
                "Capstone is the last option");
            Assert.AreEqual("BadgeRarityCompleted", rarity.IconKeyOf(options[4]));
            Assert.AreEqual("BadgeRarityRare", rarity.IconKeyOf(options[2]));
        }

        [TestMethod]
        public void Apply_CapstoneNarrowsThePickedTiers_OrKeepsEveryCapstoneAlone()
        {
            var adapter = new AchievementGridControlBarAdapter();
            var rareCapstone = new AchievementDisplayItem { Rarity = RarityTier.Rare, IsCapstone = true };
            var commonCapstone = new AchievementDisplayItem { Rarity = RarityTier.Common, IsCapstone = true };
            var rare = new AchievementDisplayItem { Rarity = RarityTier.Rare };
            var rows = new[] { rareCapstone, commonCapstone, rare };
            var options = adapter.TierFilters.RarityItem.Options.ToList();
            var changes = 0;
            adapter.FilterChanged += (_, __) => changes++;

            adapter.TierFilters.SetRaritySelected(options[4], true);
            CollectionAssert.AreEqual(new[] { rareCapstone, commonCapstone }, adapter.Apply(rows).ToArray());
            Assert.IsTrue(adapter.HasActiveFilters);

            adapter.TierFilters.SetRaritySelected(options[2], true);
            CollectionAssert.AreEqual(new[] { rareCapstone }, adapter.Apply(rows).ToArray(), "capstone narrows the tiers");

            adapter.TierFilters.SetRaritySelected(options[4], false);
            CollectionAssert.AreEqual(new[] { rareCapstone, rare }, adapter.Apply(rows).ToArray());
            Assert.AreEqual(3, changes, "each pick raises FilterChanged once");
        }

        [TestMethod]
        public void TrophyItem_IsOfferedOnlyWhileSomeRowHasATrophyType()
        {
            var adapter = new AchievementGridControlBarAdapter();
            Assert.AreEqual(0, adapter.TierFilters.TrophyOptions.Count);

            adapter.UpdateOptions(new[] { new AchievementDisplayItem { TrophyType = "gold" } });
            Assert.AreEqual(4, adapter.TierFilters.TrophyOptions.Count);

            adapter.UpdateOptions(null);
            Assert.AreEqual(0, adapter.TierFilters.TrophyOptions.Count, "no rows, no trophy filter");

            adapter.UpdateOptions(new[] { new AchievementDisplayItem { Rarity = RarityTier.Rare } });
            Assert.AreEqual(0, adapter.TierFilters.TrophyOptions.Count);
            Assert.AreEqual(5, adapter.TierFilters.RarityOptions.Count, "rarity is always offered");
        }

        [TestMethod]
        public void State_RoundTripsTheTierPicks_AndResetClearsThem()
        {
            var adapter = new AchievementGridControlBarAdapter();
            var options = adapter.TierFilters.RarityItem.Options.ToList();
            adapter.UpdateOptions(new[] { new AchievementDisplayItem { TrophyType = "gold" } });
            adapter.TierFilters.SetRaritySelected(options[3], true);
            adapter.TierFilters.SetRaritySelected(options[4], true);
            adapter.TierFilters.SetTrophySelected(Playnite.SDK.ResourceProvider.GetString("LOCPlayAch_Trophy_Gold"), true);

            var state = adapter.CaptureState();
            CollectionAssert.AreEqual(new[] { (int)RarityTier.UltraRare }, state.Tiers.Rarities);
            Assert.AreEqual(true, state.Tiers.Capstones);
            CollectionAssert.AreEqual(new[] { "gold" }, state.Tiers.Trophies);

            adapter.ResetFilters();
            Assert.IsFalse(adapter.TierFilters.HasSelection);
            Assert.IsFalse(adapter.HasActiveFilters);

            adapter.RestoreState(state);
            Assert.IsTrue(adapter.TierFilters.IsRaritySelected(options[3]));
            Assert.IsTrue(adapter.TierFilters.IsRaritySelected(options[4]));
            Assert.IsTrue(adapter.TierFilters.Matches(
                new AchievementDisplayItem { Rarity = RarityTier.UltraRare, IsCapstone = true, TrophyType = "Gold" }));
            Assert.IsFalse(adapter.TierFilters.Matches(
                new AchievementDisplayItem { Rarity = RarityTier.UltraRare, IsCapstone = true, TrophyType = "silver" }));

            adapter.RestoreState(new GridControlBarFilterState());
            Assert.IsFalse(adapter.TierFilters.HasSelection, "a state without tiers clears them");
        }
    }
}
