using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.Database;
using PlayniteAchievements.Services.ThemeIntegration;

namespace PlayniteAchievements.SqlNado.Tests
{
    /// <summary>
    /// The query terms the theme lists, pins and Unlock Next read through, on the seeded database
    /// of <see cref="AchievementReadServiceTests"/>, against the rules the lists applied in C#.
    /// </summary>
    [TestClass]
    public class ThemeListQueryTests
    {
        private const string Alpha = AchievementReadServiceTests.Alpha;
        private const string Beta = "aaaaaaaa-0000-0000-0000-000000000002";

        private static string Key(AchievementRowRecord row) => row.PlayniteGameId + "/" + row.ApiName;

        private static RarityTier Tier(AchievementRowRecord row) => AchievementSqlFunctions.ParseStoredRarity(row.Rarity);

        [TestMethod]
        public void FilterKeys_OrWithinAGroup_AndAcrossGroups_AsTheThemeListsMatch()
        {
            AchievementReadServiceTests.WithFixture(db =>
            {
                var all = AchievementQuery.CurrentUser;
                var rows = AchievementReadService.Rows(db, all);

                void Check(string filter, Func<AchievementRowRecord, bool> keep) =>
                    CollectionAssert.AreEquivalent(
                        rows.Where(keep).Select(Key).ToList(),
                        AchievementReadService.Rows(db, DynamicThemeFilterEvaluator.ApplyAchievementFilters(all, filter)).Select(Key).ToList(),
                        filter);

                Check("Locked", row => row.Unlocked == 0);
                Check("Common+Rare", row => Tier(row) == RarityTier.Common || Tier(row) == RarityTier.Rare);
                Check("Unlocked+Bronze", row => row.Unlocked == 1 && string.Equals(row.TrophyType, "bronze", StringComparison.OrdinalIgnoreCase));
                Check("Missable", row => AchievementClassificationFilter.Matches(row.CategoryKind, row.Tags, "Missable"));
                Check("Capstone", row => row.IsCapstone == 1);
                Check("NoProgress", row => !(row.ProgressNum.HasValue && row.ProgressDenom > 0));
                Check("All", row => true);
            });
        }

        [TestMethod]
        public void Provider_MatchesThePlatformKeyIgnoringCase_AuthoredRowsByTheirGame()
        {
            AchievementReadServiceTests.WithFixture(db =>
            {
                var retro = AchievementReadService.Rows(db, AchievementQuery.CurrentUser.WhereProvider("retroachievements"));
                CollectionAssert.AreEquivalent(new[] { "r1", "r2" }, retro.Select(row => row.ApiName).ToList());

                var steam = AchievementReadService.Rows(db, AchievementQuery.CurrentUser.WhereProvider("Steam")).Select(row => row.ApiName).ToList();
                CollectionAssert.Contains(steam, "custom:one", "an authored row takes its game's provider");
                CollectionAssert.DoesNotContain(steam, "r1");
            });
        }

        [TestMethod]
        public void Achievements_ReadOnlyTheNamedPairs_WhateverTheSummaryRulesLeaveOut()
        {
            AchievementReadServiceTests.WithFixture(db =>
            {
                var pins = new[]
                {
                    new KeyValuePair<Guid, string>(Guid.Parse(Alpha), "a4"),
                    new KeyValuePair<Guid, string>(Guid.Parse(Alpha), "custom:one"),
                    new KeyValuePair<Guid, string>(Guid.Parse(Beta), "b1"),
                    new KeyValuePair<Guid, string>(Guid.Parse(Beta), "missing")
                };
                var query = AchievementQuery.CurrentUser.ForAchievements(pins);

                CollectionAssert.AreEquivalent(
                    new[] { Alpha + "/a4", Alpha + "/custom:one", Beta + "/b1" },
                    AchievementReadService.Rows(db, query).Select(Key).ToList(),
                    "a filtered achievement and an excluded game's achievement still read");
                CollectionAssert.AreEquivalent(
                    new[] { Alpha + "/custom:one" },
                    AchievementReadService.Rows(db, query.WithSummaryRules()).Select(Key).ToList());
                Assert.AreEqual(0, AchievementReadService.Rows(db, AchievementQuery.CurrentUser.ForAchievements(Array.Empty<KeyValuePair<Guid, string>>())).Count);

                var keys = AchievementReadService.SortedKeys(db, query);
                CollectionAssert.AreEqual(
                    AchievementReadService.Rows(db, query).Select(AchievementReadService.KeyOf).Select(key => key.DefinitionId + key.ApiName).ToList(),
                    keys.Select(key => key.DefinitionId + key.ApiName).ToList(),
                    "a row's key reads back as SortedKeys reads it");
            });
        }

        [TestMethod]
        public void GoalsFirst_LeadsWithTheLockedGoalsInGoalOrder()
        {
            AchievementReadServiceTests.WithFixture(db =>
            {
                db.ExecuteNonQuery(
                    "INSERT INTO CustomLayerAchievements (PlayniteGameId, ApiName, GoalOrder, UpdatedUtc) VALUES (?1, 'a3', 1, ?2), (?1, 'custom:two', 0, ?2);",
                    Alpha, "2025-01-01T00:00:00.0000000Z");

                var rows = AchievementReadService.Rows(db, AchievementQuery.CurrentUser.WithGoalsFirst().OrderBy(AchievementSort.UnlockTime, descending: true));
                CollectionAssert.AreEqual(new[] { "custom:two", "a3" }, rows.Take(2).Select(row => row.ApiName).ToList());
                var rest = rows.Skip(2).Where(row => row.Unlocked == 1 && row.UnlockTimeUtc != null).Select(row => row.UnlockTimeUtc).ToList();
                CollectionAssert.AreEqual(rest.OrderByDescending(time => time, StringComparer.Ordinal).ToList(), rest, "the sort follows");
            });
        }

        [TestMethod]
        public void ThemeSortKeys_MapToQuerySorts()
        {
            AchievementReadServiceTests.WithFixture(db =>
            {
                var status = AchievementReadService.Rows(db, DynamicThemeFilterEvaluator.ApplyAchievementSort(AchievementQuery.CurrentUser, "Status", "Descending"));
                CollectionAssert.AreEqual(status.OrderByDescending(row => row.Unlocked).Select(row => row.Unlocked).ToList(), status.Select(row => row.Unlocked).ToList());

                var provider = AchievementReadService.Rows(db, DynamicThemeFilterEvaluator.ApplyAchievementSort(AchievementQuery.CurrentUser, "Provider", "Ascending"));
                // The authored-only game's row reads as Custom but sorts with no provider key.
                Assert.AreEqual("Exophase", provider.First(row => row.ProviderKey != null && row.IsAuthored == 0).ProviderKey);

                var byDefault = DynamicThemeFilterEvaluator.ApplyAchievementSort(AchievementQuery.CurrentUser, "Default", null);
                Assert.AreEqual(AchievementSort.UnlockTime, byDefault.Sort);
                Assert.IsTrue(byDefault.Descending);
            });
        }

        [TestMethod]
        public void HiddenAndCapstoneSorts_PutTheFlaggedRowsFirstWhenDescending_ThenByName()
        {
            AchievementReadServiceTests.WithFixture(db =>
            {
                db.ExecuteNonQuery("UPDATE PlatformDefinitions SET Hidden = 1 WHERE ApiName IN ('a5', 'b1');");
                foreach (var sort in new[] { ("Hidden", (Func<AchievementRowRecord, long>)(row => row.Hidden)), ("Capstone", row => row.IsCapstone) })
                {
                    foreach (var direction in new[] { "Descending", "Ascending" })
                    {
                        var rows = AchievementReadService.Rows(db, DynamicThemeFilterEvaluator.ApplyAchievementSort(AchievementQuery.CurrentUser, sort.Item1, direction));
                        var flags = rows.Select(sort.Item2).ToList();
                        CollectionAssert.AreEqual(
                            (direction == "Descending" ? flags.OrderByDescending(flag => flag) : flags.OrderBy(flag => flag)).ToList(),
                            flags,
                            sort.Item1 + " " + direction);
                        Assert.IsTrue(flags.Distinct().Count() == 2, sort.Item1 + " has rows of both kinds");
                        foreach (var group in rows.GroupBy(sort.Item2))
                        {
                            var names = group.Select(row => row.DisplayName.ToUpperInvariant()).ToList();
                            CollectionAssert.AreEqual(names.OrderBy(name => name, StringComparer.Ordinal).ToList(), names, sort.Item1 + " names ascend");
                        }
                    }
                }
            });
        }

        [TestMethod]
        public void EqualSortKeys_OrderByGameName_TheSameOnEveryReadAndInTheKeys()
        {
            AchievementReadServiceTests.WithFixture(db =>
            {
                var query = AchievementQuery.CurrentUser.OrderBy(AchievementSort.Points);
                var rows = AchievementReadService.Rows(db, query);
                foreach (var group in rows.GroupBy(row => row.ScaledPoints ?? row.Points ?? 0).Where(group => group.Select(row => row.GameName).Distinct().Count() > 1))
                {
                    var games = group.Select(row => (row.GameName ?? string.Empty).ToUpperInvariant()).ToList();
                    CollectionAssert.AreEqual(games.OrderBy(name => name, StringComparer.Ordinal).ToList(), games, "points " + group.Key);
                }

                Assert.IsTrue(rows.GroupBy(row => row.ScaledPoints ?? row.Points ?? 0).Any(group => group.Select(row => row.GameName).Distinct().Count() > 1),
                    "some equal points span games");
                CollectionAssert.AreEqual(rows.Select(Key).ToList(), AchievementReadService.Rows(db, query).Select(Key).ToList(), "a second read");
                CollectionAssert.AreEqual(
                    rows.Select(AchievementReadService.KeyOf).Select(key => key.DefinitionId + key.ApiName + key.UserId).ToList(),
                    AchievementReadService.SortedKeys(db, query).Select(key => key.DefinitionId + key.ApiName + key.UserId).ToList(),
                    "the keys read in the rows' order");
            });
        }

        private sealed class PlanRow
        {
            public string Detail { get; set; }
        }

        [TestMethod]
        public void Achievements_MatchApiNamesIgnoringCase_WithTheSamePlan()
        {
            AchievementReadServiceTests.WithFixture(db =>
            {
                var upper = AchievementQuery.CurrentUser.ForAchievements(new[]
                {
                    new KeyValuePair<Guid, string>(Guid.Parse(Alpha), "A4"),
                    new KeyValuePair<Guid, string>(Guid.Parse(Alpha), "CUSTOM:ONE")
                });
                CollectionAssert.AreEquivalent(
                    new[] { Alpha + "/a4", Alpha + "/custom:one" },
                    AchievementReadService.Rows(db, upper).Select(Key).ToList());

                // The collation leaves the plan as it was: the game term still narrows the view.
                var command = AchievementQuerySql.Rows(upper);
                string Plan(string sql) => string.Join("\n", db.Load<PlanRow>("EXPLAIN QUERY PLAN " + sql, command.Args).Select(row => row.Detail));
                StringAssert.Contains(command.Sql, ") COLLATE NOCASE IN (");
                StringAssert.Contains(Plan(command.Sql), "SEARCH g USING INDEX IX_Games_PlayniteGameId");
                Assert.AreEqual(Plan(command.Sql.Replace(") COLLATE NOCASE IN (", ") IN (")), Plan(command.Sql));
            });
        }

        [TestMethod]
        public void LibraryListItems_CarryCategoryOrderArtAndGoals()
        {
            AchievementReadServiceTests.WithFixture(db =>
            {
                db.ExecuteNonQuery(
                    "INSERT INTO CustomLayerCategories (PlayniteGameId, Category, SortIndex, ImagePath) VALUES (?1, 'Default', 2, 'art/default.png');",
                    Alpha);
                db.ExecuteNonQuery(
                    "INSERT INTO CustomLayerAchievements (PlayniteGameId, ApiName, GoalOrder, UpdatedUtc) VALUES (?1, 'a3', 1, ?2);",
                    Alpha, "2025-01-01T00:00:00.0000000Z");
                db.ExecuteNonQuery("UPDATE CustomLayerAchievements SET GoalOrder = 0 WHERE ApiName = 'a1';");

                var items = LibraryRuntimeStateBuilder.Details(
                    AchievementReadService.Rows(db, AchievementQuery.CurrentUser.ForGames(new[] { Guid.Parse(Alpha) }))
                        .Select(row => SummaryCacheReader.MapRow(row, null, path => path == null ? null : "C:\\data\\" + path)),
                    api: null).ToDictionary(item => item.ApiName);

                Assert.IsTrue(items["a3"].IsGoal);
                Assert.AreEqual(1, items["a3"].GoalOrderIndex);
                Assert.IsFalse(items["a1"].IsGoal, "an unlocked goal is no goal");
                Assert.AreEqual(int.MaxValue, items["a1"].GoalOrderIndex);
                Assert.AreEqual(2, items["a3"].CategoryOrderIndex);
                Assert.AreEqual("C:\\data\\art/default.png", items["a3"].CategoryArtPath);
            });
        }
    }
}
