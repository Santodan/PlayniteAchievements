using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.ViewModels.Items;
using PlayniteAchievements.Views.Settings.Controls;

namespace PlayniteAchievements.Models.Tests
{
    [TestClass]
    public class GridSortColumnsTests
    {
        [TestMethod]
        public void AchievementRecord_ReadsLegacyEnumSortAsColumn()
        {
            Assert.AreEqual("RaritySortValue", JsonConvert.DeserializeObject<AchievementGridOptions>(@"{ ""SortMode"": 2 }").SortColumn);
            Assert.AreEqual("UnlockTime", JsonConvert.DeserializeObject<AchievementGridOptions>(@"{ ""SortMode"": ""UnlockTime"" }").SortColumn);
            Assert.AreEqual(GridSortColumns.SourceOrder, JsonConvert.DeserializeObject<AchievementGridOptions>(@"{ ""SortMode"": 0 }").SortColumn);
        }

        [TestMethod]
        public void GameSummaryRecord_ReadsLegacyEnumSortAsColumn()
        {
            Assert.AreEqual("SortingName", JsonConvert.DeserializeObject<GameSummaryGridOptions>(@"{ ""SortMode"": 4 }").SortColumn);
            Assert.AreEqual("RemainingAchievements", JsonConvert.DeserializeObject<GameSummaryGridOptions>(@"{ ""SortMode"": ""Remaining"" }").SortColumn);
            Assert.AreEqual(GridSortColumns.SourceOrder, JsonConvert.DeserializeObject<GameSummaryGridOptions>(@"{ ""SortMode"": ""PinOrder"" }").SortColumn);
        }

        [TestMethod]
        public void SettingsModels_KeepJsonPropertiesPublic()
        {
            // Playnite's settings serializer skips non-public members even with [JsonProperty], so a
            // private legacy reader works under JsonConvert and silently drops the value in Playnite.
            const BindingFlags NonPublic = BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            var offenders = typeof(AchievementGridOptions).Assembly
                .GetTypes()
                .Where(type => type.Namespace?.StartsWith("PlayniteAchievements.Models", System.StringComparison.Ordinal) == true)
                .SelectMany(type => type.GetProperties(NonPublic).Cast<MemberInfo>()
                    .Concat(type.GetFields(NonPublic)))
                .Where(member => member.GetCustomAttribute<JsonPropertyAttribute>() != null)
                .Select(member => member.DeclaringType.Name + "." + member.Name)
                .ToList();

            CollectionAssert.AreEqual(new List<string>(), offenders, string.Join(", ", offenders));
        }

        [TestMethod]
        public void Records_WriteOnlyTheColumnKey()
        {
            var achievement = JObject.Parse(JsonConvert.SerializeObject(new AchievementGridOptions { SortColumn = "Points" }));
            var game = JObject.Parse(JsonConvert.SerializeObject(new GameSummaryGridOptions { SortColumn = "PrestigeScore" }));

            Assert.AreEqual("Points", achievement["SortColumn"].Value<string>());
            Assert.IsNull(achievement[GridSortColumns.LegacySortModeName]);
            Assert.AreEqual("PrestigeScore", game["SortColumn"].Value<string>());
            Assert.IsNull(game[GridSortColumns.LegacySortModeName]);
            Assert.AreEqual(
                "Points",
                JsonConvert.DeserializeObject<AchievementGridOptions>(achievement.ToString()).SortColumn);
        }

        [TestMethod]
        public void Records_DefaultToTheReleasedDefaultSorts()
        {
            Assert.AreEqual("UnlockTime", new AchievementGridOptions().SortColumn);
            Assert.AreEqual("LastUnlockUtc", new GameSummaryGridOptions().SortColumn);
            Assert.AreEqual(GridSortColumns.SourceOrder, new AchievementGridOptions { SortColumn = null }.SortColumn);
        }

        [TestMethod]
        public void Migration_DoesNotGiveALegacySortToARecordWithAColumnSort()
        {
            // A legacy flat value still in the file must not land after the record's column key,
            // where the record would read it last and lose the user's choice.
            const string json = @"{
                ""Persisted"": {
                    ""OverviewGameSummariesGridSortMode"": ""Alphabetical"",
                    ""GridOptions"": {
                        ""GameSummaries"": {
                            ""OverviewGameSummaries"": { ""SortColumn"": ""Points"" }
                        }
                    }
                }
            }";

            var migrated = JObject.Parse(GridOptionsSettingsMigration.MigrateFromJson(json));
            var record = (JObject)migrated["Persisted"]["GridOptions"]["GameSummaries"]["OverviewGameSummaries"];

            Assert.IsNull(record[GridSortColumns.LegacySortModeName]);
            Assert.AreEqual("Points", record.ToObject<GameSummaryGridOptions>().SortColumn);
        }

        [TestMethod]
        public void Catalogs_ListEachSortOnce()
        {
            foreach (var columns in new[] { GridSortColumns.Achievement, GridSortColumns.GameSummaries })
            {
                var keys = columns.Select(column => column.Key).ToList();
                CollectionAssert.AllItemsAreUnique(keys);
                Assert.IsFalse(keys.Any(GridSortColumns.IsSourceOrder));
            }
        }

        [TestMethod]
        public void AchievementCatalog_EverySortHasAComparer()
        {
            foreach (var column in GridSortColumns.Achievement)
            {
                Assert.IsNotNull(
                    AchievementSortHelper.GetComparison(column.Key, ListSortDirection.Ascending, AchievementSortScope.RecentAchievements),
                    column.Key);
            }
        }

        [TestMethod]
        public void GameCatalog_EverySortHasAComparer()
        {
            foreach (var column in GridSortColumns.GameSummaries)
            {
                var items = new List<GameSummaryItem>();
                string currentPath = null;
                var currentDirection = ListSortDirection.Ascending;
                Assert.IsTrue(
                    GameSummariesSortHelper.TrySortItems(items, column.Key, ListSortDirection.Descending, ref currentPath, ref currentDirection),
                    column.Key);
            }
        }

        [TestMethod]
        public void LegacyModes_MapIntoTheCatalogs()
        {
            foreach (CompactListSortMode mode in System.Enum.GetValues(typeof(CompactListSortMode)))
            {
                var key = GridSortColumns.FromAchievementMode(mode);
                Assert.IsTrue(GridSortColumns.IsSourceOrder(key) || GridSortColumns.Achievement.Any(column => column.Key == key), mode.ToString());
            }

            foreach (GameSummariesSortMode mode in System.Enum.GetValues(typeof(GameSummariesSortMode)))
            {
                var key = GridSortColumns.FromGameSummariesMode(mode);
                Assert.IsTrue(GridSortColumns.IsSourceOrder(key) || GridSortColumns.GameSummaries.Any(column => column.Key == key), mode.ToString());
            }
        }

        [TestMethod]
        public void Offered_FollowsEachSurfacesColumns()
        {
            List<string> Offered(GridOptionKind kind, string surface) => GridSortColumns
                .Offered(
                    kind == GridOptionKind.Achievement ? GridSortColumns.Achievement : GridSortColumns.GameSummaries,
                    GridDisplaySurfaces.Resolve(kind, surface).HasSortScope)
                .Select(column => column.Key)
                .ToList();

            var singleGame = Offered(GridOptionKind.Achievement, GridOptionKeys.Achievement.SingleGame);
            var recent = Offered(GridOptionKind.Achievement, GridOptionKeys.Achievement.OverviewRecent);
            var friendAchievements = Offered(GridOptionKind.Achievement, GridOptionKeys.Achievement.ViewFriendsAchievements);
            var overview = Offered(GridOptionKind.GameSummaries, GridOptionKeys.GameSummaries.Overview);
            var allFriends = Offered(GridOptionKind.GameSummaries, GridOptionKeys.GameSummaries.FriendsOverview);
            var oneFriend = Offered(GridOptionKind.GameSummaries, GridOptionKeys.GameSummaries.FriendsOverviewSelectedFriend);

            CollectionAssert.DoesNotContain(singleGame, "SortingName");
            CollectionAssert.DoesNotContain(singleGame, "FriendName");
            CollectionAssert.Contains(singleGame, "Points");
            CollectionAssert.Contains(recent, "SortingName");
            CollectionAssert.Contains(friendAchievements, "FriendName");
            CollectionAssert.Contains(overview, "Added");
            CollectionAssert.Contains(overview, "Progression");
            CollectionAssert.DoesNotContain(overview, "Owned");
            CollectionAssert.Contains(allFriends, "Owned");
            CollectionAssert.DoesNotContain(allFriends, "Progression");
            CollectionAssert.DoesNotContain(allFriends, "Points");
            CollectionAssert.Contains(oneFriend, "Progression");
            CollectionAssert.DoesNotContain(oneFriend, "PlaytimeSeconds");
        }

        [TestMethod]
        public void LabelKeyFor_NamesSharedSortsAfterTheCatalog()
        {
            Assert.AreEqual("LOCPlayAch_Column_Rarity", GridSortColumns.LabelKeyFor(GridSortColumns.Achievement, "RaritySortValue"));
            Assert.AreEqual("LOCPlayAch_Column_Achievement", GridSortColumns.LabelKeyFor(GridSortColumns.Achievement, "DisplayName"));
            Assert.IsNull(GridSortColumns.LabelKeyFor(GridSortColumns.Achievement, "Unknown"));
        }

        [TestMethod]
        public void GameSortIndicator_FollowsTheSortColumn()
        {
            Assert.AreEqual("LastUnlockUtc", new GameSummariesSortSpec("LastUnlockUtc", ListSortDirection.Descending).IndicatorSortMemberPath);
            Assert.AreEqual("Added", new GameSummariesSortSpec("Added", ListSortDirection.Descending).IndicatorSortMemberPath);
            Assert.IsNull(new GameSummariesSortSpec(GridSortColumns.SourceOrder, ListSortDirection.Descending).SortMemberPath);
            Assert.IsNull(new AchievementSortSpec(GridSortColumns.SourceOrder, ListSortDirection.Descending).SortMemberPath);
        }
    }
}
