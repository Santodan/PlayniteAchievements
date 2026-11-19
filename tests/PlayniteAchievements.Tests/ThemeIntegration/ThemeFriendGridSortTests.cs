using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.ThemeIntegration;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.Database;
using PlayniteAchievements.Services.ThemeIntegration;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.ThemeIntegration.Tests
{
    /// <summary>
    /// The theme friend grid shows <c>DynamicFriendAchievements</c>, a paged list its query sorts:
    /// a header click sets the theme's sort key and direction, which the list's query reads.
    /// </summary>
    [TestClass]
    public class ThemeFriendGridSortTests
    {
        private const string Asc = DynamicThemeViewKeys.Ascending;
        private const string Desc = DynamicThemeViewKeys.Descending;

        private static (string, string)? Click(string column, string key, string direction) =>
            AchievementSortHelper.ResolveDynamicAchievementHeaderSort(column, key, direction);

        [TestMethod]
        public void HeaderClicks_CycleTheListsSortKeyAndDirection_BackToNewestFirst()
        {
            Assert.AreEqual((DynamicThemeViewKeys.UnlockTime, Asc), Click("UnlockTime", DynamicThemeViewKeys.UnlockTime, Desc));
            Assert.AreEqual((DynamicThemeViewKeys.UnlockTime, Desc), Click("UnlockTime", DynamicThemeViewKeys.UnlockTime, Asc));

            Assert.AreEqual((DynamicThemeViewKeys.Game, Asc), Click("SortingName", DynamicThemeViewKeys.UnlockTime, Desc), "the game column sorts by the Game key");
            Assert.AreEqual((DynamicThemeViewKeys.Game, Desc), Click("SortingName", DynamicThemeViewKeys.Game, Asc));
            Assert.AreEqual((DynamicThemeViewKeys.UnlockTime, Desc), Click("SortingName", DynamicThemeViewKeys.Game, Desc), "the third click returns to the default");

            Assert.AreEqual((DynamicThemeViewKeys.Rarity, Asc), Click("RaritySortValue", DynamicThemeViewKeys.UnlockTime, Desc));
            Assert.AreEqual(("FriendName", Asc), Click("FriendName", DynamicThemeViewKeys.UnlockTime, Desc), "a column no theme sort key names is its own key");
            Assert.AreEqual(("FriendName", Desc), Click("FriendName", "FriendName", Asc));
            Assert.AreEqual(("CategoryKind", Asc), Click("CategoryKind", "FriendName", Desc));
            Assert.IsNull(Click("Description", DynamicThemeViewKeys.UnlockTime, Desc), "a column the query cannot sort by");
        }

        [TestMethod]
        public void TheSortKeyAHeaderSets_OrdersTheListsQueryAsTheColumn()
        {
            foreach (var column in new[] { "UnlockTime", "DisplayName", "SortingName", "RaritySortValue", "Points", "TrophyType", "CategoryLabel", "FriendName", "CategoryKind" })
            {
                var sort = Click(column, DynamicThemeViewKeys.UnlockTime, Desc).Value;
                var query = DynamicThemeFilterEvaluator.ApplyAchievementSort(AchievementQuery.AllFriends, sort.Item1, sort.Item2);
                Assert.AreEqual(AchievementQuery.SortOfColumn(column), query.Sort, column);
            }
        }

        [TestMethod]
        public void TheThemeGrid_SortsThroughTheTheme_AndTheGridNeverReordersAPagedList()
        {
            var grid = File.ReadAllText(FindRepoFile("source", "Views", "ThemeIntegration", "Modern", "AchievementFriendAchievementsGridControl.xaml"));
            StringAssert.Contains(grid, "UseExternalSorting=\"True\"");
            StringAssert.Contains(grid, "Sorting=\"RowsGrid_Sorting\"");

            var code = File.ReadAllText(FindRepoFile("source", "Views", "ThemeIntegration", "Modern", "AchievementFriendAchievementsGridControl.xaml.cs"));
            StringAssert.Contains(code, "AchievementSortHelper.ResolveDynamicAchievementHeaderSort(");
            StringAssert.Contains(code, "SortDynamicFriendAchievementsCommand?.Execute(sort.Value.Key)");
            StringAssert.Contains(code, "SetDynamicFriendAchievementsSortDirectionCommand?.Execute(sort.Value.Direction)");

            var control = File.ReadAllText(FindRepoFile("source", "Views", "Controls", "AchievementDataGridControl.xaml.cs"));
            StringAssert.Contains(control, "if (ItemsSource is IPartiallyLoadedRows)");
            StringAssert.Contains(control, "if (items == null || ItemsSource is IPartiallyLoadedRows)");
        }

        [TestMethod]
        public void AFriendRowReadAgain_TakesItsFriendSelfAndComparisonColumns()
        {
            var kept = new FriendAchievementDisplayItem { FriendName = "Old", UnlockedBySelf = false };
            kept.ApplyComparison("Someone", null, null, false);
            var fresh = new FriendAchievementDisplayItem
            {
                FriendName = "New",
                FriendIsFavorite = true,
                UnlockedBySelf = true,
                SelfUnlockTimeUtc = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc),
                FriendOwnsGame = true,
                DisplayName = "Fresh"
            };

            kept.RefreshFrom(fresh);

            Assert.AreEqual("New", kept.FriendName);
            Assert.IsTrue(kept.FriendIsFavorite);
            Assert.IsTrue(kept.UnlockedBySelf);
            Assert.AreEqual(fresh.SelfUnlockTimeUtc, kept.SelfUnlockTimeUtc);
            Assert.IsTrue(kept.FriendOwnsGame);
            Assert.IsFalse(kept.HasComparison, "a read with no comparison clears it");
            Assert.AreEqual("Fresh", kept.DisplayName);

            fresh.ApplyComparison("Other", "avatar", fresh.SelfUnlockTimeUtc, true, "Owner");
            kept.RefreshFrom(fresh);
            Assert.IsTrue(kept.HasComparison);
            Assert.AreEqual("Other", kept.ComparisonFriendName);
            Assert.AreEqual("Owner", kept.ComparisonOwnerName);
            Assert.IsTrue(kept.ComparisonUnlocked);
        }

        private static string FindRepoFile(params string[] parts)
        {
            var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (directory != null)
            {
                var candidate = Path.Combine(new[] { directory.FullName }.Concat(parts).ToArray());
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }

            Assert.Fail("Could not find " + Path.Combine(parts));
            return null;
        }
    }
}
