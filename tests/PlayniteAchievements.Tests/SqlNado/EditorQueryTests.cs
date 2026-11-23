using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services.Database;
using SqlNado;

namespace PlayniteAchievements.SqlNado.Tests
{
    /// <summary>
    /// The query parts the achievement editor reads its rows through, on the fixture
    /// <see cref="AchievementReadServiceTests"/> seeds.
    /// </summary>
    [TestClass]
    public class EditorQueryTests
    {
        private static readonly Guid Alpha = Guid.Parse(AchievementReadServiceTests.Alpha);
        private static readonly Guid Beta = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
        private static readonly Guid Multi = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000004");

        private static List<string> Names(SQLiteDatabase db, AchievementQuery query) =>
            AchievementReadService.Rows(db, query).Select(row => row.ApiName).ToList();

        [TestMethod]
        public void EditorSearch_AlsoMatchesTheDescriptionAndApiName()
        {
            AchievementReadServiceTests.WithFixture(db =>
            {
                var alpha = AchievementQuery.CurrentUser.ForGames(new[] { Alpha });

                CollectionAssert.AreEqual(new[] { "a1" }, Names(db, alpha.WhereSearch("swing", AchievementSearchFields.Editor)), "a1's description");
                CollectionAssert.AreEqual(new[] { "a3" }, Names(db, alpha.WhereSearch("a3", AchievementSearchFields.Editor)), "a3's API name");
                Assert.AreEqual(0, Names(db, alpha.WhereSearch("swing")).Count, "the default fields leave the description out");
                Assert.AreEqual(
                    Names(db, alpha).Count,
                    Names(db, alpha.WhereSearch("alpha", AchievementSearchFields.Editor)).Count,
                    "the game name still matches");
            });
        }

        [TestMethod]
        public void LatestProgressOnly_KeepsTheMostRecentlyUpdatedAccountOfAGame()
        {
            AchievementReadServiceTests.WithFixture(db =>
            {
                var multi = AchievementQuery.CurrentUser.ForGames(new[] { Multi });

                CollectionAssert.AreEquivalent(new[] { "m1", "m2", "x1" }, Names(db, multi));
                CollectionAssert.AreEqual(new[] { "x1" }, Names(db, multi.WithLatestProgressOnly()), "the Exophase account was updated last");
                CollectionAssert.AreEquivalent(
                    Names(db, AchievementQuery.CurrentUser.ForGames(new[] { Alpha })),
                    Names(db, AchievementQuery.CurrentUser.ForGames(new[] { Alpha }).WithLatestProgressOnly()),
                    "a game on one account keeps every row, authored ones included");
            });
        }

        [TestMethod]
        public void Customized_MatchesOverridesThatDifferFromTheProvider()
        {
            AchievementReadServiceTests.WithFixture(db =>
            {
                var alpha = AchievementQuery.CurrentUser.ForGames(new[] { Alpha });

                // a1 points and time, a2 cleared time and a capstone mark, a3 a platform capstone
                // the materialized marks dropped, a4 filtered, a5 filtered from summaries, and
                // the two authored achievements.
                CollectionAssert.AreEquivalent(
                    new[] { "a1", "a2", "a3", "a4", "a5", "custom:one", "custom:two" },
                    Names(db, alpha.WhereCustomized(true)));
                CollectionAssert.AreEqual(new[] { "a6" }, Names(db, alpha.WhereCustomized(false)));

                db.ExecuteNonQuery(
                    "INSERT INTO CustomLayerAchievements (PlayniteGameId, ApiName, DisplayName, Points, UpdatedUtc) VALUES (?, 'a6', ' Collector ', 5, ?);",
                    Alpha.ToString("D"), "2025-01-01T00:00:00.0000000Z");
                CollectionAssert.AreEqual(new[] { "a6" }, Names(db, alpha.WhereCustomized(false)), "overrides equal to the provider's values");

                db.ExecuteNonQuery("UPDATE CustomLayerAchievements SET DisplayName = 'Hoarder' WHERE ApiName = 'a6';");
                Assert.AreEqual(0, Names(db, alpha.WhereCustomized(false)).Count, "a renamed achievement");
            });
        }

        [TestMethod]
        public void KeptAchievements_PassTheRowFilters_ButNotTheGameScope()
        {
            AchievementReadServiceTests.WithFixture(db =>
            {
                var lockedAlpha = AchievementQuery.CurrentUser.ForGames(new[] { Alpha }).WhereUnlocked(false);
                var kept = lockedAlpha.OrKeep(new[]
                {
                    new KeyValuePair<Guid, string>(Alpha, "A1"),
                    new KeyValuePair<Guid, string>(Beta, "b1")
                });

                CollectionAssert.AreEquivalent(new[] { "a3", "custom:two" }, Names(db, lockedAlpha));
                CollectionAssert.AreEquivalent(new[] { "a1", "a3", "custom:two" }, Names(db, kept), "a1 matched ignoring case; b1 is outside the games");
                Assert.AreEqual(
                    AchievementReadService.SortedKeys(db, kept).Count,
                    Names(db, kept).Count,
                    "the keys read agrees with the rows read");
                Assert.AreNotEqual(AchievementQuerySql.KeyOf(lockedAlpha), AchievementQuerySql.KeyOf(kept));
            });
        }

        [TestMethod]
        public void EditorSearchAndCustomization_AreParts_OfTheQueryKey()
        {
            var query = AchievementQuery.CurrentUser.ForGames(new[] { Alpha });

            Assert.AreNotEqual(
                AchievementQuerySql.KeyOf(query.WhereSearch("x")),
                AchievementQuerySql.KeyOf(query.WhereSearch("x", AchievementSearchFields.Editor)));
            Assert.AreNotEqual(
                AchievementQuerySql.KeyOf(query.WhereCustomized(true)),
                AchievementQuerySql.KeyOf(query.WhereCustomized(false)));
            Assert.AreNotEqual(AchievementQuerySql.KeyOf(query), AchievementQuerySql.KeyOf(query.WithLatestProgressOnly()));
        }

        [TestMethod]
        public void EditorBaselines_AreTheProviderValues_WithoutTheUsersOverrides()
        {
            AchievementReadServiceTests.WithFixture(db =>
            {
                var query = AchievementQuery.CurrentUser.ForGames(new[] { Alpha });
                var keys = AchievementReadService.SortedKeys(db, query);
                var rows = AchievementReadService.Rows(db, query, keys);
                var baselines = AchievementReadService.EditorBaselines(db, keys);

                var a1 = rows.Single(row => row.ApiName == "a1");
                var a1Baseline = baselines[(a1.UserId.Value, a1.DefinitionId.Value)];
                Assert.AreEqual(99, a1.Points, "the row shows the override");
                Assert.AreEqual(10, a1Baseline.Points, "the baseline is the platform's");
                Assert.AreEqual("2025-03-01T23:30:00.0000000Z", a1Baseline.UnlockTimeUtc, "the platform's time, not the override");
                Assert.AreEqual("Épée Master", a1Baseline.DisplayName);

                var a3 = rows.Single(row => row.ApiName == "a3");
                Assert.AreEqual(1L, baselines[(a3.UserId.Value, a3.DefinitionId.Value)].IsCapstone);
                Assert.IsNull(baselines[(a3.UserId.Value, a3.DefinitionId.Value)].UnlockTimeUtc, "locked");

                Assert.AreEqual(rows.Count(row => row.IsAuthored == 0), baselines.Count, "one per platform row; authored rows have none");
            });
        }
    }
}
