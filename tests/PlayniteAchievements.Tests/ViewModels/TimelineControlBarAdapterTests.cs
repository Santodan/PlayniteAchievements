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
        public void Bar_OffersPlatformGameRarityAndTrophy_WithoutProgress()
        {
            var adapter = new TimelineControlBarAdapter();

            CollectionAssert.AreEqual(
                new[]
                {
                    ShowcaseControlBarItems.Platform,
                    ShowcaseControlBarItems.Game,
                    ShowcaseControlBarItems.Rarity,
                    ShowcaseControlBarItems.Trophy
                },
                adapter.Entries.Select(entry => entry.Id).ToArray());
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
    }
}