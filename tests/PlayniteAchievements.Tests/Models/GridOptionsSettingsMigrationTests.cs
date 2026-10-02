using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using PlayniteAchievements.Models.Settings;

namespace PlayniteAchievements.Models.Tests
{
    [TestClass]
    public class GridOptionsSettingsMigrationTests
    {
        [TestMethod]
        public void MigrateFromJson_SeedsSingleGameAppearanceFromOverviewSelectedGame()
        {
            // The View Achievements window used to follow the overview selected-game options, so a
            // config written before the switch only carries the values on that entry.
            const string json = @"{
                ""Persisted"": {
                    ""GridOptions"": {
                        ""Achievement"": {
                            ""OverviewSelectedGame"": {
                                ""ShowRarityGlow"": false,
                                ""ColorNamesByRarity"": true
                            }
                        }
                    }
                }
            }";

            var singleGame = MigrateSingleGame(json);

            Assert.IsFalse(singleGame["ShowRarityGlow"].Value<bool>());
            Assert.IsTrue(singleGame["ColorNamesByRarity"].Value<bool>());
        }

        [TestMethod]
        public void MigrateFromJson_KeepsExistingSingleGameAppearance()
        {
            const string json = @"{
                ""Persisted"": {
                    ""GridOptions"": {
                        ""Achievement"": {
                            ""OverviewSelectedGame"": { ""ShowRarityGlow"": false },
                            ""SingleGame"": { ""ShowRarityGlow"": true }
                        }
                    }
                }
            }";

            var singleGame = MigrateSingleGame(json);

            Assert.IsTrue(singleGame["ShowRarityGlow"].Value<bool>());
        }

        [TestMethod]
        public void MigrateFromJson_LeavesSingleGameUnseeded_WhenOverviewSelectedGameHasNoAppearance()
        {
            const string json = @"{ ""Persisted"": { ""GlobalLanguage"": ""english"" } }";

            var achievement = (JObject)JObject.Parse(GridOptionsSettingsMigration.MigrateFromJson(json))
                ["Persisted"]?["GridOptions"]?["Achievement"];

            Assert.IsNull(achievement?["SingleGame"]?["ShowRarityGlow"]);
        }

        private static JObject MigrateSingleGame(string json)
        {
            var migrated = JObject.Parse(GridOptionsSettingsMigration.MigrateFromJson(json));
            return (JObject)migrated["Persisted"]["GridOptions"]["Achievement"]["SingleGame"];
        }

        [TestMethod]
        public void MigrateFromJson_SeedsCategoryProgressRightWhereMissingAndStampsFlag()
        {
            // Written by a build that seeded Right on the deserialization target: an entry with a
            // cleared key, one with the user's own Center, and one with no column data at all.
            var json = new JObject
            {
                ["Persisted"] = new JObject
                {
                    ["GridOptions"] = new JObject
                    {
                        ["CategorySummaries"] = new JObject
                        {
                            [GridOptionKeys.CategorySummaries.ViewAchievements] = new JObject
                            {
                                ["Columns"] = new JObject
                                {
                                    ["CellAlignments"] = new JObject { ["GameSummaryName"] = 1 }
                                }
                            },
                            [GridOptionKeys.CategorySummaries.OverviewSelectedGame] = new JObject
                            {
                                ["Columns"] = new JObject
                                {
                                    ["CellAlignments"] = new JObject { ["GameSummaryProgression"] = 1 }
                                }
                            },
                            [GridOptionKeys.CategorySummaries.FriendsOverview] = new JObject()
                        }
                    }
                }
            }.ToString();

            var persisted = MigratePersisted(json);
            var group = (JObject)persisted["GridOptions"]["CategorySummaries"];

            var viewAchievements = group[GridOptionKeys.CategorySummaries.ViewAchievements]["Columns"]["CellAlignments"];
            Assert.AreEqual((int)GridAlignment.Right, viewAchievements["GameSummaryProgression"].Value<int>());
            Assert.AreEqual(1, viewAchievements["GameSummaryName"].Value<int>());

            // The user's own choice on another surface is left alone.
            Assert.AreEqual(
                (int)GridAlignment.Center,
                group[GridOptionKeys.CategorySummaries.OverviewSelectedGame]["Columns"]["CellAlignments"]["GameSummaryProgression"].Value<int>());

            // An entry without column data gets the objects created and the key filled.
            Assert.AreEqual(
                (int)GridAlignment.Right,
                group[GridOptionKeys.CategorySummaries.FriendsOverview]["Columns"]["CellAlignments"]["GameSummaryProgression"].Value<int>());

            // Entries absent from the JSON are not created; the catalog seeds them at runtime.
            Assert.IsNull(group[GridOptionKeys.CategorySummaries.ViewFriendsAchievements]);
            Assert.IsNull(group[GridOptionKeys.CategorySummaries.DesktopTheme]);

            Assert.IsTrue(persisted["CategoryProgressColumnAlignmentDefaulted"].Value<bool>());
        }

        [TestMethod]
        public void MigrateFromJson_KeepsClearedCategoryProgressAlignmentOnceFlagIsSet()
        {
            var json = new JObject
            {
                ["Persisted"] = new JObject
                {
                    ["CategoryProgressColumnAlignmentDefaulted"] = true,
                    ["GridOptions"] = new JObject
                    {
                        ["CategorySummaries"] = new JObject
                        {
                            [GridOptionKeys.CategorySummaries.ViewAchievements] = new JObject
                            {
                                ["Columns"] = new JObject
                                {
                                    ["CellAlignments"] = new JObject { ["GameSummaryName"] = 1 }
                                }
                            }
                        }
                    }
                }
            }.ToString();

            var persisted = MigratePersisted(json);

            // Already defaulted: the absent key is the user's cleared override and stays absent.
            Assert.IsNull(
                persisted["GridOptions"]["CategorySummaries"][GridOptionKeys.CategorySummaries.ViewAchievements]
                    ["Columns"]["CellAlignments"]["GameSummaryProgression"]);
            Assert.IsTrue(persisted["CategoryProgressColumnAlignmentDefaulted"].Value<bool>());
        }

        [TestMethod]
        public void MigrateFromJson_StampsCategoryProgressFlagWithoutCreatingEntries()
        {
            const string json = @"{ ""Persisted"": { ""GlobalLanguage"": ""english"" } }";

            var persisted = MigratePersisted(json);

            Assert.IsTrue(persisted["CategoryProgressColumnAlignmentDefaulted"].Value<bool>());
            Assert.IsNull(persisted["GridOptions"]?["CategorySummaries"]);
        }

        private static JObject MigratePersisted(string json)
        {
            var migrated = JObject.Parse(GridOptionsSettingsMigration.MigrateFromJson(json));
            return (JObject)migrated["Persisted"];
        }

        [TestMethod]
        public void ShowcaseSurfaceDefaults_PreserveSourceOrder()
        {
            var catalog = new GridOptionsCatalog();

            // None keeps the projection's source order (recency, or pin order for the pinned
            // source); game grids default to the recent-unlock sort with PinOrder available.
            Assert.AreEqual(
                CompactListSortMode.None,
                catalog.GetAchievement("ShowcaseRecentAchievements:abc").SortMode);
            Assert.AreEqual(
                GameSummariesSortMode.RecentUnlock,
                catalog.GetGameSummaries("ShowcaseGameSummaries:abc").SortMode);
        }
    }
}
