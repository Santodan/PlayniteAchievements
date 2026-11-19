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
                    "INSERT INTO AchievementOverrides (PlayniteGameId, ApiName, GoalOrder, UpdatedUtc) VALUES (?1, 'a3', 1, ?2);",
                    Alpha, "2025-01-01T00:00:00.0000000Z");
                db.ExecuteNonQuery("UPDATE AchievementOverrides SET GoalOrder = 0 WHERE ApiName = 'custom:two';");

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
                Assert.AreEqual("Exophase", provider.First(row => row.ProviderKey != null).ProviderKey);

                var byDefault = DynamicThemeFilterEvaluator.ApplyAchievementSort(AchievementQuery.CurrentUser, "Default", null);
                Assert.AreEqual(AchievementSort.UnlockTime, byDefault.Sort);
                Assert.IsTrue(byDefault.Descending);
            });
        }
    }
}
