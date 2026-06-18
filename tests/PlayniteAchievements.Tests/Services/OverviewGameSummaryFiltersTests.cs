using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Overview;
using PlayniteAchievements.ViewModels;

namespace PlayniteAchievements.Tests.Services
{
    [TestClass]
    public class OverviewGameSummaryFiltersTests
    {
        [TestMethod]
        public void ProgressFilter_Complete_ReturnsCompletedGames()
        {
            var result = ApplyProgress(Complete);

            CollectionAssert.AreEqual(new[] { "Complete" }, result);
        }

        [TestMethod]
        public void ProgressFilter_InProgress_ReturnsPartiallyUnlockedGames()
        {
            var result = ApplyProgress(InProgress);

            CollectionAssert.AreEqual(new[] { "In Progress" }, result);
        }

        [TestMethod]
        public void ProgressFilter_NoProgress_ReturnsZeroUnlockGames()
        {
            var result = ApplyProgress(NoProgress);

            CollectionAssert.AreEqual(new[] { "No Progress Played", "No Progress Unplayed" }, result);
        }

        [TestMethod]
        public void ProgressFilter_CompleteAndNoProgress_AddsZeroUnlockGames()
        {
            var result = ApplyProgress(Complete, NoProgress);

            CollectionAssert.AreEqual(new[] { "Complete", "No Progress Played", "No Progress Unplayed" }, result);
        }

        [TestMethod]
        public void ProgressFilter_InProgressAndNoProgress_ExcludesCompletedGames()
        {
            var result = ApplyProgress(InProgress, NoProgress);

            CollectionAssert.AreEqual(new[] { "In Progress", "No Progress Played", "No Progress Unplayed" }, result);
        }

        [TestMethod]
        public void ProgressFilter_EmptySelection_DoesNotFilterProgress()
        {
            var result = ApplyProgress();

            CollectionAssert.AreEqual(
                new[] { "Complete", "In Progress", "No Progress Played", "No Progress Unplayed" },
                result);
        }

        [TestMethod]
        public void ProgressFilter_AllSelection_DoesNotFilterProgress()
        {
            var result = ApplyProgress(Complete, InProgress, NoProgress);

            CollectionAssert.AreEqual(
                new[] { "Complete", "In Progress", "No Progress Played", "No Progress Unplayed" },
                result);
        }

        [TestMethod]
        public void ActivityFilter_Played_ReturnsGamesWithPlaytimeOrUnlocks()
        {
            var result = ApplyActivity(Played);

            CollectionAssert.AreEqual(new[] { "Complete", "In Progress", "No Progress Played" }, result);
        }

        [TestMethod]
        public void ActivityFilter_AllSelection_DoesNotFilterActivity()
        {
            var result = ApplyActivity(Played, Unplayed);

            CollectionAssert.AreEqual(
                new[] { "Complete", "In Progress", "No Progress Played", "No Progress Unplayed" },
                result);
        }

        [TestMethod]
        public void ActivityAndProgressFilters_CombineAcrossDropdowns()
        {
            var result = OverviewGameSummaryFilters.ApplyActivityAndProgressFilters(
                    CreateGames(),
                    new HashSet<string> { Played },
                    new HashSet<string> { NoProgress })
                .Select(game => game.GameName)
                .ToList();

            CollectionAssert.AreEqual(new[] { "No Progress Played" }, result);
        }

        [TestMethod]
        public void StableScopeFilters_NoneAndAllDoNotFilter()
        {
            var noneResult = OverviewGameSummaryFilters.ApplyActivityAndProgressFilters(
                    CreateGames(),
                    GameActivityScope.None,
                    GameProgressScope.None)
                .Select(game => game.GameName)
                .ToList();
            var allResult = OverviewGameSummaryFilters.ApplyActivityAndProgressFilters(
                    CreateGames(),
                    GameActivityScope.All,
                    GameProgressScope.All)
                .Select(game => game.GameName)
                .ToList();

            var expected = new[] { "Complete", "In Progress", "No Progress Played", "No Progress Unplayed" };
            CollectionAssert.AreEqual(expected, noneResult);
            CollectionAssert.AreEqual(expected, allResult);
        }

        private static List<string> ApplyProgress(params string[] selectedProgress)
        {
            return OverviewGameSummaryFilters.ApplyActivityAndProgressFilters(
                    CreateGames(),
                    new HashSet<string>(),
                    new HashSet<string>(selectedProgress))
                .Select(game => game.GameName)
                .ToList();
        }

        private static List<string> ApplyActivity(params string[] selectedActivity)
        {
            return OverviewGameSummaryFilters.ApplyActivityAndProgressFilters(
                    CreateGames(),
                    new HashSet<string>(selectedActivity),
                    new HashSet<string>())
                .Select(game => game.GameName)
                .ToList();
        }

        private static List<GameSummaryItem> CreateGames()
        {
            return new List<GameSummaryItem>
            {
                new GameSummaryItem
                {
                    GameName = "Complete",
                    IsCompleted = true,
                    UnlockedAchievements = 10,
                    TotalAchievements = 10,
                    LastPlayed = new DateTime(2026, 1, 1)
                },
                new GameSummaryItem
                {
                    GameName = "In Progress",
                    IsCompleted = false,
                    UnlockedAchievements = 3,
                    TotalAchievements = 10
                },
                new GameSummaryItem
                {
                    GameName = "No Progress Played",
                    IsCompleted = false,
                    UnlockedAchievements = 0,
                    TotalAchievements = 10,
                    LastPlayed = new DateTime(2026, 1, 2)
                },
                new GameSummaryItem
                {
                    GameName = "No Progress Unplayed",
                    IsCompleted = false,
                    UnlockedAchievements = 0,
                    TotalAchievements = 10
                }
            };
        }

        private const string Complete = OverviewGameSummaryFilters.CompleteFallback;
        private const string InProgress = OverviewGameSummaryFilters.InProgressFallback;
        private const string NoProgress = OverviewGameSummaryFilters.NoProgressFallback;
        private const string Played = OverviewGameSummaryFilters.PlayedFallback;
        private const string Unplayed = OverviewGameSummaryFilters.UnplayedFallback;
    }
}
