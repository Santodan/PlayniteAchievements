using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Achievements.Scoring;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.Database;
using PlayniteAchievements.Services.Friends;
using PlayniteAchievements.Services.Overview;
using PlayniteAchievements.Services.Search;
using PlayniteAchievements.ViewModels.Items;
using SqlNado;

namespace PlayniteAchievements.SqlNado.Tests
{
    /// <summary>
    /// Runs <see cref="AchievementReadService"/> on a seeded database and compares it with today's
    /// readers in the built plugin (<c>SummaryCacheReader</c>, <c>SqlNadoCacheStore</c>, called by
    /// reflection) and with the C# rules the summary and friends paths apply. Where the two
    /// disagree by design of today's path, the test states both values.
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
        public void GameTotals_MatchTheSummaryReader_PlusAuthoredAchievements()
        {
            WithFixture(db =>
            {
                var totals = AchievementReadService.Totals(db, AchievementQuery.CurrentUser.WithSummaryRules(), AchievementGroup.Game)
                    .ToDictionary(row => row.Key);
                var reader = ReaderGameTotals(db);

                Assert.IsTrue(reader.ContainsKey(Beta), "the reader lists the excluded game; hydration drops it");
                Assert.IsFalse(totals.ContainsKey(Beta), "summary rules leave out the excluded game");

                AssertTotals(reader[Retro], totals[Retro], Retro);

                // Authored achievements: the reader never sees them and CustomAchievementSummaryMerger
                // adds them; custom:one is unlocked (UltraRare, 2%, platinum, 15 points), custom:two
                // locked (Common, no percent, 3 points).
                var alpha = reader[Alpha];
                alpha["Count"] += 2;
                alpha["UnlockedCount"] += 1;
                alpha["UltraRareCount"] += 1;
                alpha["UltraRareUnlocked"] += 1;
                alpha["CommonCount"] += 1;
                alpha["PlatinumCount"] += 1;
                alpha["PlatinumUnlocked"] += 1;
                var oneCollection = AchievementScoreCalculator.GetCollectionValue(RarityTier.UltraRare);
                var onePrestige = AchievementScoreCalculator.GetPrestigeValue(2.0, RarityTier.UltraRare);
                alpha["CollectionScore"] += oneCollection;
                alpha["PrestigeScore"] += onePrestige;
                alpha["CollectionScoreTotal"] += oneCollection + AchievementScoreCalculator.GetCollectionValue(RarityTier.Common);
                alpha["PrestigeScoreTotal"] += onePrestige + AchievementScoreCalculator.GetPrestigeValue(null, RarityTier.Common);
                alpha["Points"] += 15;
                alpha["PlatformScorePoints"] += 15;
                alpha["PointsTotal"] += 18;
                AssertTotals(alpha, totals[Alpha], Alpha);

                Assert.IsFalse(reader.ContainsKey(AuthoredOnly), "a game with only authored achievements has no cache row");
                Assert.AreEqual(1L, totals[AuthoredOnly].Count, "the merger adds it as a game of its own");
            });
        }

        [TestMethod]
        public void GameTotals_LastUnlock_FollowsTheUnlockTimeOverride_WhereTheReaderKeepsTheStoredTime()
        {
            WithFixture(db =>
            {
                var totals = AchievementReadService.Totals(db, AchievementQuery.CurrentUser.WithSummaryRules(), AchievementGroup.Game)
                    .ToDictionary(row => row.Key);
                var reader = ReaderLastUnlock(db);

                // Disagreement: the reader's last unlock is the stored time (a6), and hydration only
                // raises it for authored unlocks (custom:one, 2025-03-06); the override on a1 moves
                // that unlock to 2025-03-10 in the resolved rows.
                Assert.AreEqual("2025-03-05T00:30:00.0000000Z", reader[Alpha]);
                Assert.AreEqual(A1Override, totals[Alpha].LastUnlockUtc);
            });
        }

        [TestMethod]
        public void GameTotals_CountEveryCurrentUserAccount_WhereTheReaderTakesTheLatest()
        {
            WithFixture(db =>
            {
                var totals = AchievementReadService.Totals(db, AchievementQuery.CurrentUser.WithSummaryRules(), AchievementGroup.Game)
                    .ToDictionary(row => row.Key);
                var reader = ReaderGameTotals(db);

                // Disagreement: the reader keeps the most recently updated progress row per cache
                // key (the Exophase account, x1); the resolved rows hold both accounts (m1, m2, x1),
                // as ResolvedAchievements always has.
                Assert.AreEqual(1L, reader[Multi]["Count"]);
                Assert.AreEqual(3L, totals[Multi].Count);
                Assert.AreEqual(2L, totals[Multi].UnlockedCount);
            });
        }

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
        public void FriendRows_MatchTheFriendLoader_ExceptTheUsersOwnUnlockTimeEdits()
        {
            WithFixture(db =>
            {
                var loader = LoaderFriendRows(db);
                var rows = AchievementReadService.Rows(db, AchievementQuery.AllFriends.WhereUnlocked(true));

                string Identity(string friend, string provider, long? appId, string gameKey, string gameId, string apiName, string time) =>
                    string.Join("|", friend, provider, appId, gameKey, gameId, apiName, time);

                var expected = loader.ToDictionary(row => Identity(
                    (string)Get(row, "FriendExternalUserId"), (string)Get(row, "ProviderKey"), (long?)Get(row, "ProviderGameId"),
                    (string)Get(row, "ProviderGameKey"), (string)Get(row, "PlayniteGameId"), (string)Get(row, "ApiName"), (string)Get(row, "UnlockTimeUtc")));
                var actual = rows.ToDictionary(row => Identity(
                    row.ExternalUserId, row.ProviderKey, row.ProviderGameId, row.ProviderGameKey, row.PlayniteGameId, row.ApiName, row.UnlockTimeUtc));

                CollectionAssert.AreEquivalent(expected.Keys.ToList(), actual.Keys.ToList(), "the same friend unlocks");
                Assert.AreEqual(5, actual.Count, "A: a1, a3, o1; B: a2, a6; not the inactive friend C");

                foreach (var pair in expected)
                {
                    var old = pair.Value;
                    var row = actual[pair.Key];
                    Assert.AreEqual(Convert.ToInt64(Get(old, "MyUnlocked")), row.SelfUnlocked, pair.Key);
                    Assert.AreEqual(Convert.ToInt64(Get(old, "MyHasGameData")), row.SelfHasGameData, pair.Key);
                    Assert.AreEqual(Convert.ToInt64(Get(old, "FriendOwnsGame")), row.OwnershipConfirmed, pair.Key);

                    // Disagreement: the loader reads the user's stored unlock time; the resolved rows
                    // carry the user's override (a1) and clear (a2).
                    var oldSelfTime = (string)Get(old, "MyUnlockTimeUtc");
                    switch (row.ApiName)
                    {
                        case "a1":
                            Assert.AreEqual(A1Raw, oldSelfTime);
                            Assert.AreEqual(A1Override, row.SelfUnlockTimeUtc);
                            break;
                        case "a2":
                            Assert.AreEqual(A2Raw, oldSelfTime);
                            Assert.IsNull(row.SelfUnlockTimeUtc);
                            break;
                        default:
                            Assert.AreEqual(oldSelfTime, row.SelfUnlockTimeUtc, pair.Key);
                            break;
                    }
                }
            });
        }

        [TestMethod]
        public void FriendTotals_MatchTheFriendSummaryScores()
        {
            WithFixture(db =>
            {
                var items = LoaderFriendRows(db).Select(row =>
                {
                    var rarity = AchievementSqlFunctions.ParseStoredRarity((string)Get(row, "Rarity"));
                    var percent = (double?)Get(row, "GlobalPercentUnlocked");
                    return new FriendAchievementDisplayItem
                    {
                        ProviderKey = (string)Get(row, "ProviderKey"),
                        FriendExternalUserId = (string)Get(row, "FriendExternalUserId"),
                        Rarity = rarity,
                        GlobalPercentUnlocked = percent,
                        TrophyType = (string)Get(row, "TrophyType"),
                        Unlocked = true,
                        CollectionScore = AchievementScoreCalculator.GetCollectionValue(rarity),
                        PrestigeScore = AchievementScoreCalculator.GetPrestigeValue(percent, rarity)
                    };
                }).ToList();
                var friends = new[] { new FriendSummaryItem { ProviderKey = "Steam", ExternalUserId = "a" }, new FriendSummaryItem { ProviderKey = "Steam", ExternalUserId = "b" } };
                FriendsOverviewDerivations.ApplyFriendSummaryScores(friends, items);

                var totals = AchievementReadService.Totals(db, AchievementQuery.AllFriends.WhereUnlocked(true), AchievementGroup.Friend)
                    .ToDictionary(row => row.Key);
                Assert.AreEqual(2, totals.Count);
                foreach (var friend in friends)
                {
                    var total = totals[FriendsOverviewDerivations.BuildFriendScoreKey(friend.ProviderKey, friend.ExternalUserId)];
                    Assert.AreEqual((long)friend.CollectionScore, total.CollectionScore, friend.ExternalUserId);
                    Assert.AreEqual((long)friend.PrestigeScore, total.PrestigeScore, friend.ExternalUserId);
                    Assert.AreEqual((long)friend.CommonCount, total.CommonUnlocked, friend.ExternalUserId);
                    Assert.AreEqual((long)friend.UncommonCount, total.UncommonUnlocked, friend.ExternalUserId);
                    Assert.AreEqual((long)friend.RareCount, total.RareUnlocked, friend.ExternalUserId);
                    Assert.AreEqual((long)friend.UltraRareCount, total.UltraRareUnlocked, friend.ExternalUserId);
                    Assert.AreEqual((long)friend.TrophyPlatinumCount, total.PlatinumUnlocked, friend.ExternalUserId);
                    Assert.AreEqual((long)friend.TrophyGoldCount, total.GoldUnlocked, friend.ExternalUserId);
                    Assert.AreEqual((long)friend.TrophySilverCount, total.SilverUnlocked, friend.ExternalUserId);
                    Assert.AreEqual((long)friend.TrophyBronzeCount, total.BronzeUnlocked, friend.ExternalUserId);
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
        public void Count_IsTheNumberOfRows_IgnoringThePage()
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
                    Assert.AreEqual(rows, AchievementReadService.Count(db, query));
                    Assert.AreEqual(rows, AchievementReadService.Count(db, query.Page(1, 1)));
                }
            });
        }

        private static string Key(AchievementRowRecord row) => row.UserId + "/" + row.PlayniteGameId + "/" + row.ApiName;

        private static void AssertTotals(Dictionary<string, long> expected, AchievementTotalsRecord actual, string game)
        {
            foreach (var pair in expected)
            {
                Assert.AreEqual(pair.Value, Convert.ToInt64(typeof(AchievementTotalsRecord).GetProperty(pair.Key).GetValue(actual)), game + " " + pair.Key);
            }
        }

        // The summary reader's game rows and score totals, keyed by cache key, in the measure names
        // of AchievementTotalsRecord.
        private static Dictionary<string, Dictionary<string, long>> ReaderGameTotals(SQLiteDatabase db)
        {
            var reader = PluginType("PlayniteAchievements.Services.Database.SummaryCacheReader");
            var rows = (IEnumerable)Invoke(reader, "LoadCachedGameSummaryRows", db, null);
            var unlocked = (IDictionary)Invoke(reader, "LoadCachedScoreTotals", db, true, null);
            var possible = (IDictionary)Invoke(reader, "LoadCachedScoreTotals", db, false, null);
            var result = new Dictionary<string, Dictionary<string, long>>();
            foreach (var row in rows)
            {
                var key = (string)Get(row, "CacheKey");
                long Score(IDictionary totals, string item) => totals.Contains(key) ? Convert.ToInt64(Get(totals[key], item)) : 0;
                long Field(string name) => Convert.ToInt64(Get(row, name));
                result[key] = new Dictionary<string, long>
                {
                    ["Count"] = Field("TotalAchievements"),
                    ["UnlockedCount"] = Field("AchievementsUnlocked"),
                    ["CommonUnlocked"] = Field("CommonCount"),
                    ["UncommonUnlocked"] = Field("UncommonCount"),
                    ["RareUnlocked"] = Field("RareCount"),
                    ["UltraRareUnlocked"] = Field("UltraRareCount"),
                    ["CommonCount"] = Field("TotalCommonPossible"),
                    ["UncommonCount"] = Field("TotalUncommonPossible"),
                    ["RareCount"] = Field("TotalRarePossible"),
                    ["UltraRareCount"] = Field("TotalUltraRarePossible"),
                    ["PlatinumUnlocked"] = Field("TrophyPlatinumCount"),
                    ["GoldUnlocked"] = Field("TrophyGoldCount"),
                    ["SilverUnlocked"] = Field("TrophySilverCount"),
                    ["BronzeUnlocked"] = Field("TrophyBronzeCount"),
                    ["PlatinumCount"] = Field("TrophyPlatinumTotal"),
                    ["GoldCount"] = Field("TrophyGoldTotal"),
                    ["SilverCount"] = Field("TrophySilverTotal"),
                    ["BronzeCount"] = Field("TrophyBronzeTotal"),
                    ["CapstoneCount"] = Field("CapstoneTotal"),
                    ["CapstoneUnlocked"] = Field("CapstoneUnlocked"),
                    ["CollectionScore"] = Score(unlocked, "Item1"),
                    ["PrestigeScore"] = Score(unlocked, "Item2"),
                    ["Points"] = Score(unlocked, "Item3"),
                    ["PlatformScorePoints"] = Score(unlocked, "Item4"),
                    ["CollectionScoreTotal"] = Score(possible, "Item1"),
                    ["PrestigeScoreTotal"] = Score(possible, "Item2"),
                    ["PointsTotal"] = Score(possible, "Item3")
                };
            }

            return result;
        }

        private static Dictionary<string, string> ReaderLastUnlock(SQLiteDatabase db) =>
            ((IEnumerable)Invoke(PluginType("PlayniteAchievements.Services.Database.SummaryCacheReader"), "LoadCachedGameSummaryRows", db, null))
                .Cast<object>()
                .ToDictionary(row => (string)Get(row, "CacheKey"), row => (string)Get(row, "LastUnlockUtc"));

        private static List<object> LoaderFriendRows(SQLiteDatabase db) =>
            ((IEnumerable)Invoke(
                PluginType("PlayniteAchievements.Services.Database.SqlNadoCacheStore"),
                "LoadFriendAchievementRows",
                db, 0, false, true, null, null)).Cast<object>().ToList();

        private const BindingFlags AnyMember = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        // Today's readers live in the built plugin, which the assembly initializer loads.
        private static Type PluginType(string name)
        {
            var plugin = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(assembly => assembly.GetName().Name == "PlayniteAchievements");
            Assert.IsNotNull(plugin, "the built plugin assembly is loaded by LocalizationAssemblyInitializer");
            return plugin.GetType(name, throwOnError: true);
        }

        private static object Invoke(Type type, string method, params object[] args) =>
            type.GetMethods(AnyMember).Single(candidate => candidate.Name == method && candidate.GetParameters().Length == args.Length).Invoke(null, args);

        private static object Get(object target, string name)
        {
            var property = target.GetType().GetProperty(name, AnyMember);
            return property != null ? property.GetValue(target) : target.GetType().GetField(name, AnyMember).GetValue(target);
        }

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
            Exec(@"INSERT INTO AchievementOverrides (PlayniteGameId, ApiName, Points, UnlockTimeUtc, Cleared, CapstoneAssigned, IsFiltered, IsSummaryFiltered, UpdatedUtc) VALUES
                (?1, 'a1', 99, ?2, 0, 0, 0, 0, ?4), (?1, 'a2', NULL, NULL, ?3, 1, 0, 0, ?4),
                (?1, 'a4', NULL, NULL, 0, 0, 1, 0, ?4), (?1, 'a5', NULL, NULL, 0, 0, 0, 1, ?4);",
                Alpha, A1Override, (long)AchievementField.UnlockTime, Stamp);
            Exec(@"INSERT INTO AchievementOverrides (PlayniteGameId, ApiName, DisplayName, IsAuthored, Unlocked, UnlockTimeUtc, Rarity, GlobalPercentUnlocked, Points, TrophyType, UpdatedUtc) VALUES
                (?1, 'custom:one', 'Épilogue', 1, 1, '2025-03-06T10:00:00.0000000Z', 'UltraRare', 2.0, 15, 'platinum', ?3),
                (?1, 'custom:two', 'Later', 1, 0, NULL, 'Common', NULL, 3, NULL, ?3),
                (?2, 'custom:solo', 'Solo', 1, 1, '2025-02-01T10:00:00.0000000Z', 'Rare', 9.0, NULL, NULL, ?3);",
                Alpha, AuthoredOnly, Stamp);
            Exec("INSERT INTO GameCustomState (PlayniteGameId, CapstonesMaterialized, ExcludedFromSummaries) VALUES (?1, 1, 0), (?2, 0, 1);", Alpha, Beta);
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
