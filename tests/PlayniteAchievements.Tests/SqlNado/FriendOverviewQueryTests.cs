using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.Database;
using PlayniteAchievements.Services.Friends;

namespace PlayniteAchievements.SqlNado.Tests
{
    /// <summary>
    /// The query features the Friends Overview window reads with, on the read service fixture:
    /// friend A (Steam "a") has a1 and a3 of Alpha unlocked and a2 locked, and o1 of a game
    /// outside the library; friend B (Steam "b") has a2 and a6 of Alpha.
    /// </summary>
    [TestClass]
    public class FriendOverviewQueryTests
    {
        private static readonly FriendAccountRef A = FriendAccountRef.From("Steam", "a");
        private static readonly FriendAccountRef B = FriendAccountRef.From("Steam", "b");

        private static string Key(AchievementRowRecord row) => row.ExternalUserId + "/" + row.ApiName;

        [TestMethod]
        public void FriendAccounts_ReadTheNamedAccountsOnly()
        {
            AchievementReadServiceTests.WithFixture(db =>
            {
                var query = AchievementQuery.AllFriends.WhereUnlocked(true).ForFriendAccounts(new[] { B });
                CollectionAssert.AreEquivalent(new[] { "b/a2", "b/a6" }, AchievementReadService.Rows(db, query).Select(Key).ToList());
                Assert.AreEqual(2, AchievementReadService.Count(db, query));

                var both = AchievementQuery.AllFriends.WhereUnlocked(true).ForFriendAccounts(new[] { A, B, FriendAccountRef.From("Steam", "a") });
                Assert.AreEqual(5, AchievementReadService.Count(db, both), "duplicates are dropped");

                var none = AchievementQuery.AllFriends.ForFriendAccounts(new FriendAccountRef[0]);
                Assert.AreEqual(0, AchievementReadService.Count(db, none));
                Assert.AreEqual(0, AchievementReadService.Rows(db, none).Count);
            });
        }

        [TestMethod]
        public void CompareWith_AddsTheCompareAccountsStateOfTheSameProviderGame()
        {
            AchievementReadServiceTests.WithFixture(db =>
            {
                var rows = AchievementReadService.Rows(db, AchievementQuery.AllFriends.ForFriendAccounts(new[] { A }).CompareWith(new[] { B }))
                    .ToDictionary(row => row.ApiName);

                Assert.AreEqual(1L, rows["a2"].CompareUnlocked, "B unlocked a2, which A has locked");
                Assert.AreEqual("2024-03-01T08:00:00.0000000Z", rows["a2"].CompareUnlockTimeUtc);
                Assert.AreEqual(0L, rows["a1"].CompareUnlocked);
                Assert.AreEqual(1L, rows["a1"].CompareHasGame, "B has Alpha");
                Assert.AreEqual(0L, rows["o1"].CompareHasGame, "B has nothing of the game outside the library");

                var plain = AchievementReadService.Rows(db, AchievementQuery.AllFriends.ForFriendAccounts(new[] { A }));
                Assert.IsTrue(plain.All(row => row.CompareUnlocked == 0 && row.CompareHasGame == 0 && row.CompareUnlockTimeUtc == null));
                Assert.AreEqual(plain.Count, rows.Count, "the compare columns add no rows");
            });
        }

        [TestMethod]
        public void Filters_HiddenLocked_Capstone_Category_UnlockTime_Nothing()
        {
            AchievementReadServiceTests.WithFixture(db =>
            {
                db.ExecuteNonQuery("UPDATE PlatformDefinitions SET Hidden = 1 WHERE Id = 2;");
                var friends = AchievementQuery.AllFriends;

                var shown = AchievementReadService.Rows(db, friends.WithoutHiddenLocked()).Select(Key).ToList();
                CollectionAssert.DoesNotContain(shown, "a/a2", "hidden and locked for A");
                CollectionAssert.Contains(shown, "b/a2", "hidden but unlocked for B");

                CollectionAssert.AreEquivalent(
                    AchievementReadService.Rows(db, friends).Where(row => row.IsCapstone == 1).Select(Key).ToList(),
                    AchievementReadService.Rows(db, friends.WhereCapstone()).Select(Key).ToList());

                Assert.AreEqual(
                    AchievementReadService.Count(db, friends),
                    AchievementReadService.Count(db, friends.WhereCategory(new[] { AchievementCategoryHelper.DefaultCategoryLabel })),
                    "rows without a category are in the default one");
                Assert.AreEqual(0, AchievementReadService.Count(db, friends.WhereCategory(new[] { "Nope" })));

                Assert.IsTrue(AchievementReadService.Rows(db, friends.WhereUnlockTimeKnown()).All(row => row.Unlocked == 1 && row.UnlockTimeUtc != null));
                Assert.AreEqual(0, AchievementReadService.Count(db, friends.MatchNothing()));
            });
        }

        [TestMethod]
        public void FriendGameAndProviderGameKeys_ReadBackToAccountsAndGames()
        {
            AchievementReadServiceTests.WithFixture(db =>
            {
                var totals = AchievementReadService.Totals(db, AchievementQuery.AllFriends.WhereUnlocked(true), AchievementGroup.FriendGame);
                var byKey = totals.ToDictionary(
                    total =>
                    {
                        var key = AchievementGroupKeys.FriendGame(total.Key);
                        return key.Key + "@" + FriendOverviewProjection.BuildGameUnlockKey(key.Value.ProviderKey, key.Value.ProviderGameKey, key.Value.AppId, key.Value.PlayniteGameId);
                    },
                    total => total.UnlockedCount);

                Assert.AreEqual(2L, byKey["Steam\u001fa@steam|app:100"]);
                Assert.AreEqual(1L, byKey["Steam\u001fa@steam|app:400"]);
                Assert.AreEqual(2L, byKey["Steam\u001fb@steam|app:100"]);

                var game = AchievementGroupKeys.ProviderGame(
                    AchievementReadService.Keys(db, AchievementQuery.AllFriends.ForFriendAccounts(new[] { B }), AchievementGroup.ProviderGame).Single());
                Assert.AreEqual("Steam", game.ProviderKey);
                Assert.AreEqual(100, game.AppId);
                Assert.AreEqual(Guid.Parse(AchievementReadServiceTests.Alpha), game.PlayniteGameId);
            });
        }

        [TestMethod]
        public void DistinctAchievements_CountEachAchievementOfAGameOnce()
        {
            AchievementReadServiceTests.WithFixture(db =>
            {
                // B unlocks a1 too, later than A.
                db.ExecuteNonQuery("INSERT INTO UserAchievements (UserGameProgressId, AchievementDefinitionId, Unlocked, UnlockTimeUtc, LastUpdatedUtc, CreatedUtc) VALUES (12, 1, 1, '2024-05-01T08:00:00.0000000Z', ?1, ?1);",
                    "2025-01-01T00:00:00.0000000Z");
                var both = AchievementQuery.AllFriends.WhereUnlocked(true).ForFriendAccounts(new[] { A, B }).ForGames(new[] { Guid.Parse(AchievementReadServiceTests.Alpha) });

                var rows = AchievementReadService.Totals(db, both, AchievementGroup.ProviderGame).Single();
                var unique = AchievementReadService.Totals(db, both.CountDistinctAchievements(), AchievementGroup.ProviderGame).Single();

                Assert.AreEqual(5L, rows.UnlockedCount);
                Assert.AreEqual(4L, unique.UnlockedCount, "a1, a2, a3 and a6");
                Assert.AreEqual("2024-05-01T08:00:00.0000000Z", unique.LastUnlockUtc);
                Assert.IsTrue(unique.CollectionScore < rows.CollectionScore);
            });
        }

        [TestMethod]
        public void CategoryTotals_CarryHiddenLockedRowsAndTheCategoryOrder()
        {
            AchievementReadServiceTests.WithFixture(db =>
            {
                db.ExecuteNonQuery("UPDATE PlatformDefinitions SET Hidden = 1 WHERE Id = 2;");
                var pair = AchievementQuery.AllFriends.ForFriendAccounts(new[] { A }).ForProviderGame("Steam", 100, null);

                var total = AchievementReadService.Totals(db, pair, AchievementGroup.Category).Single();
                Assert.AreEqual(AchievementCategoryHelper.DefaultCategoryLabel, AchievementCategoryHelper.NormalizeCategoryOrDefault(total.Key), "no category stored");
                Assert.AreEqual(1L, total.HiddenLockedCount);
                Assert.IsNull(total.MinCategoryOrder);
                Assert.IsTrue(total.Count > total.UnlockedCount);
            });
        }

        [TestMethod]
        public void EverySort_Runs_AndRaritySortsAsTheGridDoes()
        {
            AchievementReadServiceTests.WithFixture(db =>
            {
                var count = AchievementReadService.Count(db, AchievementQuery.AllFriends);
                foreach (AchievementSort sort in Enum.GetValues(typeof(AchievementSort)))
                {
                    foreach (var descending in new[] { false, true })
                    {
                        var rows = AchievementReadService.Rows(db, AchievementQuery.AllFriends.OrderBy(sort, descending));
                        Assert.AreEqual(count, rows.Count, sort + (descending ? " desc" : " asc"));
                        CollectionAssert.AreEqual(
                            rows.Select(Key).ToList(),
                            Enumerable.Range(0, (count + 1) / 2).SelectMany(page =>
                                AchievementReadService.Rows(db, AchievementQuery.AllFriends.OrderBy(sort, descending).Page(2, page * 2))).Select(Key).ToList(),
                            "pages of two follow the whole order: " + sort);
                    }
                }

                var byRarity = AchievementReadService.Rows(db, AchievementQuery.AllFriends.OrderBy(AchievementSort.Rarity))
                    .Select(row => AchievementRarityResolver.GetSortValue(row.GlobalPercentUnlocked, AchievementSqlFunctions.ParseStoredRarity(row.Rarity)))
                    .ToList();
                CollectionAssert.AreEqual(byRarity.OrderBy(value => value).ToList(), byRarity);

                var byTime = AchievementReadService.Rows(db, AchievementQuery.AllFriends.OrderBy(AchievementSort.UnlockTime)).ToList();
                var firstLocked = byTime.FindIndex(row => row.Unlocked == 0);
                Assert.IsTrue(firstLocked > 0 && byTime.Skip(firstLocked).All(row => row.Unlocked == 0), "locked rows last, ascending too");
            });
        }
    }
}
