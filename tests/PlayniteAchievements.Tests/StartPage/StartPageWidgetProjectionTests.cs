using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.StartPage;
using PlayniteAchievements.ViewModels;

namespace PlayniteAchievements.Tests.StartPage
{
    [TestClass]
    public class StartPageWidgetProjectionTests
    {
        [TestMethod]
        public void ProjectGameSummaries_UsesStartPageSettingsSortAndDefaultLimit()
        {
            var items = Enumerable.Range(0, 30)
                .Select(index => new GameSummaryItem
                {
                    GameName = $"Game {index:D2}",
                    SortingName = $"Game {index:D2}",
                    LastUnlockUtc = new DateTime(2026, 1, 1).AddDays(index),
                    UnlockedAchievements = 1,
                    TotalAchievements = index + 1
                })
                .ToList();
            var settings = new PersistedSettings
            {
                OverviewGameSummariesGridSortMode = GameSummariesSortMode.Alphabetical,
                OverviewGameSummariesGridSortDescending = false
            };
            settings.StartPageGameSummariesGrid.SortMode = GameSummariesSortMode.RecentUnlock;
            settings.StartPageGameSummariesGrid.SortDescending = true;

            var result = StartPageWidgetProjection.ProjectGameSummaries(items, settings);

            Assert.AreEqual(StartPageWidgetProjection.DefaultGridRowLimit, result.Count);
            Assert.AreEqual("Game 29", result[0].GameName);
            Assert.AreEqual("Game 05", result[result.Count - 1].GameName);
        }

        [TestMethod]
        public void ProjectGameSummaries_UsesAlphabeticalSortFromStartPageSettings()
        {
            var items = new[]
            {
                new GameSummaryItem { GameName = "Zed", SortingName = "Zed", UnlockedAchievements = 1, TotalAchievements = 10 },
                new GameSummaryItem { GameName = "Alpha", SortingName = "Alpha", UnlockedAchievements = 1, TotalAchievements = 10 }
            };
            var settings = new PersistedSettings
            {
                OverviewGameSummariesGridSortMode = GameSummariesSortMode.RecentUnlock,
                OverviewGameSummariesGridSortDescending = true
            };
            settings.StartPageGameSummariesGrid.SortMode = GameSummariesSortMode.Alphabetical;
            settings.StartPageGameSummariesGrid.SortDescending = false;

            var result = StartPageWidgetProjection.ProjectGameSummaries(items, settings, rowLimit: 10);

            Assert.AreEqual("Alpha", result[0].GameName);
            Assert.AreEqual("Zed", result[1].GameName);
        }

        [TestMethod]
        public void ProjectGameSummaries_UsesExplicitSettingsLimit()
        {
            var items = Enumerable.Range(0, 10)
                .Select(index => new GameSummaryItem
                {
                    GameName = $"Game {index:D2}",
                    SortingName = $"Game {index:D2}",
                    UnlockedAchievements = 1,
                    TotalAchievements = 10
                })
                .ToList();
            var settings = new PersistedSettings
            {
                OverviewGameSummariesGridSortMode = GameSummariesSortMode.Alphabetical,
                OverviewGameSummariesGridSortDescending = false
            };
            settings.StartPageGameSummariesGrid.MaxRows = 3;
            settings.StartPageGameSummariesGrid.SortMode = GameSummariesSortMode.Alphabetical;
            settings.StartPageGameSummariesGrid.SortDescending = false;

            var result = StartPageWidgetProjection.ProjectGameSummaries(items, settings);

            Assert.AreEqual(3, result.Count);
            Assert.AreEqual("Game 00", result[0].GameName);
            Assert.AreEqual("Game 02", result[2].GameName);
        }

        [TestMethod]
        public void ProjectGameSummaries_NullSettingsLimitIsUnlimited()
        {
            var items = Enumerable.Range(0, 30)
                .Select(index => new GameSummaryItem
                {
                    GameName = $"Game {index:D2}",
                    SortingName = $"Game {index:D2}",
                    UnlockedAchievements = 1,
                    TotalAchievements = 10
                })
                .ToList();
            var settings = new PersistedSettings
            {
                StartPageGameSummariesGrid =
                {
                    MaxRows = null
                }
            };

            var result = StartPageWidgetProjection.ProjectGameSummaries(items, settings);

            Assert.AreEqual(30, result.Count);
        }

        [TestMethod]
        public void ProjectGameSummaries_DefaultScopeUsesPlayedCompletedAndInProgress()
        {
            var settings = CreateScopeTestSettings();

            var result = StartPageWidgetProjection.ProjectGameSummaries(CreateScopeTestGames(), settings)
                .Select(game => game.GameName)
                .ToList();

            CollectionAssert.AreEqual(new[] { "Complete", "In Progress" }, result);
        }

        [TestMethod]
        public void ProjectGameSummaries_NoneAndAllScopesDoNotFilter()
        {
            var settings = CreateScopeTestSettings();
            settings.StartPageActivityScope = GameActivityScope.None;
            settings.StartPageProgressScope = GameProgressScope.None;

            var noneResult = StartPageWidgetProjection.ProjectGameSummaries(CreateScopeTestGames(), settings)
                .Select(game => game.GameName)
                .ToList();

            settings.StartPageActivityScope = GameActivityScope.All;
            settings.StartPageProgressScope = GameProgressScope.All;
            var allResult = StartPageWidgetProjection.ProjectGameSummaries(CreateScopeTestGames(), settings)
                .Select(game => game.GameName)
                .ToList();

            var expected = new[] { "Complete", "In Progress", "No Progress Played", "No Progress Unplayed" };
            CollectionAssert.AreEqual(expected, noneResult);
            CollectionAssert.AreEqual(expected, allResult);
        }

        [TestMethod]
        public void ProjectGameSummaries_ProgressOnlyScopeUsesStrictOr()
        {
            var settings = CreateScopeTestSettings();
            settings.StartPageActivityScope = GameActivityScope.None;
            settings.StartPageProgressScope = GameProgressScope.InProgress | GameProgressScope.NoProgress;

            var result = StartPageWidgetProjection.ProjectGameSummaries(CreateScopeTestGames(), settings)
                .Select(game => game.GameName)
                .ToList();

            CollectionAssert.AreEqual(
                new[] { "In Progress", "No Progress Played", "No Progress Unplayed" },
                result);
        }

        [TestMethod]
        public void FilterGameSummariesForStartPage_CanIgnoreProgressForCompletedGamesPie()
        {
            var settings = CreateScopeTestSettings();
            settings.StartPageActivityScope = GameActivityScope.Played;
            settings.StartPageProgressScope = GameProgressScope.Completed;

            var result = StartPageWidgetProjection.FilterGameSummariesForStartPage(
                    CreateScopeTestGames(),
                    settings,
                    includeProgressScope: false)
                .Select(game => game.GameName)
                .ToList();

            CollectionAssert.AreEqual(new[] { "Complete", "In Progress", "No Progress Played" }, result);
        }

        [TestMethod]
        public void ProjectRecentUnlocks_UsesStartPageRecentSortAndDefaultLimit()
        {
            var items = Enumerable.Range(0, 30)
                .Select(index => new AchievementDisplayItem
                {
                    DisplayName = $"Achievement {index:D2}",
                    GameName = "Game",
                    UnlockTimeUtc = new DateTime(2026, 1, 1).AddMinutes(index),
                    RaritySortValue = index
                })
                .ToList();

            var result = StartPageWidgetProjection.ProjectRecentUnlocks(items, new PersistedSettings());

            Assert.AreEqual(StartPageWidgetProjection.DefaultGridRowLimit, result.Count);
            Assert.AreEqual("Achievement 29", result[0].DisplayName);
            Assert.AreEqual("Achievement 05", result[result.Count - 1].DisplayName);
            Assert.AreNotSame(items[0], result.Last());
        }

        [TestMethod]
        public void ProjectRecentUnlocks_UsesStartPageRecentSortSettings()
        {
            var items = new[]
            {
                new AchievementDisplayItem
                {
                    DisplayName = "Common",
                    GameName = "Game",
                    UnlockTimeUtc = new DateTime(2026, 1, 1).AddMinutes(2),
                    RaritySortValue = 80
                },
                new AchievementDisplayItem
                {
                    DisplayName = "Rare",
                    GameName = "Game",
                    UnlockTimeUtc = new DateTime(2026, 1, 1).AddMinutes(1),
                    RaritySortValue = 5
                }
            };
            var settings = new PersistedSettings();
            settings.StartPageRecentUnlocksGrid.SortMode = CompactListSortMode.Rarity;
            settings.StartPageRecentUnlocksGrid.SortDescending = false;

            var result = StartPageWidgetProjection.ProjectRecentUnlocks(items, settings);

            Assert.AreEqual("Rare", result[0].DisplayName);
            Assert.AreEqual("Common", result[1].DisplayName);
        }

        [TestMethod]
        public void ProjectRecentUnlocks_UsesExplicitAndNullSettingsLimits()
        {
            var items = Enumerable.Range(0, 12)
                .Select(index => new AchievementDisplayItem
                {
                    DisplayName = $"Achievement {index:D2}",
                    GameName = "Game",
                    UnlockTimeUtc = new DateTime(2026, 1, 1).AddMinutes(index)
                })
                .ToList();
            var settings = new PersistedSettings
            {
                StartPageRecentUnlocksGrid =
                {
                    MaxRows = 4
                }
            };

            var limited = StartPageWidgetProjection.ProjectRecentUnlocks(items, settings);

            Assert.AreEqual(4, limited.Count);
            Assert.AreEqual("Achievement 11", limited[0].DisplayName);
            Assert.AreEqual("Achievement 08", limited[3].DisplayName);

            settings.StartPageRecentUnlocksGrid.MaxRows = null;
            var unlimited = StartPageWidgetProjection.ProjectRecentUnlocks(items, settings);

            Assert.AreEqual(12, unlimited.Count);
        }

        private static PersistedSettings CreateScopeTestSettings()
        {
            var settings = new PersistedSettings();
            settings.StartPageGameSummariesGrid.SortMode = GameSummariesSortMode.Alphabetical;
            settings.StartPageGameSummariesGrid.SortDescending = false;
            settings.StartPageGameSummariesGrid.MaxRows = null;
            return settings;
        }

        private static GameSummaryItem[] CreateScopeTestGames()
        {
            return new[]
            {
                new GameSummaryItem
                {
                    GameName = "Complete",
                    SortingName = "Complete",
                    IsCompleted = true,
                    UnlockedAchievements = 10,
                    TotalAchievements = 10,
                    LastPlayed = new DateTime(2026, 1, 1)
                },
                new GameSummaryItem
                {
                    GameName = "In Progress",
                    SortingName = "In Progress",
                    UnlockedAchievements = 3,
                    TotalAchievements = 10
                },
                new GameSummaryItem
                {
                    GameName = "No Progress Played",
                    SortingName = "No Progress Played",
                    UnlockedAchievements = 0,
                    TotalAchievements = 10,
                    LastPlayed = new DateTime(2026, 1, 2)
                },
                new GameSummaryItem
                {
                    GameName = "No Progress Unplayed",
                    SortingName = "No Progress Unplayed",
                    UnlockedAchievements = 0,
                    TotalAchievements = 10
                }
            };
        }
    }
}
