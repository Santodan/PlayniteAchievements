using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.ViewModels.Items;
using PlayniteAchievements.ViewModels.Showcase.Widgets;

namespace PlayniteAchievements.Tests.ViewModels
{
    [TestClass]
    public class TimelineControlBarAdapterTests
    {
        [TestMethod]
        public void Bar_OffersPlatformGameTagRarityAndTrophy_WithoutProgress()
        {
            var adapter = new TimelineControlBarAdapter();

            CollectionAssert.AreEqual(
                new[]
                {
                    ShowcaseControlBarItems.Platform,
                    ShowcaseControlBarItems.Game,
                    ShowcaseControlBarItems.Tag,
                    ShowcaseControlBarItems.Rarity,
                    ShowcaseControlBarItems.Trophy
                },
                adapter.Entries.Select(entry => entry.Id).ToArray());
        }

        [TestMethod]
        public void Capstone_NarrowsThePickedTiers_OrKeepsEveryCapstoneAlone()
        {
            var adapter = new TimelineControlBarAdapter();
            var filters = adapter.TierFilters;
            var rareCapstone = new AchievementDisplayItem { Rarity = RarityTier.Rare, IsCapstone = true };
            var commonCapstone = new AchievementDisplayItem { Rarity = RarityTier.Common, IsCapstone = true };
            var rare = new AchievementDisplayItem { Rarity = RarityTier.Rare };

            adapter.RestoreState(new ControlBarFilterState { Capstones = true });
            Assert.IsTrue(filters.Matches(rareCapstone));
            Assert.IsTrue(filters.Matches(commonCapstone));
            Assert.IsFalse(filters.Matches(rare));

            adapter.RestoreState(new ControlBarFilterState
            {
                Rarities = new[] { (int)RarityTier.Rare, (int)RarityTier.UltraRare }.ToList(),
                Capstones = true
            });
            Assert.IsTrue(filters.Matches(rareCapstone));
            Assert.IsFalse(filters.Matches(commonCapstone), "capstone narrows the tiers");
            Assert.IsFalse(filters.Matches(rare));
            Assert.AreEqual(true, adapter.CaptureState().Capstones);
        }

        [TestMethod]
        public void RarityItem_OffersCapstoneLast_AndShowsThePickedBadges()
        {
            var adapter = new TimelineControlBarAdapter();
            var rarity = adapter.TierFilters.RarityItem;
            var options = rarity.Options.ToList();
            Assert.AreEqual(5, options.Count);

            rarity.SetSelected(options[4], true);
            rarity.SetSelected(options[2], true);

            CollectionAssert.AreEqual(new[] { "BadgeRarityRare", "BadgeRarityCompleted" }, rarity.SelectedIconKeys.ToArray());
            Assert.IsTrue(rarity.ShowsSelectedIcons);
            Assert.AreEqual(string.Empty, rarity.ButtonText, "badges stand in for the names");
            Assert.AreEqual(rarity.DisplayText, rarity.ButtonToolTip);
            Assert.AreEqual("TrophyGold", adapter.TierFilters.TrophyItem.IconKeyOf(
                Playnite.SDK.ResourceProvider.GetString("LOCPlayAch_Trophy_Gold")));
        }

        [TestMethod]
        public void RarityAndTrophy_RoundTripThroughState_AndProgressIsIgnored()
        {
            var state = new ControlBarFilterState
            {
                Rarities = new[] { (int)RarityTier.Rare }.ToList(),
                Trophies = new[] { "gold" }.ToList(),
                Progress = new[] { 0 }.ToList()
            };
            var adapter = new TimelineControlBarAdapter();
            adapter.RestoreState(state);

            var captured = adapter.CaptureState();
            CollectionAssert.AreEqual(new[] { (int)RarityTier.Rare }, captured.Rarities);
            CollectionAssert.AreEqual(new[] { "gold" }, captured.Trophies);
            Assert.AreEqual(0, captured.Progress?.Count ?? 0, "the timeline has no progress filter");
            Assert.IsTrue(adapter.TierFilters.Matches(new AchievementDisplayItem { Rarity = RarityTier.Rare, TrophyType = "Gold" }));
            Assert.IsFalse(adapter.TierFilters.Matches(new AchievementDisplayItem { Rarity = RarityTier.Common, TrophyType = "gold" }));
        }

        [TestMethod]
        public void PickedOptions_ShowBadgesOnlyWhenEveryPickHasOne()
        {
            var picked = new System.Collections.Generic.HashSet<string>();
            var filter = new GridMultiSelectFilter(
                null,
                null,
                () => string.Join(", ", picked),
                new[] { "a", "b", "c" },
                picked.Contains,
                (option, selected) => { if (selected) { picked.Add(option); } else { picked.Remove(option); } })
            {
                IconKeyOf = option => option == "c" ? null : "Badge" + option
            };

            filter.SetSelected("a", true);
            filter.SetSelected("b", true);
            CollectionAssert.AreEqual(new[] { "Badgea", "Badgeb" }, filter.SelectedIconKeys.ToArray());

            filter.SetSelected("c", true);
            Assert.AreEqual(0, filter.SelectedIconKeys.Count, "a pick without a badge brings the names back");
            Assert.AreEqual("a, b, c", filter.ButtonText);
        }
    }
}
