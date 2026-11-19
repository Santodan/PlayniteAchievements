using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.Database;
using PlayniteAchievements.Services.Friends;
using PlayniteAchievements.Services.Overview;
using PlayniteAchievements.Services.Search;

namespace PlayniteAchievements.SqlNado.Tests
{
    /// <summary>
    /// The reads the Overview's Achievements grid and Games grid filter make, on the seeded
    /// database of <see cref="AchievementReadServiceTests"/>, against the row rules the grid
    /// applied in C# before it read through queries.
    /// </summary>
    [TestClass]
    public class OverviewGridQueryTests
    {
        private const string Alpha = AchievementReadServiceTests.Alpha;
        private const string Retro = "aaaaaaaa-0000-0000-0000-000000000003";
        private const string AuthoredOnly = "aaaaaaaa-0000-0000-0000-000000000005";

        // The grid's base read: the unlocks under the summary rules.
        private static readonly AchievementQuery Unlocks = AchievementQuery.CurrentUser.WithSummaryRules().WhereUnlocked(true);

        [TestMethod]
        public void PagedKeys_ReadBackEveryRow_AuthoredOnesByGameAndApiName()
        {
            AchievementReadServiceTests.WithFixture(db =>
            {
                var query = Unlocks.OrderBy(AchievementSort.UnlockTime, descending: true);
                var expected = AchievementReadService.Rows(db, query).Select(Key).ToList();
                var keys = AchievementReadService.SortedKeys(db, query);

                CollectionAssert.AreEqual(expected, AchievementReadService.Rows(db, query, keys).Select(Key).ToList());
                var authored = keys.Where(key => key.PlayniteGameId != null).ToList();
                CollectionAssert.AreEquivalent(
                    new[] { Alpha + "/custom:one", AuthoredOnly + "/custom:solo" },
                    authored.Select(key => key.PlayniteGameId + "/" + key.ApiName).ToList());
                Assert.IsTrue(authored.All(key => key.UserId == null && key.GameId == null && key.DefinitionId == null));
                Assert.IsTrue(keys.Except(authored).All(key => key.ApiName == null && key.DefinitionId != null));

                // A page of authored keys alone, and one key of each kind.
                CollectionAssert.AreEquivalent(
                    authored.Select(key => key.PlayniteGameId + "/" + key.ApiName).ToList(),
                    AchievementReadService.Rows(db, query, authored).Select(row => row.PlayniteGameId + "/" + row.ApiName).ToList());
                var mixed = new[] { authored[0], keys.First(key => key.PlayniteGameId == null) };
                Assert.AreEqual(2, AchievementReadService.Rows(db, query, mixed).Count);
            });
        }

        [TestMethod]
        public void Filters_KeepTheRowsTheGridKeptInCSharp()
        {
            AchievementReadServiceTests.WithFixture(db =>
            {
                var rows = AchievementReadService.Rows(db, Unlocks);
                var from = new DateTime(2025, 3, 2);
                var to = new DateTime(2025, 3, 6);

                void Check(string name, Func<AchievementRowRecord, bool> keep, AchievementQuery query) =>
                    CollectionAssert.AreEquivalent(
                        rows.Where(keep).Select(Key).ToList(),
                        AchievementReadService.Rows(db, query).Select(Key).ToList(),
                        name);

                Check("unlock days", row => Day(row) >= from && Day(row) <= to, Unlocks.WhereUnlockedBetween(from, to));
                Check("open start", row => Day(row) <= to, Unlocks.WhereUnlockedBetween(null, to));
                Check("rarity", row => Tier(row) == RarityTier.Uncommon, Unlocks.WhereRarity(new[] { RarityTier.Uncommon }));
                Check("trophy", row => string.Equals(row.TrophyType, "bronze", StringComparison.OrdinalIgnoreCase), Unlocks.WhereTrophy(new[] { "bronze" }));
                Check("tag", row => AchievementTagHelper.Contains(row.Tags, "Missable"), Unlocks.WhereClassification(new[] { "Missable" }));
                Check("games grid", row => row.PlayniteGameId == Retro, Unlocks.ForGames(new[] { Guid.Parse(Retro) }));
                Check("no listed game", row => false, Unlocks.ForGames(Array.Empty<Guid>()));
                Check(
                    "search",
                    row => SearchQuery.From("gold").Matches(SearchTextBuilder.ForRecentAchievement(row.GameName, row.DisplayName)),
                    Unlocks.WhereSearch("gold"));
                Check(
                    "combined",
                    row => Day(row) >= from && Tier(row) == RarityTier.Uncommon && row.PlayniteGameId == Alpha,
                    Unlocks.WhereUnlockedBetween(from, null).WhereRarity(new[] { RarityTier.Uncommon }).ForGames(new[] { Guid.Parse(Alpha) }));
            });
        }

        [TestMethod]
        public void Capstone_NarrowsThePickedTiers_OrJoinsThem()
        {
            AchievementReadServiceTests.WithFixture(db =>
            {
                var all = AchievementReadService.Rows(db, AchievementQuery.CurrentUser.WithSummaryRules());
                var tiers = new[] { RarityTier.Common };
                CollectionAssert.AreEquivalent(
                    all.Where(row => Tier(row) == RarityTier.Common && row.IsCapstone == 1).Select(Key).ToList(),
                    AchievementReadService.Rows(db, AchievementQuery.CurrentUser.WithSummaryRules().WhereRarity(tiers).WhereCapstone()).Select(Key).ToList());

                var joined = all.Where(row => Tier(row) == RarityTier.Common || row.IsCapstone == 1).Select(Key).ToList();
                Assert.IsTrue(all.Any(row => row.IsCapstone == 1 && Tier(row) != RarityTier.Common), "the fixture has a capstone of another tier");
                CollectionAssert.AreEquivalent(
                    joined,
                    AchievementReadService.Rows(db, AchievementQuery.CurrentUser.WithSummaryRules().WhereRarity(tiers).WhereCapstone(orRarity: true)).Select(Key).ToList());
                CollectionAssert.AreEquivalent(
                    all.Where(row => row.IsCapstone == 1).Select(Key).ToList(),
                    AchievementReadService.Rows(db, AchievementQuery.CurrentUser.WithSummaryRules().WhereCapstone(orRarity: true)).Select(Key).ToList(),
                    "joined with no tier picked keeps the capstones");
            });
        }

        [TestMethod]
        public void GamesGridFilter_KeepsTheGamesOfTheMatchingUnlocks()
        {
            AchievementReadServiceTests.WithFixture(db =>
            {
                var rows = AchievementReadService.Rows(db, Unlocks);
                foreach (var query in new[]
                         {
                             Unlocks.WhereRarity(new[] { RarityTier.Rare }),
                             Unlocks.WhereTrophy(new[] { "platinum" }),
                             Unlocks.WhereUnlockedBetween(new DateTime(2025, 5, 1), null)
                         })
                {
                    var expected = AchievementReadService.Rows(db, query).Select(row => row.PlayniteGameId).Distinct().ToList();
                    Assert.IsTrue(expected.Count < rows.Select(row => row.PlayniteGameId).Distinct().Count());
                    CollectionAssert.AreEquivalent(expected, AchievementReadService.Keys(db, query, AchievementGroup.Game).ToList());
                }
            });
        }

        [TestMethod]
        public void EverySortableColumn_KeepsEveryRow_AndTheDefaultPutsUndatedUnlocksLast()
        {
            AchievementReadServiceTests.WithFixture(db =>
            {
                var expected = AchievementReadService.Rows(db, Unlocks).Select(Key).ToList();
                foreach (var column in new[]
                         {
                             "UnlockTime", "GlobalPercent", "RaritySortValue", "DisplayName", "GameName", "SortingName", "Points",
                             "TrophyType", "CategoryLabel", "CategoryKind", "CategoryType", "HasAchievementNote", "CollectionScore", "PrestigeScore"
                         })
                {
                    var sort = AchievementQuery.SortOfColumn(column);
                    Assert.IsNotNull(sort, column);
                    foreach (var descending in new[] { false, true })
                    {
                        var query = Unlocks.OrderBy(sort.Value, descending);
                        CollectionAssert.AreEquivalent(expected, AchievementReadService.Rows(db, query).Select(Key).ToList(), column);
                        Assert.AreEqual(expected.Count, AchievementReadService.SortedKeys(db, query).Count, column);
                    }
                }

                Assert.IsNull(AchievementQuery.SortOfColumn("Status"));

                var newest = AchievementReadService.Rows(db, Unlocks.OrderBy(AchievementSort.UnlockTime, descending: true));
                var times = newest.Select(row => AchievementSqlFunctions.ParseUtc(row.UnlockTimeUtc)).ToList();
                Assert.IsNull(times.Last(), "a2's cleared time comes last");
                CollectionAssert.AreEqual(
                    times.Where(time => time.HasValue).OrderByDescending(time => time).ToList(),
                    times.Where(time => time.HasValue).ToList());
            });
        }

        [TestMethod]
        public void FriendDefinitionChanges_NameTheLibraryGamesOfTheirProviderGames()
        {
            AchievementReadServiceTests.WithFixture(db =>
            {
                CollectionAssert.AreEquivalent(
                    new[] { Guid.Parse(Alpha) },
                    AchievementReadService.LibraryGamesOf(db, new[]
                    {
                        FriendCacheChange.ForGameDefinition("Steam", 100, null),
                        FriendCacheChange.ForGameDefinition("Steam", 400, null),
                        FriendCacheChange.ForGameDefinition("Exophase", 100, null)
                    }).ToList(),
                    "the outside game and another provider's id name no library game");
                CollectionAssert.AreEquivalent(
                    new[] { Guid.Parse("aaaaaaaa-0000-0000-0000-000000000004") },
                    AchievementReadService.LibraryGamesOf(db, new[] { FriendCacheChange.ForGameDefinition("Exophase", 0, "multi-exo") }).ToList(),
                    "by provider game key");
            });
        }

        private static string Key(AchievementRowRecord row) => row.UserId + "/" + row.PlayniteGameId + "/" + row.ApiName;

        private static DateTime? Day(AchievementRowRecord row)
        {
            var time = AchievementSqlFunctions.ParseUtc(row.UnlockTimeUtc);
            return time.HasValue ? UnlockDayCounts.DayOf(time.Value) : (DateTime?)null;
        }

        private static RarityTier Tier(AchievementRowRecord row) =>
            AchievementSqlFunctions.ParseStoredRarity(row.Rarity);
    }
}
