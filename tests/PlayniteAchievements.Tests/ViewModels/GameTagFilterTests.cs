using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.Overview;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.ViewModels.Items;
using PlayniteAchievements.ViewModels.Showcase.Widgets;

namespace PlayniteAchievements.Tests.ViewModels
{
    [TestClass]
    public class GameTagFilterTests
    {
        private static readonly Guid MissableGame = Guid.NewGuid();
        private static readonly Guid PlainGame = Guid.NewGuid();

        private static GameSummaryItem[] Games() => new[]
        {
            // Two Missable achievements, one Rare and still locked; one Common untagged unlock.
            new GameSummaryItem
            {
                GameName = "Missable", PlayniteGameId = MissableGame, ProviderKey = "Steam", Platforms = new[] { "PC" },
                TotalAchievements = 3, UnlockedAchievements = 2,
                Tallies = new[]
                {
                    new AchievementTally(AchievementTagHelper.Missable, RarityTier.Common, null, false, 1, 1),
                    new AchievementTally(AchievementTagHelper.Missable, RarityTier.Rare, "gold", true, 0, 1),
                    new AchievementTally(string.Empty, RarityTier.Common, null, false, 1, 1)
                }
            },
            new GameSummaryItem
            {
                GameName = "Plain", PlayniteGameId = PlainGame, ProviderKey = "Xbox", Platforms = new[] { "PC" },
                TotalAchievements = 2, UnlockedAchievements = 1,
                Tallies = new[] { new AchievementTally(string.Empty, RarityTier.Rare, null, false, 1, 2) }
            }
        };

        [TestMethod]
        public void GameBar_HasNoAchievementFilters()
        {
            var adapter = new GameSummaryGridControlBarAdapter();
            Assert.IsNull(adapter.TagFilter);
            Assert.IsNull(adapter.TierFilters);
            var ids = adapter.ControlBar.Items.Select(item => item.Id).ToList();
            CollectionAssert.DoesNotContain(ids, ShowcaseControlBarItems.Tag);
            CollectionAssert.DoesNotContain(ids, ShowcaseControlBarItems.Rarity);
            CollectionAssert.DoesNotContain(ids, ShowcaseControlBarItems.Trophy);
        }

        [TestMethod]
        public void ChartBars_OfferAchievementFiltersExceptTheScoreCard()
        {
            Assert.IsNull(new ChartControlBarAdapter().TagFilter);
            Assert.IsNotNull(new TimelineControlBarAdapter().TierFilters);
            Assert.IsNotNull(new ActivityCalendarControlBarAdapter().TagFilter);
            Assert.IsNotNull(new PieControlBarAdapter().TagFilter);
            Assert.IsNotNull(new StatisticsControlBarAdapter().TierFilters);
        }

        [TestMethod]
        public void AchievementFilters_NeverDropGames()
        {
            var adapter = new PieControlBarAdapter();
            adapter.UpdateOptions(Games());
            adapter.TagFilter.Item.SetSelected(AchievementTagHelper.Missable, true);

            Assert.IsTrue(adapter.HasAchievementSelection);
            Assert.AreEqual(2, adapter.Apply(Games()).Count);
        }

        [TestMethod]
        public void AchievementFilters_SetAsideWhileTheWidgetCountsGames()
        {
            var adapter = new PieControlBarAdapter();
            adapter.UpdateOptions(Games());
            adapter.TagFilter.Item.SetSelected(AchievementTagHelper.Missable, true);
            var untagged = new AchievementTally(string.Empty, 0, 1);
            Assert.IsFalse(adapter.MatchesTally(untagged));

            adapter.SetAchievementFiltersApply(false);
            Assert.IsFalse(adapter.HasAchievementSelection);
            Assert.IsTrue(adapter.MatchesTally(untagged));
            Assert.IsFalse(adapter.TagFilter.Item.IsVisible);

            // The pick is kept for when the widget counts achievements again.
            adapter.SetAchievementFiltersApply(true);
            Assert.IsTrue(adapter.HasAchievementSelection);
            var restored = new PieControlBarAdapter();
            restored.RestoreState(adapter.CaptureState());
            Assert.IsTrue(restored.TagFilter.SelectedTags.Contains(AchievementTagHelper.Missable));
        }

        [TestMethod]
        public void FilterAchievements_KeepsLockedCountsAndGameTotals()
        {
            var games = Games().ToList();
            var source = OverviewDataSnapshot.FromGameSummaries(games);
            source.Completions = 1;

            var adapter = new PieControlBarAdapter();
            adapter.UpdateOptions(games);
            adapter.TierFilters.RarityItem.SetSelected(Playnite.SDK.ResourceProvider.GetString("LOCPlayAch_Rarity_Rare"), true);
            var rare = OverviewLinkedSnapshots.FilterAchievements(source, adapter.MatchesAchievement, adapter.MatchesTally);

            Assert.AreEqual(3, rare.TotalAchievements);
            Assert.AreEqual(1, rare.TotalUnlocked);
            Assert.AreEqual(2, rare.TotalLocked);
            Assert.AreEqual(3, rare.TotalRarePossible);
            Assert.AreEqual(0, rare.TotalCommonPossible);
            Assert.AreEqual(1, rare.TotalGoldPossible);
            Assert.AreEqual(1, rare.TotalByProvider["Steam"]);
            Assert.AreEqual(2, rare.TotalByProvider["Xbox"]);
            // Game counts follow the games alone.
            Assert.AreEqual(2, rare.TotalGames);
            Assert.AreEqual(1, rare.Completions);
        }
    }
}
