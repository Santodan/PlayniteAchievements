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
                    TotalAchievements = index
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
                new GameSummaryItem { GameName = "Zed", SortingName = "Zed" },
                new GameSummaryItem { GameName = "Alpha", SortingName = "Alpha" }
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
                    SortingName = $"Game {index:D2}"
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
                    SortingName = $"Game {index:D2}"
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
    }
}
