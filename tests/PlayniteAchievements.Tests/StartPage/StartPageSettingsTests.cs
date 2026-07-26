using System.Collections.Generic;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;

namespace PlayniteAchievements.Tests.StartPage
{
    [TestClass]
    public class StartPageSettingsTests
    {
        [TestMethod]
        public void CloneAndCopyFrom_PreserveStartPageColumnSettings()
        {
            var source = new PersistedSettings
            {
                StartPageAchievementColumnVisibility = new Dictionary<string, bool>
                {
                    ["Achievement"] = false
                },
                StartPageAchievementColumnWidths = new Dictionary<string, double>
                {
                    ["Achievement"] = 320
                },
                StartPageAchievementColumnOrder = new Dictionary<string, int>
                {
                    ["Achievement"] = 2
                },
                StartPageAchievementColumnAlignments = new Dictionary<string, GridAlignment>
                {
                    ["Achievement"] = GridAlignment.Center
                },
                StartPageFriendAchievementColumnVisibility = new Dictionary<string, bool>
                {
                    ["Friend"] = false
                },
                StartPageFriendAchievementColumnWidths = new Dictionary<string, double>
                {
                    ["Friend"] = 180
                },
                StartPageFriendAchievementColumnOrder = new Dictionary<string, int>
                {
                    ["Friend"] = 4
                },
                StartPageFriendAchievementColumnAlignments = new Dictionary<string, GridAlignment>
                {
                    ["Friend"] = GridAlignment.Right
                },
                StartPageGameSummariesColumnVisibility = new Dictionary<string, bool>
                {
                    ["GameSummaryProvider"] = false
                },
                StartPageGameSummariesColumnWidths = new Dictionary<string, double>
                {
                    ["GameSummaryProvider"] = 140
                },
                StartPageGameSummariesColumnOrder = new Dictionary<string, int>
                {
                    ["GameSummaryProvider"] = 3
                },
                StartPageGameSummariesColumnAlignments = new Dictionary<string, GridAlignment>
                {
                    ["GameSummaryProvider"] = GridAlignment.Right
                },
                StartPageGameSummariesColumnVerticalAlignments = new Dictionary<string, GridVerticalAlignment>
                {
                    ["GameSummaryProvider"] = GridVerticalAlignment.Bottom
                },
                StartPageGameSummariesColumnHeaderAlignments = new Dictionary<string, GridAlignment>
                {
                    ["GameSummaryProvider"] = GridAlignment.Center
                }
            };

            var clone = source.Clone();
            var copy = new PersistedSettings();
            copy.CopyFrom(source);

            Assert.IsFalse(clone.StartPageAchievementColumnVisibility["Achievement"]);
            Assert.AreEqual(320, clone.StartPageAchievementColumnWidths["Achievement"]);
            Assert.AreEqual(2, clone.StartPageAchievementColumnOrder["Achievement"]);
            Assert.AreEqual(GridAlignment.Center, clone.StartPageAchievementColumnAlignments["Achievement"]);
            Assert.IsFalse(clone.StartPageFriendAchievementColumnVisibility["Friend"]);
            Assert.AreEqual(180, clone.StartPageFriendAchievementColumnWidths["Friend"]);
            Assert.AreEqual(4, clone.StartPageFriendAchievementColumnOrder["Friend"]);
            Assert.AreEqual(GridAlignment.Right, clone.StartPageFriendAchievementColumnAlignments["Friend"]);
            Assert.IsFalse(clone.StartPageGameSummariesColumnVisibility["GameSummaryProvider"]);
            Assert.AreEqual(140, clone.StartPageGameSummariesColumnWidths["GameSummaryProvider"]);
            Assert.AreEqual(3, clone.StartPageGameSummariesColumnOrder["GameSummaryProvider"]);
            Assert.AreEqual(GridAlignment.Right, clone.StartPageGameSummariesColumnAlignments["GameSummaryProvider"]);
            Assert.AreEqual(GridVerticalAlignment.Bottom, clone.StartPageGameSummariesColumnVerticalAlignments["GameSummaryProvider"]);
            Assert.AreEqual(GridAlignment.Center, clone.StartPageGameSummariesColumnHeaderAlignments["GameSummaryProvider"]);

            Assert.IsFalse(copy.StartPageAchievementColumnVisibility["Achievement"]);
            Assert.AreEqual(320, copy.StartPageAchievementColumnWidths["Achievement"]);
            Assert.AreEqual(2, copy.StartPageAchievementColumnOrder["Achievement"]);
            Assert.AreEqual(GridAlignment.Center, copy.StartPageAchievementColumnAlignments["Achievement"]);
            Assert.IsFalse(copy.StartPageFriendAchievementColumnVisibility["Friend"]);
            Assert.AreEqual(180, copy.StartPageFriendAchievementColumnWidths["Friend"]);
            Assert.AreEqual(4, copy.StartPageFriendAchievementColumnOrder["Friend"]);
            Assert.AreEqual(GridAlignment.Right, copy.StartPageFriendAchievementColumnAlignments["Friend"]);
            Assert.IsFalse(copy.StartPageGameSummariesColumnVisibility["GameSummaryProvider"]);
            Assert.AreEqual(140, copy.StartPageGameSummariesColumnWidths["GameSummaryProvider"]);
            Assert.AreEqual(3, copy.StartPageGameSummariesColumnOrder["GameSummaryProvider"]);
            Assert.AreEqual(GridAlignment.Right, copy.StartPageGameSummariesColumnAlignments["GameSummaryProvider"]);
            Assert.AreEqual(GridVerticalAlignment.Bottom, copy.StartPageGameSummariesColumnVerticalAlignments["GameSummaryProvider"]);
            Assert.AreEqual(GridAlignment.Center, copy.StartPageGameSummariesColumnHeaderAlignments["GameSummaryProvider"]);

            Assert.AreNotSame(source.StartPageAchievementColumnVisibility, clone.StartPageAchievementColumnVisibility);
            Assert.AreNotSame(source.StartPageAchievementColumnWidths, clone.StartPageAchievementColumnWidths);
            Assert.AreNotSame(source.StartPageAchievementColumnOrder, clone.StartPageAchievementColumnOrder);
            Assert.AreNotSame(source.StartPageAchievementColumnAlignments, clone.StartPageAchievementColumnAlignments);
            Assert.AreNotSame(source.StartPageFriendAchievementColumnVisibility, clone.StartPageFriendAchievementColumnVisibility);
            Assert.AreNotSame(source.StartPageFriendAchievementColumnWidths, clone.StartPageFriendAchievementColumnWidths);
            Assert.AreNotSame(source.StartPageFriendAchievementColumnOrder, clone.StartPageFriendAchievementColumnOrder);
            Assert.AreNotSame(source.StartPageFriendAchievementColumnAlignments, clone.StartPageFriendAchievementColumnAlignments);
            Assert.AreNotSame(source.StartPageGameSummariesColumnVisibility, copy.StartPageGameSummariesColumnVisibility);
            Assert.AreNotSame(source.StartPageGameSummariesColumnWidths, copy.StartPageGameSummariesColumnWidths);
            Assert.AreNotSame(source.StartPageGameSummariesColumnOrder, copy.StartPageGameSummariesColumnOrder);
            Assert.AreNotSame(source.StartPageGameSummariesColumnAlignments, copy.StartPageGameSummariesColumnAlignments);
            Assert.AreNotSame(source.StartPageGameSummariesColumnVerticalAlignments, copy.StartPageGameSummariesColumnVerticalAlignments);
            Assert.AreNotSame(source.StartPageGameSummariesColumnHeaderAlignments, copy.StartPageGameSummariesColumnHeaderAlignments);
        }

        [TestMethod]
        public void CloneAndCopyFrom_PreserveStartPageWidgetSettings()
        {
            var source = new PersistedSettings();
            source.StartPageGameSummariesGrid.UseCoverImages = false;
            source.StartPageGameSummariesGrid.ShowMetadataPlatform = false;
            source.StartPageGameSummariesGrid.ShowMetadataPlaytime = false;
            source.StartPageGameSummariesGrid.ShowMetadataRegion = false;
            source.StartPageGameSummariesGrid.ShowCompletionGlow = false;
            source.StartPageGameSummariesGrid.ColorRarityColumnsByRarity = true;
            source.StartPageGameSummariesGrid.ShowNameAboveProgress = true;
            source.StartPageGameSummariesGrid.ShowColumnHeaders = false;
            source.StartPageGameSummariesGrid.ShowControlBar = true;
            source.StartPageGameSummariesGrid.RowHeight = 72d;
            source.StartPageGameSummariesGrid.MaxRows = 11;
            source.StartPageGameSummariesGrid.SortMode = GameSummariesSortMode.Alphabetical;
            source.StartPageGameSummariesGrid.SortDescending = false;

            source.StartPageRecentUnlocksGrid.UseCoverImages = false;
            source.StartPageRecentUnlocksGrid.ColorNamesByRarity = true;
            source.StartPageRecentUnlocksGrid.ColorRarityColumnsByRarity = true;
            source.StartPageRecentUnlocksGrid.ShowColumnHeaders = false;
            source.StartPageRecentUnlocksGrid.ShowControlBar = true;
            source.StartPageRecentUnlocksGrid.RowHeight = 84d;
            source.StartPageRecentUnlocksGrid.MaxRows = 12;
            source.StartPageRecentUnlocksGrid.SortMode = CompactListSortMode.Rarity;
            source.StartPageRecentUnlocksGrid.SortDescending = false;

            source.StartPageFriendsRecentUnlocksGrid.UseCoverImages = false;
            source.StartPageFriendsRecentUnlocksGrid.ColorNamesByRarity = true;
            source.StartPageFriendsRecentUnlocksGrid.ColorRarityColumnsByRarity = true;
            source.StartPageFriendsRecentUnlocksGrid.ShowColumnHeaders = false;
            source.StartPageFriendsRecentUnlocksGrid.ShowControlBar = true;
            source.StartPageFriendsRecentUnlocksGrid.RowHeight = 86d;
            source.StartPageFriendsRecentUnlocksGrid.MaxRows = 13;
            source.StartPageFriendsRecentUnlocksGrid.SortMode = CompactListSortMode.None;
            source.StartPageFriendsRecentUnlocksGrid.SortDescending = false;

            source.StartPagePieCharts.ShowCenterPercentage = false;
            source.StartPagePieCharts.SmallSliceMode = OverviewPieSmallSliceMode.Hide;
            source.StartPageActivityScope = GameActivityScope.All;
            source.StartPageProgressScope = GameProgressScope.NoProgress;

            var clone = source.Clone();
            var copy = new PersistedSettings();
            copy.CopyFrom(source);

            Assert.IsFalse(clone.StartPageGameSummariesGrid.UseCoverImages);
            Assert.IsFalse(clone.StartPageGameSummariesGrid.ShowMetadataPlatform);
            Assert.IsFalse(clone.StartPageGameSummariesGrid.ShowMetadataPlaytime);
            Assert.IsFalse(clone.StartPageGameSummariesGrid.ShowMetadataRegion);
            Assert.IsFalse(clone.StartPageGameSummariesGrid.ShowCompletionGlow);
            Assert.IsTrue(clone.StartPageGameSummariesGrid.ColorRarityColumnsByRarity);
            Assert.IsTrue(clone.StartPageGameSummariesGrid.ShowNameAboveProgress);
            Assert.IsFalse(clone.StartPageGameSummariesGrid.ShowColumnHeaders);
            Assert.IsTrue(clone.StartPageGameSummariesGrid.ShowControlBar);
            Assert.AreEqual(72d, clone.StartPageGameSummariesGrid.RowHeight);
            Assert.AreEqual(11, clone.StartPageGameSummariesGrid.MaxRows);
            Assert.AreEqual(GameSummariesSortMode.Alphabetical, clone.StartPageGameSummariesGrid.SortMode);
            Assert.IsFalse(clone.StartPageGameSummariesGrid.SortDescending);
            Assert.IsTrue(copy.StartPageGameSummariesGrid.ShowControlBar);

            Assert.IsFalse(copy.StartPageRecentUnlocksGrid.UseCoverImages);
            Assert.IsTrue(copy.StartPageRecentUnlocksGrid.ColorNamesByRarity);
            Assert.IsTrue(copy.StartPageRecentUnlocksGrid.ColorRarityColumnsByRarity);
            Assert.IsFalse(copy.StartPageRecentUnlocksGrid.ShowColumnHeaders);
            Assert.IsTrue(copy.StartPageRecentUnlocksGrid.ShowControlBar);
            Assert.AreEqual(84d, copy.StartPageRecentUnlocksGrid.RowHeight);
            Assert.AreEqual(12, copy.StartPageRecentUnlocksGrid.MaxRows);
            Assert.AreEqual(CompactListSortMode.Rarity, copy.StartPageRecentUnlocksGrid.SortMode);
            Assert.IsFalse(copy.StartPageRecentUnlocksGrid.SortDescending);
            Assert.IsTrue(clone.StartPageRecentUnlocksGrid.ShowControlBar);

            Assert.IsFalse(copy.StartPageFriendsRecentUnlocksGrid.UseCoverImages);
            Assert.IsTrue(copy.StartPageFriendsRecentUnlocksGrid.ColorNamesByRarity);
            Assert.IsTrue(copy.StartPageFriendsRecentUnlocksGrid.ColorRarityColumnsByRarity);
            Assert.IsFalse(copy.StartPageFriendsRecentUnlocksGrid.ShowColumnHeaders);
            Assert.IsTrue(copy.StartPageFriendsRecentUnlocksGrid.ShowControlBar);
            Assert.AreEqual(86d, copy.StartPageFriendsRecentUnlocksGrid.RowHeight);
            Assert.AreEqual(13, copy.StartPageFriendsRecentUnlocksGrid.MaxRows);
            Assert.AreEqual(CompactListSortMode.None, copy.StartPageFriendsRecentUnlocksGrid.SortMode);
            Assert.IsFalse(copy.StartPageFriendsRecentUnlocksGrid.SortDescending);
            Assert.IsTrue(clone.StartPageFriendsRecentUnlocksGrid.ShowControlBar);

            Assert.IsFalse(clone.StartPagePieCharts.ShowCenterPercentage);
            Assert.AreEqual(OverviewPieSmallSliceMode.Hide, clone.StartPagePieCharts.SmallSliceMode);
            Assert.IsFalse(copy.StartPagePieCharts.ShowCenterPercentage);
            Assert.AreEqual(OverviewPieSmallSliceMode.Hide, copy.StartPagePieCharts.SmallSliceMode);
            Assert.AreEqual(GameActivityScope.All, clone.StartPageActivityScope);
            Assert.AreEqual(GameProgressScope.NoProgress, clone.StartPageProgressScope);
            Assert.AreEqual(GameActivityScope.All, copy.StartPageActivityScope);
            Assert.AreEqual(GameProgressScope.NoProgress, copy.StartPageProgressScope);

            Assert.AreNotSame(source.StartPageGameSummariesGrid, clone.StartPageGameSummariesGrid);
            Assert.AreNotSame(source.StartPageRecentUnlocksGrid, copy.StartPageRecentUnlocksGrid);
            Assert.AreNotSame(source.StartPageFriendsRecentUnlocksGrid, copy.StartPageFriendsRecentUnlocksGrid);
            Assert.AreNotSame(source.StartPagePieCharts, clone.StartPagePieCharts);
            Assert.AreNotSame(source.StartPagePieCharts, copy.StartPagePieCharts);
        }

        [TestMethod]
        public void JsonRoundTrip_RebindsStartPageGridWrappersToGridOptions()
        {
            var settings = new PersistedSettings();
            settings.StartPageGameSummariesGrid.ShowColumnHeaders = false;
            settings.StartPageGameSummariesGrid.ShowControlBar = true;
            settings.StartPageGameSummariesGrid.RowHeight = 61d;
            settings.StartPageGameSummariesGrid.MaxRows = 6;

            settings.StartPageRecentUnlocksGrid.ShowColumnHeaders = false;
            settings.StartPageRecentUnlocksGrid.ShowControlBar = true;
            settings.StartPageRecentUnlocksGrid.RowHeight = 62d;
            settings.StartPageRecentUnlocksGrid.MaxRows = 7;

            settings.StartPageFriendsRecentUnlocksGrid.ShowColumnHeaders = false;
            settings.StartPageFriendsRecentUnlocksGrid.ShowControlBar = true;
            settings.StartPageFriendsRecentUnlocksGrid.RowHeight = 63d;
            settings.StartPageFriendsRecentUnlocksGrid.MaxRows = 8;

            var roundTrip = JsonConvert.DeserializeObject<PersistedSettings>(
                JsonConvert.SerializeObject(settings));

            Assert.IsFalse(roundTrip.StartPageGameSummariesGrid.ShowColumnHeaders);
            Assert.IsTrue(roundTrip.StartPageGameSummariesGrid.ShowControlBar);
            Assert.AreEqual(61d, roundTrip.StartPageGameSummariesGrid.RowHeight);
            Assert.AreEqual(6, roundTrip.StartPageGameSummariesGrid.MaxRows);

            Assert.IsFalse(roundTrip.StartPageRecentUnlocksGrid.ShowColumnHeaders);
            Assert.IsTrue(roundTrip.StartPageRecentUnlocksGrid.ShowControlBar);
            Assert.AreEqual(62d, roundTrip.StartPageRecentUnlocksGrid.RowHeight);
            Assert.AreEqual(7, roundTrip.StartPageRecentUnlocksGrid.MaxRows);

            Assert.IsFalse(roundTrip.StartPageFriendsRecentUnlocksGrid.ShowColumnHeaders);
            Assert.IsTrue(roundTrip.StartPageFriendsRecentUnlocksGrid.ShowControlBar);
            Assert.AreEqual(63d, roundTrip.StartPageFriendsRecentUnlocksGrid.RowHeight);
            Assert.AreEqual(8, roundTrip.StartPageFriendsRecentUnlocksGrid.MaxRows);

            roundTrip.StartPageGameSummariesGrid.RowHeight = 71d;
            roundTrip.StartPageRecentUnlocksGrid.RowHeight = 72d;
            roundTrip.StartPageFriendsRecentUnlocksGrid.RowHeight = 73d;

            Assert.AreEqual(71d, roundTrip.GridOptions.GetGameSummaries(GridOptionKeys.GameSummaries.StartPage).RowHeight);
            Assert.AreEqual(72d, roundTrip.GridOptions.GetAchievement(GridOptionKeys.Achievement.StartPageRecent).RowHeight);
            Assert.AreEqual(73d, roundTrip.GridOptions.GetAchievement(GridOptionKeys.Achievement.StartPageFriendAchievements).RowHeight);
        }

        [TestMethod]
        public void JsonRoundTrip_FullSettingsRootRebindsStartPageGridWrappersToGridOptions()
        {
            var settings = new PlayniteAchievementsSettings();
            settings.Persisted.StartPageGameSummariesGrid.ShowColumnHeaders = false;
            settings.Persisted.StartPageGameSummariesGrid.RowHeight = 64d;
            settings.Persisted.StartPageGameSummariesGrid.MaxRows = 4;
            settings.Persisted.StartPageRecentUnlocksGrid.ShowColumnHeaders = false;
            settings.Persisted.StartPageRecentUnlocksGrid.RowHeight = 65d;
            settings.Persisted.StartPageRecentUnlocksGrid.MaxRows = 5;

            var roundTrip = JsonConvert.DeserializeObject<PlayniteAchievementsSettings>(
                JsonConvert.SerializeObject(settings));

            Assert.IsFalse(roundTrip.Persisted.StartPageGameSummariesGrid.ShowColumnHeaders);
            Assert.AreEqual(64d, roundTrip.Persisted.StartPageGameSummariesGrid.RowHeight);
            Assert.AreEqual(4, roundTrip.Persisted.StartPageGameSummariesGrid.MaxRows);
            Assert.IsFalse(roundTrip.Persisted.StartPageRecentUnlocksGrid.ShowColumnHeaders);
            Assert.AreEqual(65d, roundTrip.Persisted.StartPageRecentUnlocksGrid.RowHeight);
            Assert.AreEqual(5, roundTrip.Persisted.StartPageRecentUnlocksGrid.MaxRows);
        }

        [TestMethod]
        public void StartPageGridGetter_RebindsStaleWrapperBeforeWritingMaxRows()
        {
            var settings = new PersistedSettings();

            var ignoredGameWrapper = settings.StartPageGameSummariesGrid;
            var ignoredRecentWrapper = settings.StartPageRecentUnlocksGrid;
            var replacement = settings.GridOptions.Clone();
            replacement.GetGameSummaries(GridOptionKeys.GameSummaries.StartPage).MaxRows = 3;
            replacement.GetAchievement(GridOptionKeys.Achievement.StartPageRecent).MaxRows = 4;
            SetGridOptionsBackingField(settings, replacement);

            settings.StartPageGameSummariesGrid.MaxRows = 11;
            settings.StartPageRecentUnlocksGrid.MaxRows = 12;

            Assert.AreEqual(11, settings.GridOptions.GetGameSummaries(GridOptionKeys.GameSummaries.StartPage).MaxRows);
            Assert.AreEqual(12, settings.GridOptions.GetAchievement(GridOptionKeys.Achievement.StartPageRecent).MaxRows);
        }

        [TestMethod]
        public void JsonRoundTrip_PreservesEveryGridOptionsSurface()
        {
            var settings = new PersistedSettings();
            var achievementIds = new[]
            {
                GridOptionKeys.Achievement.Default,
                GridOptionKeys.Achievement.SingleGame,
                GridOptionKeys.Achievement.OverviewRecent,
                GridOptionKeys.Achievement.OverviewSelectedGame,
                GridOptionKeys.Achievement.FriendsOverviewRecent,
                GridOptionKeys.Achievement.FriendsOverviewSelectedFriend,
                GridOptionKeys.Achievement.FriendsOverviewSelectedGame,
                GridOptionKeys.Achievement.FriendsOverviewSelectedFriendGame,
                GridOptionKeys.Achievement.ViewFriendsAchievements,
                GridOptionKeys.Achievement.ViewFriendsAchievementsSelectedFriend,
                GridOptionKeys.Achievement.StartPageRecent,
                GridOptionKeys.Achievement.StartPageFriendAchievements,
                GridOptionKeys.Achievement.DesktopTheme
            };
            var gameSummaryIds = new[]
            {
                GridOptionKeys.GameSummaries.Overview,
                GridOptionKeys.GameSummaries.StartPage,
                GridOptionKeys.GameSummaries.ViewAchievements,
                GridOptionKeys.GameSummaries.FriendsOverview,
                GridOptionKeys.GameSummaries.FriendsOverviewSelectedFriend,
                GridOptionKeys.GameSummaries.ViewFriendsAchievements,
                GridOptionKeys.GameSummaries.ViewFriendsAchievementsSelectedFriend
            };
            var friendSummaryIds = new[]
            {
                GridOptionKeys.FriendSummaries.FriendsOverview,
                GridOptionKeys.FriendSummaries.ViewFriendsAchievements
            };
            var categorySummaryIds = new[]
            {
                GridOptionKeys.CategorySummaries.ViewAchievements,
                GridOptionKeys.CategorySummaries.OverviewSelectedGame,
                GridOptionKeys.CategorySummaries.FriendsOverview,
                GridOptionKeys.CategorySummaries.ViewFriendsAchievements,
                GridOptionKeys.CategorySummaries.DesktopTheme
            };

            for (var index = 0; index < achievementIds.Length; index++)
            {
                ConfigureAchievementGrid(
                    settings.GridOptions.GetAchievement(achievementIds[index]),
                    index + 10);
            }

            for (var index = 0; index < gameSummaryIds.Length; index++)
            {
                ConfigureGameSummaryGrid(
                    settings.GridOptions.GetGameSummaries(gameSummaryIds[index]),
                    index + 30);
            }

            for (var index = 0; index < friendSummaryIds.Length; index++)
            {
                ConfigureFriendSummaryGrid(
                    settings.GridOptions.GetFriendSummaries(friendSummaryIds[index]),
                    index + 50);
            }

            for (var index = 0; index < categorySummaryIds.Length; index++)
            {
                ConfigureCategorySummaryGrid(
                    settings.GridOptions.GetCategorySummaries(categorySummaryIds[index]),
                    index + 70);
            }

            var roundTrip = JsonConvert.DeserializeObject<PersistedSettings>(
                JsonConvert.SerializeObject(settings));

            for (var index = 0; index < achievementIds.Length; index++)
            {
                AssertAchievementGrid(
                    roundTrip.GridOptions.GetAchievement(achievementIds[index]),
                    index + 10);
            }

            for (var index = 0; index < gameSummaryIds.Length; index++)
            {
                AssertGameSummaryGrid(
                    roundTrip.GridOptions.GetGameSummaries(gameSummaryIds[index]),
                    index + 30);
            }

            for (var index = 0; index < friendSummaryIds.Length; index++)
            {
                AssertFriendSummaryGrid(
                    roundTrip.GridOptions.GetFriendSummaries(friendSummaryIds[index]),
                    index + 50);
            }

            for (var index = 0; index < categorySummaryIds.Length; index++)
            {
                AssertCategorySummaryGrid(
                    roundTrip.GridOptions.GetCategorySummaries(categorySummaryIds[index]),
                    index + 70);
            }
        }

        [TestMethod]
        public void GridOptionsMigration_SeedsStartPageControlBarsDefaultOffWhenMissing()
        {
            const string json = @"{
                ""Persisted"": {
                    ""StartPageGameSummariesGrid"": {
                        ""MaxRows"": 7
                    },
                    ""StartPageRecentUnlocksGrid"": {
                        ""MaxRows"": 8
                    },
                    ""StartPageFriendsRecentUnlocksGrid"": {
                        ""MaxRows"": 9
                    }
                }
            }";

            var migrated = JObject.Parse(GridOptionsSettingsMigration.MigrateFromJson(json));
            var gridOptions = (JObject)migrated["Persisted"]["GridOptions"];

            Assert.IsFalse(gridOptions["GameSummaries"]["StartPage"]["ShowControlBar"].Value<bool>());
            Assert.IsFalse(gridOptions["Achievement"]["StartPageRecent"]["ShowControlBar"].Value<bool>());
            Assert.IsFalse(gridOptions["Achievement"]["StartPageFriendAchievements"]["ShowControlBar"].Value<bool>());
        }

        [TestMethod]
        public void GridOptionsMigration_MigratesStartPageFriendsRecentGrid()
        {
            const string json = @"{
                ""Persisted"": {
                    ""StartPageFriendsRecentUnlocksGrid"": {
                        ""ShowColumnHeaders"": false,
                        ""ShowControlBar"": true,
                        ""RowHeight"": 77.0,
                        ""MaxRows"": 9,
                        ""SortMode"": 2,
                        ""SortDescending"": false
                    },
                    ""StartPageFriendAchievementColumnWidths"": {
                        ""Friend"": 144.0
                    },
                    ""StartPageFriendAchievementColumnOrder"": {
                        ""Friend"": 3
                    }
                }
            }";

            var migrated = JObject.Parse(GridOptionsSettingsMigration.MigrateFromJson(json));
            var options = (JObject)migrated["Persisted"]["GridOptions"]["Achievement"]["StartPageFriendAchievements"];

            Assert.IsFalse(options["ShowColumnHeaders"].Value<bool>());
            Assert.IsTrue(options["ShowControlBar"].Value<bool>());
            Assert.AreEqual(77d, options["RowHeight"].Value<double>());
            Assert.AreEqual(9, options["MaxRows"].Value<int>());
            Assert.AreEqual(2, options["SortMode"].Value<int>());
            Assert.IsFalse(options["SortDescending"].Value<bool>());
            Assert.AreEqual(144d, options["Columns"]["Widths"]["Friend"].Value<double>());
            Assert.AreEqual(3, options["Columns"]["Order"]["Friend"].Value<int>());
        }

        [TestMethod]
        public void GridOptionsMigration_MigratesNewSurfaceColumnSets()
        {
            const string json = @"{
                ""Persisted"": {
                    ""ViewFriendsAchievementsColumnWidths"": {
                        ""Title"": 155.0
                    },
                    ""ViewFriendsAchievementsFriendsColumnWidths"": {
                        ""FriendSummaryFriend"": 166.0
                    },
                    ""ViewFriendsAchievementsCategorySummariesColumnWidths"": {
                        ""Category"": 177.0
                    }
                }
            }";

            var migrated = JObject.Parse(GridOptionsSettingsMigration.MigrateFromJson(json));
            var gridOptions = (JObject)migrated["Persisted"]["GridOptions"];

            Assert.AreEqual(
                155d,
                gridOptions["Achievement"]["ViewFriendsAchievements"]["Columns"]["Widths"]["Title"].Value<double>());
            Assert.AreEqual(
                166d,
                gridOptions["FriendSummaries"]["ViewFriendsAchievements"]["Columns"]["Widths"]["FriendSummaryFriend"].Value<double>());
            Assert.AreEqual(
                177d,
                gridOptions["CategorySummaries"]["ViewFriendsAchievements"]["Columns"]["Widths"]["Category"].Value<double>());
        }

        private static void ConfigureAchievementGrid(AchievementGridOptions options, int seed)
        {
            ConfigureCommonGrid(options, seed);
            options.UseCoverImages = seed % 2 == 0;
            options.ShowRarityGlow = seed % 2 != 0;
            options.ColorNamesByRarity = seed % 2 == 0;
            options.ColorRarityColumnsByRarity = seed % 2 != 0;
            options.UnlockDateMode = (DateDisplayMode)(seed % 3);
            options.SortMode = (CompactListSortMode)(seed % 3);
            options.SortDescending = seed % 2 == 0;
            options.MaxHeight = 500d + seed;
            options.StartInCategoryMode = seed % 2 != 0;
        }

        private static void AssertAchievementGrid(AchievementGridOptions options, int seed)
        {
            AssertCommonGrid(options, seed);
            Assert.AreEqual(seed % 2 == 0, options.UseCoverImages);
            Assert.AreEqual(seed % 2 != 0, options.ShowRarityGlow);
            Assert.AreEqual(seed % 2 == 0, options.ColorNamesByRarity);
            Assert.AreEqual(seed % 2 != 0, options.ColorRarityColumnsByRarity);
            Assert.AreEqual((DateDisplayMode)(seed % 3), options.UnlockDateMode);
            Assert.AreEqual((CompactListSortMode)(seed % 3), options.SortMode);
            Assert.AreEqual(seed % 2 == 0, options.SortDescending);
            Assert.AreEqual(500d + seed, options.MaxHeight);
            Assert.AreEqual(seed % 2 != 0, options.StartInCategoryMode);
        }

        private static void ConfigureGameSummaryGrid(GameSummaryGridOptions options, int seed)
        {
            ConfigureCommonGrid(options, seed);
            options.UseCoverImages = seed % 2 == 0;
            options.ShowMetadataPlatform = seed % 2 != 0;
            options.ShowMetadataPlaytime = seed % 2 == 0;
            options.ShowMetadataRegion = seed % 2 != 0;
            options.ShowCompletionGlow = seed % 2 == 0;
            options.ColorRarityColumnsByRarity = seed % 2 != 0;
            options.ShowNameAboveProgress = seed % 2 == 0;
            options.LastPlayedDateMode = (DateDisplayMode)(seed % 3);
            options.SortMode = (GameSummariesSortMode)(seed % 5);
            options.SortDescending = seed % 2 == 0;
        }

        private static void AssertGameSummaryGrid(GameSummaryGridOptions options, int seed)
        {
            AssertCommonGrid(options, seed);
            Assert.AreEqual(seed % 2 == 0, options.UseCoverImages);
            Assert.AreEqual(seed % 2 != 0, options.ShowMetadataPlatform);
            Assert.AreEqual(seed % 2 == 0, options.ShowMetadataPlaytime);
            Assert.AreEqual(seed % 2 != 0, options.ShowMetadataRegion);
            Assert.AreEqual(seed % 2 == 0, options.ShowCompletionGlow);
            Assert.AreEqual(seed % 2 != 0, options.ColorRarityColumnsByRarity);
            Assert.AreEqual(seed % 2 == 0, options.ShowNameAboveProgress);
            Assert.AreEqual((DateDisplayMode)(seed % 3), options.LastPlayedDateMode);
            Assert.AreEqual((GameSummariesSortMode)(seed % 5), options.SortMode);
            Assert.AreEqual(seed % 2 == 0, options.SortDescending);
        }

        private static void ConfigureFriendSummaryGrid(FriendSummaryGridOptions options, int seed)
        {
            ConfigureCommonGrid(options, seed);
            options.LastUnlockDateMode = (DateDisplayMode)(seed % 3);
        }

        private static void AssertFriendSummaryGrid(FriendSummaryGridOptions options, int seed)
        {
            AssertCommonGrid(options, seed);
            Assert.AreEqual((DateDisplayMode)(seed % 3), options.LastUnlockDateMode);
        }

        private static void ConfigureCategorySummaryGrid(CategorySummaryGridOptions options, int seed)
        {
            ConfigureColumns(options.Columns, seed);
            options.ShowNameAboveProgress = seed % 2 == 0;
        }

        private static void AssertCategorySummaryGrid(CategorySummaryGridOptions options, int seed)
        {
            AssertColumns(options.Columns, seed);
            Assert.AreEqual(seed % 2 == 0, options.ShowNameAboveProgress);
        }

        private static void ConfigureCommonGrid(GridCommonOptions options, int seed)
        {
            options.ShowColumnHeaders = seed % 2 != 0;
            options.ShowControlBar = seed % 2 == 0;
            options.RowHeight = 40d + seed;
            options.MaxRows = 2 + seed;
            ConfigureColumns(options.Columns, seed);
        }

        private static void AssertCommonGrid(GridCommonOptions options, int seed)
        {
            Assert.AreEqual(seed % 2 != 0, options.ShowColumnHeaders);
            Assert.AreEqual(seed % 2 == 0, options.ShowControlBar);
            Assert.AreEqual(40d + seed, options.RowHeight);
            Assert.AreEqual(2 + seed, options.MaxRows);
            AssertColumns(options.Columns, seed);
        }

        private static void ConfigureColumns(GridColumnLayoutOptions options, int seed)
        {
            var key = "Column" + seed;
            options.Visibility[key] = seed % 2 == 0;
            options.Widths[key] = 100d + seed;
            options.Order[key] = seed;
            options.CellAlignments[key] = GridAlignment.Right;
            options.CellVerticalAlignments[key] = GridVerticalAlignment.Bottom;
            options.HeaderAlignments[key] = GridAlignment.Center;
        }

        private static void AssertColumns(GridColumnLayoutOptions options, int seed)
        {
            var key = "Column" + seed;
            Assert.AreEqual(seed % 2 == 0, options.Visibility[key]);
            Assert.AreEqual(100d + seed, options.Widths[key]);
            Assert.AreEqual(seed, options.Order[key]);
            Assert.AreEqual(GridAlignment.Right, options.CellAlignments[key]);
            Assert.AreEqual(GridVerticalAlignment.Bottom, options.CellVerticalAlignments[key]);
            Assert.AreEqual(GridAlignment.Center, options.HeaderAlignments[key]);
        }

        private static void SetGridOptionsBackingField(PersistedSettings settings, GridOptionsCatalog value)
        {
            var field = typeof(PersistedSettings).GetField(
                "_gridOptions",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field);
            field.SetValue(settings, value);
        }
    }
}
