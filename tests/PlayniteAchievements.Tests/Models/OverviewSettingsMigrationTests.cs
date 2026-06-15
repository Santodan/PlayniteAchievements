using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using PlayniteAchievements.Models.Settings;

namespace PlayniteAchievements.Models.Tests
{
    [TestClass]
    public class OverviewSettingsMigrationTests
    {
        [TestMethod]
        public void MigrateFromJson_RenamesOverviewAndGameSummarySettings()
        {
            const string json =
                @"{
                    ""Persisted"": {
                        ""ShowSidebarCollectionScoreCard"": false,
                        ""ShowSidebarGameMetadata"": false,
                        ""SidebarPieSmallSliceMode"": ""Hide"",
                        ""GamesOverviewGridSortMode"": ""Alphabetical"",
                        ""GamesOverviewGridSortDescending"": false,
                        ""SidebarOverviewGridRowHeight"": 84.0,
                        ""SidebarOverviewGridMaxRows"": 3,
                        ""SidebarSelectedGameGridSortMode"": ""None"",
                        ""SidebarSelectedGameGridSortDescending"": false,
                        ""SidebarOverviewLeftColumnRatio"": 0.64,
                        ""SidebarTimelineRange"": ""SixMonths"",
                        ""ShowOverviewGridColumnHeaders"": false,
                        ""StartPageGamesOverviewGrid"": {
                            ""RowHeight"": 72.0,
                            ""MaxRows"": 11,
                            ""SortMode"": ""Progress"",
                            ""SortDescending"": false
                        }
                    }
                }";

            var migrated = JObject.Parse(OverviewSettingsMigration.MigrateFromJson(json));
            var persisted = (JObject)migrated["Persisted"];

            Assert.AreEqual(false, persisted["ShowOverviewCollectionScoreCard"].Value<bool>());
            Assert.AreEqual(false, persisted["ShowOverviewGameMetadata"].Value<bool>());
            Assert.AreEqual("Hide", persisted["OverviewPieSmallSliceMode"].Value<string>());
            Assert.AreEqual("Alphabetical", persisted["OverviewGameSummariesGridSortMode"].Value<string>());
            Assert.AreEqual(false, persisted["OverviewGameSummariesGridSortDescending"].Value<bool>());
            Assert.AreEqual(84.0, persisted["OverviewGameSummariesGridRowHeight"].Value<double>());
            Assert.AreEqual(3, persisted["OverviewGameSummariesGridMaxRows"].Value<int>());
            Assert.AreEqual("None", persisted["OverviewSelectedGameGridSortMode"].Value<string>());
            Assert.AreEqual(false, persisted["OverviewSelectedGameGridSortDescending"].Value<bool>());
            Assert.AreEqual(0.64, persisted["OverviewLeftColumnRatio"].Value<double>());
            Assert.AreEqual("SixMonths", persisted["OverviewTimelineRange"].Value<string>());
            Assert.AreEqual(false, persisted["ShowOverviewGameSummariesGridColumnHeaders"].Value<bool>());
            Assert.IsNotNull(persisted["StartPageGameSummariesGrid"]);
            Assert.IsNull(persisted["ShowSidebarCollectionScoreCard"]);
            Assert.IsNull(persisted["GamesOverviewGridSortMode"]);
            Assert.IsNull(persisted["StartPageGamesOverviewGrid"]);
        }

        [TestMethod]
        public void MigrateFromJson_RenamesColumnDictionariesAndKeys()
        {
            const string json =
                @"{
                    ""Persisted"": {
                        ""GamesOverviewColumnWidths"": {
                            ""OverviewGameName"": 500.0,
                            ""OverviewProvider"": 40.0,
                            ""TotalAchievements"": 180.0
                        },
                        ""StartPageGamesOverviewColumnOrder"": {
                            ""OverviewProgression"": 2,
                            ""GameSummaryName"": 1
                        },
                        ""SidebarAchievementColumnWidths"": {
                            ""Achievement"": 520.0
                        },
                        ""SidebarGameColumnAlignments"": {
                            ""Rarity"": ""Right""
                        }
                    }
                }";

            var migrated = JObject.Parse(OverviewSettingsMigration.MigrateFromJson(json));
            var persisted = (JObject)migrated["Persisted"];
            var overviewWidths = (JObject)persisted["OverviewGameSummariesColumnWidths"];
            var startPageOrder = (JObject)persisted["StartPageGameSummariesColumnOrder"];

            Assert.AreEqual(500.0, overviewWidths["GameSummaryName"].Value<double>());
            Assert.AreEqual(40.0, overviewWidths["GameSummaryProvider"].Value<double>());
            Assert.AreEqual(180.0, overviewWidths["TotalAchievements"].Value<double>());
            Assert.IsNull(overviewWidths["OverviewGameName"]);
            Assert.AreEqual(2, startPageOrder["GameSummaryProgression"].Value<int>());
            Assert.AreEqual(1, startPageOrder["GameSummaryName"].Value<int>());
            Assert.IsNotNull(persisted["OverviewRecentAchievementColumnWidths"]);
            Assert.IsNotNull(persisted["OverviewSelectedGameAchievementColumnAlignments"]);
            Assert.IsNull(persisted["GamesOverviewColumnWidths"]);
            Assert.IsNull(persisted["SidebarAchievementColumnWidths"]);
            Assert.IsNull(persisted["SidebarGameColumnAlignments"]);
        }

        [TestMethod]
        public void MigrateFromJson_PreservesExistingNewValuesWhenBothNamesExist()
        {
            const string json =
                @"{
                    ""Persisted"": {
                        ""ShowSidebarGameMetadata"": false,
                        ""ShowOverviewGameMetadata"": true,
                        ""GamesOverviewColumnOrder"": { ""OverviewGameName"": 2 },
                        ""OverviewGameSummariesColumnOrder"": { ""GameSummaryName"": 1 }
                    }
                }";

            var migrated = JObject.Parse(OverviewSettingsMigration.MigrateFromJson(json));
            var persisted = (JObject)migrated["Persisted"];
            var order = (JObject)persisted["OverviewGameSummariesColumnOrder"];

            Assert.AreEqual(true, persisted["ShowOverviewGameMetadata"].Value<bool>());
            Assert.AreEqual(1, order["GameSummaryName"].Value<int>());
            Assert.IsNull(persisted["ShowSidebarGameMetadata"]);
            Assert.IsNull(persisted["GamesOverviewColumnOrder"]);
        }

        [TestMethod]
        public void MigrateFromJson_RenamesIntermediateGameSummariesSettings()
        {
            const string json =
                @"{
                    ""Persisted"": {
                        ""ShowGameSummariesGridColumnHeaders"": false,
                        ""GameSummariesGridSortMode"": ""Alphabetical"",
                        ""GameSummariesGridSortDescending"": false,
                        ""GameSummariesColumnHeaderAlignments"": {
                            ""OverviewGameName"": ""Right""
                        },
                        ""GameSummariesColumnVerticalAlignments"": {
                            ""OverviewProvider"": ""Bottom""
                        }
                    }
                }";

            var migrated = JObject.Parse(OverviewSettingsMigration.MigrateFromJson(json));
            var persisted = (JObject)migrated["Persisted"];
            var headerAlignments = (JObject)persisted["OverviewGameSummariesColumnHeaderAlignments"];
            var verticalAlignments = (JObject)persisted["OverviewGameSummariesColumnVerticalAlignments"];

            Assert.AreEqual(false, persisted["ShowOverviewGameSummariesGridColumnHeaders"].Value<bool>());
            Assert.AreEqual("Alphabetical", persisted["OverviewGameSummariesGridSortMode"].Value<string>());
            Assert.AreEqual(false, persisted["OverviewGameSummariesGridSortDescending"].Value<bool>());
            Assert.AreEqual("Right", headerAlignments["GameSummaryName"].Value<string>());
            Assert.AreEqual("Bottom", verticalAlignments["GameSummaryProvider"].Value<string>());
            Assert.IsNull(persisted["ShowGameSummariesGridColumnHeaders"]);
            Assert.IsNull(persisted["GameSummariesGridSortMode"]);
            Assert.IsNull(persisted["GameSummariesColumnHeaderAlignments"]);
        }

        [TestMethod]
        public void MigrateFromJson_CopiesLegacyAchievementColumnVisibilityToScopeMaps()
        {
            const string json =
                @"{
                    ""Persisted"": {
                        ""DataGridColumnVisibility"": {
                            ""Title"": false,
                            ""Rarity"": true
                        }
                    }
                }";

            var migrated = JObject.Parse(OverviewSettingsMigration.MigrateFromJson(json));
            var persisted = (JObject)migrated["Persisted"];
            var recentVisibility = (JObject)persisted["OverviewRecentAchievementColumnVisibility"];
            var selectedVisibility = (JObject)persisted["OverviewSelectedGameAchievementColumnVisibility"];
            var singleGameVisibility = (JObject)persisted["SingleGameColumnVisibility"];

            Assert.IsFalse(recentVisibility["Title"].Value<bool>());
            Assert.IsTrue(recentVisibility["Rarity"].Value<bool>());
            Assert.IsFalse(selectedVisibility["Title"].Value<bool>());
            Assert.IsTrue(selectedVisibility["Rarity"].Value<bool>());
            Assert.IsFalse(singleGameVisibility["Title"].Value<bool>());
            Assert.IsTrue(singleGameVisibility["Rarity"].Value<bool>());
            Assert.IsNotNull(persisted["DataGridColumnVisibility"]);
        }

        [TestMethod]
        public void MigrateFromJson_DoesNotOverwriteExistingAchievementColumnVisibilityMaps()
        {
            const string json =
                @"{
                    ""Persisted"": {
                        ""DataGridColumnVisibility"": {
                            ""Title"": false
                        },
                        ""OverviewRecentAchievementColumnVisibility"": {
                            ""Icon"": false
                        },
                        ""OverviewSelectedGameAchievementColumnVisibility"": {
                            ""Rarity"": false
                        },
                        ""SingleGameColumnVisibility"": {
                            ""Points"": false
                        }
                    }
                }";

            var migrated = JObject.Parse(OverviewSettingsMigration.MigrateFromJson(json));
            var persisted = (JObject)migrated["Persisted"];
            var recentVisibility = (JObject)persisted["OverviewRecentAchievementColumnVisibility"];
            var selectedVisibility = (JObject)persisted["OverviewSelectedGameAchievementColumnVisibility"];
            var singleGameVisibility = (JObject)persisted["SingleGameColumnVisibility"];

            Assert.IsFalse(recentVisibility["Icon"].Value<bool>());
            Assert.IsNull(recentVisibility["Title"]);
            Assert.IsFalse(selectedVisibility["Rarity"].Value<bool>());
            Assert.IsNull(selectedVisibility["Title"]);
            Assert.IsFalse(singleGameVisibility["Points"].Value<bool>());
            Assert.IsNull(singleGameVisibility["Title"]);
        }
    }
}
