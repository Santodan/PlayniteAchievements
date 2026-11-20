using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Playnite.SDK.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.ThemeIntegration;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.Database;
using PlayniteAchievements.Services.ThemeIntegration;
using PlayniteAchievements.ViewModels;
using SqlNado;

namespace PlayniteAchievements.SqlNado.Tests
{
    /// <summary>
    /// Each library-wide theme list read from SQL, on the seeded database of
    /// <see cref="AchievementReadServiceTests"/>, against what the whole-library build computed in
    /// C# from the same rows: its sort, its goals-first order and its 180-day window.
    /// </summary>
    [TestClass]
    public class ThemeLibraryListTests
    {
        private static readonly AchievementQuery Library = AchievementQuery.CurrentUser.WithSummaryRules();

        private static List<string> Read(SQLiteDatabase db, string name, LibraryAchievementViewState view = null) =>
            AchievementReadService.Rows(db, ThemeIntegrationService.LibraryListQuery(name, Library, view ?? new LibraryAchievementViewState()))
                .Select(row => row.ApiName)
                .ToList();

        // The rows as the whole-library build held them, with a goal on a locked achievement.
        private static List<AchievementDetail> Details(SQLiteDatabase db) =>
            AchievementReadService.Rows(db, Library).Select(row => new AchievementDetail
            {
                ApiName = row.ApiName,
                DisplayName = row.DisplayName,
                Unlocked = row.Unlocked != 0,
                UnlockTimeUtc = AchievementSqlFunctions.ParseUtc(row.UnlockTimeUtc),
                GlobalPercentUnlocked = row.GlobalPercentUnlocked,
                Rarity = AchievementSqlFunctions.ParseStoredRarity(row.Rarity),
                IsGoal = row.IsGoal != 0 && row.Unlocked == 0,
                GoalOrderIndex = (int)(row.GoalOrder ?? int.MaxValue),
                Game = new Game { Name = row.GameName }
            }).ToList();

        private static List<string> Sorted(IEnumerable<AchievementDetail> details, string path, ListSortDirection direction, bool goalsFirst) =>
            (goalsFirst
                ? AchievementSortHelper.CreateGoalsFirstDetailList(AchievementSortHelper.CreateSortedDetailList(details, path, direction, includeGameNameTieBreak: true))
                : AchievementSortHelper.CreateSortedDetailList(details, path, direction, includeGameNameTieBreak: true))
            .Select(detail => detail.ApiName)
            .ToList();

        // The same rows in the same rarity order; rows of equal rarity may come in another order.
        private static void AssertRarityOrder(IEnumerable<AchievementDetail> details, List<string> expected, List<string> actual)
        {
            CollectionAssert.AreEquivalent(expected, actual);
            var byName = details.ToDictionary(detail => detail.ApiName);
            CollectionAssert.AreEqual(
                expected.Select(name => byName[name].RaritySortValue).ToList(),
                actual.Select(name => byName[name].RaritySortValue).ToList());
        }

        [TestMethod]
        public void WholeLibraryLists_MatchTheBuildsSortAndGoalsFirst()
        {
            WithFixture(db =>
            {
                var all = Details(db);
                Assert.IsTrue(all.Any(detail => detail.IsGoal), "the fixture has a goal");
                CollectionAssert.AreEqual(
                    Sorted(all, nameof(AchievementDisplayItem.UnlockTime), ListSortDirection.Ascending, true),
                    Read(db, nameof(ModernThemeBindings.AllAchievementsUnlockAsc)));
                CollectionAssert.AreEqual(
                    Sorted(all, nameof(AchievementDisplayItem.UnlockTime), ListSortDirection.Descending, true),
                    Read(db, nameof(ModernThemeBindings.AllAchievementsUnlockDesc)));
                AssertRarityOrder(
                    all,
                    Sorted(all, nameof(AchievementDisplayItem.RaritySortValue), ListSortDirection.Ascending, true),
                    Read(db, nameof(ModernThemeBindings.AllAchievementsRarityAsc)));
                AssertRarityOrder(
                    all,
                    Sorted(all, nameof(AchievementDisplayItem.RaritySortValue), ListSortDirection.Descending, true),
                    Read(db, nameof(ModernThemeBindings.AllAchievementsRarityDesc)));
            });
        }

        [TestMethod]
        public void RecentLists_MatchTheBuildsUnlockedRowsAndWindow()
        {
            WithFixture(db =>
            {
                var unlocked = Details(db).Where(detail => detail.Unlocked && detail.UnlockTimeUtc.HasValue).ToList();
                CollectionAssert.AreEqual(
                    Sorted(unlocked, nameof(AchievementDisplayItem.UnlockTime), ListSortDirection.Descending, false),
                    Read(db, nameof(ModernThemeBindings.MostRecentUnlocks)));

                // The fixture's unlocks are of 2025; move two into the window.
                db.ExecuteNonQuery("UPDATE CustomLayerAchievements SET UnlockTimeUtc = ? WHERE ApiName = 'a1';", DateTime.UtcNow.AddDays(-3).ToString("o"));
                db.ExecuteNonQuery("UPDATE CustomLayerAchievements SET UnlockTimeUtc = ? WHERE ApiName = 'custom:one';", DateTime.UtcNow.AddDays(-30).ToString("o"));
                var cutoff = DateTime.UtcNow.AddDays(-180);
                var recent = Details(db).Where(detail => detail.Unlocked && detail.UnlockTimeUtc >= cutoff).ToList();
                Assert.AreEqual(2, recent.Count);
                CollectionAssert.AreEqual(
                    Sorted(recent, nameof(AchievementDisplayItem.RaritySortValue), ListSortDirection.Ascending, false),
                    Read(db, nameof(ModernThemeBindings.RarestRecentUnlocks)));
            });
        }

        [TestMethod]
        public void DynamicLibraryList_AppliesItsProviderGameFilterAndSort()
        {
            WithFixture(db =>
            {
                var view = new LibraryAchievementViewState { ProviderKey = "Steam", FilterKey = "Unlocked+Common+Uncommon", SortKey = "Rarity", SortDirectionKey = "Ascending" };
                var kept = Details(db).Where(detail => detail.Unlocked && (detail.Rarity == RarityTier.Common || detail.Rarity == RarityTier.Uncommon)).ToList();
                var steam = AchievementReadService.Rows(db, Library.WhereProvider("Steam")).Select(row => row.ApiName).ToList();
                AssertRarityOrder(
                    kept,
                    Sorted(kept.Where(detail => steam.Contains(detail.ApiName)), nameof(AchievementDisplayItem.RaritySortValue), ListSortDirection.Ascending, true),
                    Read(db, nameof(ModernThemeBindings.DynamicLibraryAchievements), view));

                view.GameKey = AchievementReadServiceTests.Alpha;
                view.ProviderKey = "All";
                CollectionAssert.IsSubsetOf(
                    Read(db, nameof(ModernThemeBindings.DynamicLibraryAchievements), view),
                    AchievementReadService.Rows(db, Library.ForGames(new[] { Guid.Parse(AchievementReadServiceTests.Alpha) })).Select(row => row.ApiName).ToList());
            });
        }

        private static void WithFixture(Action<SQLiteDatabase> test) =>
            AchievementReadServiceTests.WithFixture(db =>
            {
                // A goal on a locked achievement: a3, and the authored custom:two first.
                db.ExecuteNonQuery(
                    "INSERT INTO CustomLayerAchievements (PlayniteGameId, ApiName, GoalOrder, UpdatedUtc) VALUES (?1, 'a3', 1, ?2), (?1, 'custom:two', 0, ?2);",
                    AchievementReadServiceTests.Alpha, "2025-01-01T00:00:00.0000000Z");
                test(db);
            });
    }
}
