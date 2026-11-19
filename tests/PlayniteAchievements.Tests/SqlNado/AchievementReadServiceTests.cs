using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Achievements.Scoring;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.Database;
using PlayniteAchievements.Services.Friends;
using PlayniteAchievements.Services.Overview;
using PlayniteAchievements.Services.Search;
using PlayniteAchievements.Services.Summaries;
using PlayniteAchievements.ViewModels.Items;
using SqlNado;

namespace PlayniteAchievements.SqlNado.Tests
{
    /// <summary>
    /// Runs <see cref="AchievementReadService"/> on a seeded database and compares it with the C#
    /// rules the summary and friends paths apply.
    /// </summary>
    [TestClass]
    public class AchievementReadServiceTests
    {
        internal const string Alpha = "aaaaaaaa-0000-0000-0000-000000000001";
        private const string Beta = "aaaaaaaa-0000-0000-0000-000000000002";
        private const string Retro = "aaaaaaaa-0000-0000-0000-000000000003";
        private const string Multi = "aaaaaaaa-0000-0000-0000-000000000004";
        private const string AuthoredOnly = "aaaaaaaa-0000-0000-0000-000000000005";

        private const string A1Raw = "2025-03-01T23:30:00.0000000Z";
        private const string A1Override = "2025-03-10T23:45:00.0000000Z";
        private const string A2Raw = "2025-03-02T10:00:00.0000000Z";

        [TestMethod]
        public void DayTotals_MatchTheLocalDayOfEachUnlock()
        {
            WithFixture(db =>
            {
                var query = AchievementQuery.CurrentUser.WithSummaryRules().WhereUnlocked(true);
                var expected = AchievementReadService.Rows(db, query)
                    .Select(row => AchievementSqlFunctions.ParseUtc(row.UnlockTimeUtc))
                    .Where(time => time.HasValue)
                    .GroupBy(time => UnlockDayCounts.DayOf(time.Value).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                    .ToDictionary(group => group.Key, group => (long)group.Count());

                var actual = AchievementReadService.Totals(db, query, AchievementGroup.Day)
                    .Where(row => row.Key != null)
                    .ToDictionary(row => row.Key, row => row.Count);

                CollectionAssert.AreEquivalent(expected.ToList(), actual.ToList());
                Assert.IsTrue(actual.ContainsKey(UnlockDayCounts.DayOf(DateTime.Parse(A1Override, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                    "a1 counts on its overridden day");
                Assert.AreEqual(1L, AchievementReadService.Totals(db, query, AchievementGroup.Day).Single(row => row.Key == null).Count,
                    "a2's cleared time has no day");
            });
        }

        [TestMethod]
        public void FriendRows_CarryTheUsersOwnResolvedUnlockTimes()
        {
            WithFixture(db =>
            {
                var rows = AchievementReadService.Rows(db, AchievementQuery.AllFriends.WhereUnlocked(true));

                Assert.AreEqual(5, rows.Count, "A: a1, a3, o1; B: a2, a6; not the inactive friend C");
                // The user's override (a1) and clear (a2) of their own unlock time.
                Assert.AreEqual(A1Override, rows.Single(row => row.ApiName == "a1").SelfUnlockTimeUtc);
                Assert.IsNull(rows.Single(row => row.ApiName == "a2").SelfUnlockTimeUtc);
                Assert.AreEqual(1L, rows.Single(row => row.ApiName == "a1").SelfUnlocked);
                Assert.AreEqual(0L, rows.Single(row => row.ApiName == "o1").SelfHasGameData, "a game outside the library");
            });
        }

        [TestMethod]
        public void FriendTotals_MatchTheFriendSummaryScores()
        {
            WithFixture(db =>
            {
                var items = AchievementReadService.Rows(db, AchievementQuery.AllFriends.WhereUnlocked(true)).Select(row =>
                {
                    var rarity = AchievementSqlFunctions.ParseStoredRarity(row.Rarity);
                    var percent = row.GlobalPercentUnlocked;
                    return new FriendAchievementDisplayItem
                    {
                        ProviderKey = row.ProviderKey,
                        FriendExternalUserId = row.ExternalUserId,
                        Rarity = rarity,
                        GlobalPercentUnlocked = percent,
                        TrophyType = row.TrophyType,
                        Unlocked = true,
                        CollectionScore = AchievementScoreCalculator.GetCollectionValue(rarity),
                        PrestigeScore = AchievementScoreCalculator.GetPrestigeValue(percent, rarity)
                    };
                }).ToList();
                var totals = AchievementReadService.Totals(db, AchievementQuery.AllFriends.WhereUnlocked(true), AchievementGroup.Friend)
                    .ToDictionary(row => row.Key);
                Assert.AreEqual(2, totals.Count);
                foreach (var friend in new[] { "a", "b" })
                {
                    var stats = AchievementStatsAccumulator.FromDisplayItems(
                        items.Where(item => item.FriendExternalUserId == friend).ToList(),
                        treatItemsAsUnlocked: true);
                    var total = totals["Steam\u001f" + friend];
                    Assert.AreEqual((long)stats.CollectionScore, total.CollectionScore, friend);
                    Assert.AreEqual((long)stats.PrestigeScore, total.PrestigeScore, friend);
                    Assert.AreEqual((long)stats.CommonCount, total.CommonUnlocked, friend);
                    Assert.AreEqual((long)stats.UncommonCount, total.UncommonUnlocked, friend);
                    Assert.AreEqual((long)stats.RareCount, total.RareUnlocked, friend);
                    Assert.AreEqual((long)stats.UltraRareCount, total.UltraRareUnlocked, friend);
                    Assert.AreEqual((long)stats.TrophyPlatinumCount, total.PlatinumUnlocked, friend);
                    Assert.AreEqual((long)stats.TrophyGoldCount, total.GoldUnlocked, friend);
                    Assert.AreEqual((long)stats.TrophySilverCount, total.SilverUnlocked, friend);
                    Assert.AreEqual((long)stats.TrophyBronzeCount, total.BronzeUnlocked, friend);
                }
            });
        }

        [TestMethod]
        public void RecentPage_IsTheFirstRowsOfTheWholeListSortedInCSharp()
        {
            WithFixture(db =>
            {
                var unlocked = AchievementQuery.CurrentUser.WithSummaryRules().WhereUnlocked(true);
                var expected = AchievementReadService.Rows(db, unlocked)
                    .OrderByDescending(row => AchievementSqlFunctions.ParseUtc(row.UnlockTimeUtc) ?? DateTime.MinValue)
                    .Select(row => row.PlayniteGameId + "/" + row.ApiName)
                    .Take(4)
                    .ToList();

                var page = AchievementReadService.Rows(db, unlocked.OrderBy(AchievementSort.UnlockTime, descending: true).Page(4))
                    .Select(row => row.PlayniteGameId + "/" + row.ApiName)
                    .ToList();

                CollectionAssert.AreEqual(expected, page);
                Assert.AreEqual(Alpha + "/a1", page[2], "a1 sorts by its overridden time, after the two Retro unlocks");
                CollectionAssert.AreEqual(
                    expected.Skip(1).Take(2).ToList(),
                    AchievementReadService.Rows(db, unlocked.OrderBy(AchievementSort.UnlockTime, descending: true).Page(2, 1))
                        .Select(row => row.PlayniteGameId + "/" + row.ApiName).ToList(),
                    "an offset page");
            });
        }

        [TestMethod]
        public void Search_MatchesTheCSharpMatcher()
        {
            WithFixture(db =>
            {
                var friends = AchievementReadService.Rows(db, AchievementQuery.AllFriends.WhereUnlocked(true));
                var mine = AchievementReadService.Rows(db, AchievementQuery.CurrentUser.WhereUnlocked(true));
                foreach (var text in new[] { "épée", "ÉPÉE", "EPEE", "  gold  ", "ánna", "Outside", "steam", "zzz" })
                {
                    var query = SearchQuery.From(text);
                    CollectionAssert.AreEquivalent(
                        friends.Where(row => query.Matches(SearchTextBuilder.FromValues(
                            row.UserDisplayName, row.GameName, row.DisplayName, row.Description, row.Category, row.ProviderKey))).Select(Key).ToList(),
                        AchievementReadService.Rows(db, AchievementQuery.AllFriends.WhereUnlocked(true).WhereSearch(text)).Select(Key).ToList(),
                        "friends: " + text);
                    CollectionAssert.AreEquivalent(
                        mine.Where(row => query.Matches(SearchTextBuilder.ForRecentAchievement(row.GameName, row.DisplayName))).Select(Key).ToList(),
                        AchievementReadService.Rows(db, AchievementQuery.CurrentUser.WhereUnlocked(true).WhereSearch(text)).Select(Key).ToList(),
                        "current user: " + text);
                }

                Assert.AreEqual(
                    AchievementReadService.Rows(db, AchievementQuery.AllFriends.WhereUnlocked(true).WhereSearch("épée")).Count,
                    AchievementReadService.Rows(db, AchievementQuery.AllFriends.WhereUnlocked(true).WhereSearch("ÉPÉE")).Count,
                    "case folds accented letters");
                Assert.AreEqual(0, AchievementReadService.Rows(db, AchievementQuery.CurrentUser.WhereUnlocked(true).WhereSearch("EPEE")).Count,
                    "accents are not folded");
            });
        }

        [TestMethod]
        public void Filters_And_Keys_MatchTheCSharpRules()
        {
            WithFixture(db =>
            {
                var all = AchievementReadService.Rows(db, AchievementQuery.CurrentUser);

                CollectionAssert.AreEquivalent(
                    all.Where(row => AchievementSqlFunctions.ParseStoredRarity(row.Rarity) == RarityTier.UltraRare && row.Unlocked == 1)
                        .Select(row => row.PlayniteGameId).Distinct().ToList(),
                    AchievementReadService.Keys(db, AchievementQuery.CurrentUser.WhereUnlocked(true).WhereRarity(new[] { RarityTier.UltraRare }), AchievementGroup.Game).ToList());

                CollectionAssert.AreEquivalent(
                    all.Where(row => AchievementClassificationFilter.Matches(row.CategoryKind, row.Tags, "missable")).Select(Key).ToList(),
                    AchievementReadService.Rows(db, AchievementQuery.CurrentUser.WhereClassification(new[] { "missable" })).Select(Key).ToList());

                CollectionAssert.AreEquivalent(
                    all.Where(row => string.Equals(row.TrophyType?.Trim(), "gold", StringComparison.OrdinalIgnoreCase)).Select(Key).ToList(),
                    AchievementReadService.Rows(db, AchievementQuery.CurrentUser.WhereTrophy(new[] { "Gold" })).Select(Key).ToList());

                var byTag = AchievementReadService.Totals(db, AchievementQuery.CurrentUser.WithSummaryRules(), AchievementGroup.Tag)
                    .ToDictionary(row => row.Key ?? string.Empty, row => row.Count);
                var summaryRows = AchievementReadService.Rows(db, AchievementQuery.CurrentUser.WithSummaryRules());
                var expectedByTag = summaryRows
                    .SelectMany(row => AchievementTagHelper.GetComponents(row.Tags).DefaultIfEmpty(string.Empty))
                    .GroupBy(tag => tag, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => (long)group.Count());
                CollectionAssert.AreEquivalent(expectedByTag.ToList(), byTag.ToList());
            });
        }

        [TestMethod]
        public void OneFriendAndGame_ListsLockedRowsInDisplayOrder()
        {
            WithFixture(db =>
            {
                var rows = AchievementReadService.Rows(db, AchievementQuery.AllFriends.ForFriend("Steam", "a").ForProviderGame("Steam", 100, null));

                CollectionAssert.AreEqual(new[] { "a1", "a2", "a3", "a4", "a5", "a6" }, rows.Select(row => row.ApiName).ToList());
                CollectionAssert.AreEqual(new long[] { 1, 0, 1, 0, 0, 0 }, rows.Select(row => row.Unlocked).ToList());
                Assert.IsTrue(rows.All(row => row.ExternalUserId == "a" && row.OwnershipConfirmed == 1));
            });
        }

        [TestMethod]
        public void SortedKeys_AreOnePerRow_AndFollowThePage()
        {
            WithFixture(db =>
            {
                foreach (var query in new[]
                         {
                             AchievementQuery.CurrentUser,
                             AchievementQuery.CurrentUser.WithSummaryRules().WhereUnlocked(true),
                             AchievementQuery.CurrentUser.WhereSearch("a"),
                             AchievementQuery.AllFriends,
                             AchievementQuery.AllFriends.ForFriend("Steam", "a").ForProviderGame("Steam", 100, null)
                         })
                {
                    var rows = AchievementReadService.Rows(db, query).Count;
                    Assert.IsTrue(rows > 0);
                    Assert.AreEqual(rows, AchievementReadService.SortedKeys(db, query).Count);
                    Assert.AreEqual(1, AchievementReadService.SortedKeys(db, query.Page(1, 1)).Count);
                }
            });
        }

        [TestMethod]
        public void KeyedReads_InlineTheirIds_SoThousandsOfKeysBindNoVariablePerKey()
        {
            WithFixture(db =>
            {
                var query = AchievementQuery.AllFriends.CompareWith(new[] { FriendAccountRef.From("Steam", "b") });
                var keys = AchievementReadService.SortedKeys(db, query).ToList();

                // Keys of rows gone since the keys were read: one per user, game and definition id.
                var gone = Enumerable.Range(1000, 5000).Select(id => new AchievementQuerySql.RowKey { UserId = id, GameId = id, DefinitionId = id });
                var command = AchievementQuerySql.Rows(query, keys.Concat(gone).ToList());

                Assert.AreEqual(4, command.Args.Length, "the compare account's provider and id in each compare table; every id is inline");
                CollectionAssert.AreEqual(
                    AchievementReadService.Rows(db, query, keys).Select(Key).ToList(),
                    AchievementReadService.Rows(db, query, keys.Concat(gone).ToList()).Select(Key).ToList());
                Assert.IsTrue(keys.Count > 0);
            });
        }

        [TestMethod]
        public void KeyedReads_InlineAuthoredKeysAndPins_AsQuotedLiterals()
        {
            WithFixture(db =>
            {
                db.ExecuteNonQuery(
                    "INSERT INTO CustomLayerAchievements (PlayniteGameId, ApiName, DisplayName, IsAuthored, Unlocked, UpdatedUtc) VALUES (?, 'it''s; DROP TABLE Games; --', 'Quote', 1, 0, ?);",
                    Alpha, "2025-01-01T00:00:00.0000000Z");
                var query = AchievementQuery.CurrentUser.ForGames(new[] { Guid.Parse(Alpha) });
                var authored = AchievementReadService.SortedKeys(db, query).Where(key => key.PlayniteGameId != null).ToList();
                var notAGuid = new AchievementQuerySql.RowKey { PlayniteGameId = "x' OR 1=1 --", ApiName = "custom:one" };

                var command = AchievementQuerySql.Rows(query, authored.Concat(new[] { notAGuid }).ToList());
                var rows = AchievementReadService.Rows(db, query, authored.Concat(new[] { notAGuid }).ToList()).Select(row => row.ApiName).ToList();

                Assert.AreEqual(0, command.Args.Count(arg => arg is string text && text.StartsWith("it's", StringComparison.Ordinal)), "authored keys bind no variables");
                CollectionAssert.AreEquivalent(new[] { "custom:one", "custom:two", "it's; DROP TABLE Games; --" }, rows);

                var pinned = AchievementQuery.CurrentUser.ForAchievements(new[]
                {
                    new KeyValuePair<Guid, string>(Guid.Parse(Alpha), "IT'S; DROP TABLE Games; --"),
                    new KeyValuePair<Guid, string>(Guid.Parse(Alpha), "a1")
                });
                CollectionAssert.AreEquivalent(
                    new[] { "a1", "it's; DROP TABLE Games; --" },
                    AchievementReadService.Rows(db, pinned).Select(row => row.ApiName).ToList(),
                    "pins match ignoring case");
                Assert.AreEqual(6L, db.ExecuteScalar<long>("SELECT COUNT(*) FROM Games;"));
            });
        }

        [TestMethod]
        public void ProgressKeys_SplitTheLockedRows_EvenANumeratorWithoutADenominator()
        {
            WithFixture(db =>
            {
                db.ExecuteNonQuery("UPDATE UserAchievements SET ProgressNum = 3, ProgressDenom = NULL WHERE UserGameProgressId = 1 AND AchievementDefinitionId = 3;");
                var locked = AchievementQuery.CurrentUser.WhereUnlocked(false);
                List<string> Names(AchievementQuery query) => AchievementReadService.Rows(db, query).Select(row => row.ApiName).ToList();

                CollectionAssert.Contains(Names(locked.WhereAnyKey(new[] { "NoProgress" })), "a3");
                CollectionAssert.AreEquivalent(
                    Names(locked),
                    Names(locked.WhereAnyKey(new[] { "NoProgress" })).Concat(Names(locked.WhereAnyKey(new[] { "InProgress" }))).ToList());
            });
        }

        private static string Key(AchievementRowRecord row) => row.UserId + "/" + row.PlayniteGameId + "/" + row.ApiName;

        /// <summary>
        /// The current user has Alpha (a1-a6 and two authored), Beta (excluded from summaries),
        /// Retro (a softcore and a hardcore unlock) and Multi on two accounts, plus an authored-only
        /// game. Friend A has Alpha and a game outside the library, B has Alpha, C is inactive.
        /// </summary>
        private static void Seed(SQLiteDatabase db)
        {
            void Exec(string sql, params object[] args) => db.ExecuteNonQuery(sql, args);
            const string Stamp = "2025-01-01T00:00:00.0000000Z";

            Exec(@"INSERT INTO Users (Id, ProviderKey, ExternalUserId, DisplayName, IsCurrentUser, IsActiveFriend, FriendSource, CreatedUtc, UpdatedUtc) VALUES
                (1, 'Steam', 'me', 'Me', 1, 1, NULL, ?1, ?1), (2, 'Exophase', 'me-exo', 'Me', 1, 1, NULL, ?1, ?1),
                (10, 'Steam', 'a', 'Ánna', 0, 1, 'SteamFriends', ?1, ?1), (11, 'Steam', 'b', 'Bob', 0, 1, 'SteamFriends', ?1, ?1),
                (12, 'Steam', 'c', 'Cee', 0, 0, 'SteamFriends', ?1, ?1);", Stamp);
            Exec(@"INSERT INTO Games (Id, ProviderKey, ProviderGameId, ProviderGameKey, PlayniteGameId, GameName, FirstSeenUtc, LastUpdatedUtc) VALUES
                (1, 'Steam', 100, NULL, ?1, 'Alpha Épée', ?6, ?6), (2, 'Steam', 200, NULL, ?2, 'Beta', ?6, ?6),
                (3, 'RetroAchievements', 300, NULL, ?3, 'Retro', ?6, ?6), (4, 'Steam', 400, NULL, NULL, 'Outside', ?6, ?6),
                (5, 'Steam', 500, NULL, ?4, 'Multi', ?6, ?6), (6, 'Exophase', NULL, 'multi-exo', ?4, 'Multi', ?6, ?6);",
                Alpha, Beta, Retro, Multi, AuthoredOnly, Stamp);
            Exec(@"INSERT INTO UserGameProgress (Id, UserId, GameId, CacheKey, HasAchievements, LastUpdatedUtc, CreatedUtc, UpdatedUtc) VALUES
                (1, 1, 1, ?1, 1, '2025-06-01T00:00:00Z', ?5, ?5), (2, 1, 2, ?2, 1, '2025-06-01T00:00:00Z', ?5, ?5),
                (3, 1, 3, ?3, 1, '2025-06-01T00:00:00Z', ?5, ?5), (4, 1, 5, ?4, 1, '2025-06-01T00:00:00Z', ?5, ?5),
                (5, 2, 6, ?4, 1, '2025-07-01T00:00:00Z', ?5, ?5),
                (10, 10, 1, 'f-a-1', 1, ?5, ?5, ?5), (11, 10, 4, 'f-a-4', 1, ?5, ?5, ?5), (12, 11, 1, 'f-b-1', 1, ?5, ?5, ?5), (13, 12, 1, 'f-c-1', 1, ?5, ?5, ?5);",
                Alpha, Beta, Retro, Multi, Stamp);
            Exec(@"INSERT INTO FriendOwnership (UserId, GameId, PlaytimeForeverMinutes, LastOwnershipRefreshUtc, CreatedUtc, UpdatedUtc) VALUES
                (10, 1, 0, ?1, ?1, ?1), (10, 4, 0, NULL, ?1, ?1), (11, 1, 30, NULL, ?1, ?1), (12, 1, 0, ?1, ?1, ?1);", Stamp);

            Exec(@"INSERT INTO PlatformDefinitions (Id, GameId, ApiName, DisplayName, Description, Points, TrophyType, Tags, GlobalPercentUnlocked, Rarity, IsCapstone, MetadataOrder, CreatedUtc, UpdatedUtc) VALUES
                (1, 1, 'a1', 'Épée Master', 'Swing', 10, 'bronze', NULL, 50.0, 'Common', 0, 1, ?1, ?1),
                (2, 1, 'a2', 'Gold Rush', 'Dig', 20, 'gold', NULL, 8.0, 'Rare', 0, 2, ?1, ?1),
                (3, 1, 'a3', 'Finale', NULL, 30, 'platinum', NULL, 1.0, 'UltraRare', 1, 3, ?1, ?1),
                (4, 1, 'a4', 'Hidden Path', NULL, 5, 'silver', NULL, 30.0, 'Uncommon', 0, 4, ?1, ?1),
                (5, 1, 'a5', 'Side Quest', NULL, 5, NULL, 'Missable', 60.0, 'Common', 0, 5, ?1, ?1),
                (6, 1, 'a6', 'Collector', NULL, 5, 'bronze', 'Collectable|Missable', 20.0, 'Uncommon', 0, 6, ?1, ?1),
                (21, 2, 'b1', 'Beta One', NULL, 10, NULL, NULL, 70.0, 'Common', 0, NULL, ?1, ?1),
                (31, 3, 'r1', 'Soft', NULL, 5, NULL, 'Softcore', 9.0, 'Rare', 0, NULL, ?1, ?1),
                (32, 3, 'r2', 'Hard', NULL, 7, NULL, 'Hardcore', 40.0, 'Common', 0, NULL, ?1, ?1),
                (41, 4, 'o1', 'Outside One', NULL, NULL, 'gold', NULL, 7.0, 'Rare', 0, NULL, ?1, ?1),
                (42, 4, 'o2', 'Outside Two', NULL, NULL, NULL, NULL, 60.0, 'Common', 0, NULL, ?1, ?1),
                (51, 5, 'm1', 'Multi One', NULL, NULL, NULL, NULL, 50.0, 'Common', 0, NULL, ?1, ?1),
                (52, 5, 'm2', 'Multi Two', NULL, NULL, NULL, NULL, 9.0, 'Rare', 0, NULL, ?1, ?1),
                (61, 6, 'x1', 'Multi Exo', NULL, NULL, NULL, NULL, 30.0, 'Uncommon', 0, NULL, ?1, ?1);", Stamp);

            // a2's rarity comes from the metadata layer.
            db.ExecuteNonQuery(AchievementLayerSql.InsertSql, 2L, null, null, null, null, null, null, null, null, null, 3.0, "UltraRare", null, 0L);

            Exec(@"INSERT INTO UserAchievements (UserGameProgressId, AchievementDefinitionId, Unlocked, UnlockTimeUtc, LastUpdatedUtc, CreatedUtc) VALUES
                (1, 1, 1, ?2, ?1, ?1), (1, 2, 1, ?3, ?1, ?1), (1, 3, 0, NULL, ?1, ?1),
                (1, 4, 1, '2025-03-03T10:00:00.0000000Z', ?1, ?1), (1, 5, 1, '2025-03-04T10:00:00.0000000Z', ?1, ?1),
                (1, 6, 1, '2025-03-05T00:30:00.0000000Z', ?1, ?1),
                (2, 21, 1, '2025-04-01T10:00:00.0000000Z', ?1, ?1),
                (3, 31, 1, '2025-05-01T12:00:00.0000000Z', ?1, ?1), (3, 32, 1, '2025-05-02T12:00:00.0000000Z', ?1, ?1),
                (4, 51, 1, '2025-01-05T12:00:00.0000000Z', ?1, ?1), (5, 61, 1, '2025-01-06T12:00:00.0000000Z', ?1, ?1),
                (10, 1, 1, '2024-01-01T08:00:00.0000000Z', ?1, ?1), (10, 3, 1, '2024-01-02T08:00:00.0000000Z', ?1, ?1), (10, 2, 0, NULL, ?1, ?1),
                (11, 41, 1, '2024-02-01T08:00:00.0000000Z', ?1, ?1),
                (12, 2, 1, '2024-03-01T08:00:00.0000000Z', ?1, ?1), (12, 6, 1, '2024-03-02T08:00:00.0000000Z', ?1, ?1),
                (13, 1, 1, '2024-04-01T08:00:00.0000000Z', ?1, ?1);", Stamp, A1Raw, A2Raw);

            // The user's edits: a1's points and unlock time, a2's cleared time and capstone, a4
            // filtered, a5 filtered from summaries, and two authored achievements on Alpha and one
            // on a game with nothing else.
            Exec(@"INSERT INTO CustomLayerAchievements (PlayniteGameId, ApiName, Points, UnlockTimeUtc, Cleared, CapstoneAssigned, IsFiltered, IsSummaryFiltered, UpdatedUtc) VALUES
                (?1, 'a1', 99, ?2, 0, 0, 0, 0, ?4), (?1, 'a2', NULL, NULL, ?3, 1, 0, 0, ?4),
                (?1, 'a4', NULL, NULL, 0, 0, 1, 0, ?4), (?1, 'a5', NULL, NULL, 0, 0, 0, 1, ?4);",
                Alpha, A1Override, (long)AchievementField.UnlockTime, Stamp);
            Exec(@"INSERT INTO CustomLayerAchievements (PlayniteGameId, ApiName, DisplayName, IsAuthored, Unlocked, UnlockTimeUtc, Rarity, GlobalPercentUnlocked, Points, TrophyType, UpdatedUtc) VALUES
                (?1, 'custom:one', 'Épilogue', 1, 1, '2025-03-06T10:00:00.0000000Z', 'UltraRare', 2.0, 15, 'platinum', ?3),
                (?1, 'custom:two', 'Later', 1, 0, NULL, 'Common', NULL, 3, NULL, ?3),
                (?2, 'custom:solo', 'Solo', 1, 1, '2025-02-01T10:00:00.0000000Z', 'Rare', 9.0, NULL, NULL, ?3);",
                Alpha, AuthoredOnly, Stamp);
            Exec("INSERT INTO CustomLayerGames (PlayniteGameId, CapstonesMaterialized, ExcludedFromSummaries) VALUES (?1, 1, 0), (?2, 0, 1);", Alpha, Beta);
        }

        internal static void WithFixture(Action<SQLiteDatabase> test)
        {
            var dir = Path.Combine(Path.GetTempPath(), "playach-reads-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, SqlNadoSchemaManager.DatabaseFileName);
            try
            {
                using (var db = new SQLiteDatabase(
                    path,
                    SQLiteOpenOptions.SQLITE_OPEN_READWRITE | SQLiteOpenOptions.SQLITE_OPEN_CREATE | SQLiteOpenOptions.SQLITE_OPEN_FULLMUTEX))
                {
                    new SqlNadoSchemaManager(null, path, dir).EnsureSchema(db);
                    AchievementSqlFunctions.Register(db);
                    Seed(db);
                    test(db);
                }
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch (IOException) { }
            }
        }
    }
}
