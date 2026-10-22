using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.Tests.ViewModels
{
    [TestClass]
    public class GameTagFilterTests
    {
        private static readonly Guid MissableGame = Guid.NewGuid();
        private static readonly Guid CollectableGame = Guid.NewGuid();
        private static readonly Guid BothGame = Guid.NewGuid();

        private static GameSummaryItem[] Games() => new[]
        {
            // Every Missable achievement still locked: a game matches on its locked ones too.
            new GameSummaryItem
            {
                GameName = "Missable", PlayniteGameId = MissableGame, ProviderKey = "Steam", Platforms = new[] { "PC" },
                TagTotals = new[] { new AchievementTagTally(AchievementTagHelper.Missable, 0, 2) }
            },
            new GameSummaryItem
            {
                GameName = "Collectable", PlayniteGameId = CollectableGame, ProviderKey = "Steam", Platforms = new[] { "PC" },
                TagTotals = new[] { new AchievementTagTally("Collectable", 1, 3) }
            },
            new GameSummaryItem
            {
                GameName = "Both", PlayniteGameId = BothGame, ProviderKey = "Steam", Platforms = new[] { "PC" },
                TagTotals = new[] { new AchievementTagTally(AchievementTagHelper.Missable + "|Collectable", 1, 1) }
            }
        };

        private static GameSummaryGridControlBarAdapter CreateAdapter()
        {
            var adapter = new GameSummaryGridControlBarAdapter();
            adapter.UpdateOptions(Games());
            return adapter;
        }

        [TestMethod]
        public void GameBar_HasTagsAfterGame()
        {
            var ids = CreateAdapter().ControlBar.Items.Select(item => item.Id).ToList();
            Assert.AreEqual(ids.IndexOf(ShowcaseControlBarItems.Game) + 1, ids.IndexOf(ShowcaseControlBarItems.Tag));
        }

        [TestMethod]
        public void PickedTag_KeepsGamesWithATaggedAchievementLockedOrNot()
        {
            var adapter = CreateAdapter();
            adapter.TagFilter.Item.SetSelected(AchievementTagHelper.Missable, true);

            CollectionAssert.AreEquivalent(
                new[] { "Missable", "Both" },
                adapter.Apply(Games()).Select(game => game.GameName).ToArray());

            var restored = new GameSummaryGridControlBarAdapter();
            restored.RestoreState(adapter.CaptureState());
            restored.UpdateOptions(Games());
            Assert.AreEqual(2, restored.Apply(Games()).Count);
        }

        [TestMethod]
        public void ChartBars_LeaveTagsOutExceptTheTimelineAndCalendar()
        {
            Assert.IsNull(new PlayniteAchievements.ViewModels.Showcase.Widgets.ChartControlBarAdapter().TagFilter);
            Assert.IsNotNull(new PlayniteAchievements.ViewModels.Showcase.Widgets.TimelineControlBarAdapter().TagFilter);
            Assert.IsNotNull(new PlayniteAchievements.ViewModels.Showcase.Widgets.ActivityCalendarControlBarAdapter().TagFilter);
        }
    }
}