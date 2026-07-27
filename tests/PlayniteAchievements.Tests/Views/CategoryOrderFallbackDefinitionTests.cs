using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PlayniteAchievements.Tests.Views
{
    // Guards the definition-order fallback for category ordering: with no persisted
    // AchievementCategoryOrder, every surface must derive its first-seen category order
    // from the canonical definition/custom achievement order, not a live or display sort.
    [TestClass]
    public class CategoryOrderFallbackDefinitionTests
    {
        [TestMethod]
        public void Overview_SyncsCategorySummarySourceFromDefinitionOrderSnapshot()
        {
            var code = File.ReadAllText(FindRepoFile("source", "ViewModels", "OverviewViewModel.cs"));

            AssertContainsAll(
                code,
                "_selectedGameDefaultOrderedAchievements ?? new List<AchievementDisplayItem>());");
            Assert.IsFalse(
                code.Contains("_allSelectedGameAchievements ?? new List<AchievementDisplayItem>()"),
                "Overview category-summary source must not follow the live-sorted achievement list.");
        }

        [TestMethod]
        public void ViewAchievements_BindsCategorySummariesToDefinitionOrderedCollection()
        {
            var xaml = File.ReadAllText(FindRepoFile("source", "Views", "ViewAchievementsControl.xaml"));
            var code = File.ReadAllText(FindRepoFile("source", "ViewModels", "ViewAchievementsViewModel.cs"));

            AssertContainsAll(xaml, "CategorySummarySource=\"{Binding AllAchievements}\"");
            AssertContainsAll(
                code,
                "public ObservableCollection<AchievementDisplayItem> AllAchievements { get; }",
                "CollectionHelper.Replace(AllAchievements, _allAchievements);",
                "AllAchievements.Clear();");
        }

        [TestMethod]
        public void ModernThemeGrid_OrdersCategorySummariesAndOptionsByDefaultOrder()
        {
            var code = File.ReadAllText(FindRepoFile(
                "source", "Views", "ThemeIntegration", "Modern", "AchievementDataGridControl.xaml.cs"));

            AssertContainsAll(
                code,
                "var categorySummaryItems = new List<AchievementDisplayItem>(clonedItems);",
                "AchievementSortHelper.CreateExplicitOrderKeys(theme?.AchievementDefaultOrder ?? new List<AchievementDetail>()));",
                "AchievementsGrid.CategorySummarySource = categorySummaryItems;",
                "_controlBarAdapter.UpdateOptions(categorySummaryItems);",
                "_controlBarAdapter.UpdateOptions(AchievementsGrid?.CategorySummarySource ?? items);",
                "AchievementsGrid.CategorySummarySource = null;");
        }

        [TestMethod]
        public void FriendsOverview_BindsCategorySummariesToDefinitionOrderedSnapshot()
        {
            var xaml = File.ReadAllText(FindRepoFile("source", "Views", "FriendsOverviewControl.xaml"));
            var code = File.ReadAllText(FindRepoFile("source", "ViewModels", "FriendsOverviewViewModel.cs"));

            AssertContainsAll(xaml, "CategorySummarySource=\"{Binding SelectedFriendGameAllAchievements}\"");
            AssertContainsAll(
                code,
                "public BulkObservableCollection<FriendAchievementDisplayItem> SelectedFriendGameAllAchievements { get; }",
                "SelectedFriendGameAllAchievements.ReplaceAll(HasFriendGameSelection");
            Assert.IsFalse(
                code.Contains("SelectedFriendGameAllAchievements.ReplaceAll(_filteredAchievementsList"),
                "Friends overview category-summary source must not follow the filtered/sorted achievement list.");
        }

        [TestMethod]
        public void ViewFriendsAchievements_BindsCategorySummariesToDefinitionOrderedSnapshot()
        {
            var xaml = File.ReadAllText(FindRepoFile("source", "Views", "ViewFriendsAchievementsControl.xaml"));
            var code = File.ReadAllText(FindRepoFile("source", "ViewModels", "ViewFriendsAchievementsViewModel.cs"));

            AssertContainsAll(xaml, "CategorySummarySource=\"{Binding SelectedFriendAllAchievements}\"");
            AssertContainsAll(
                code,
                "public BulkObservableCollection<FriendAchievementDisplayItem> SelectedFriendAllAchievements { get; }",
                "SelectedFriendAllAchievements.ReplaceAll(SelectedFriend != null");
        }

        [TestMethod]
        public void FriendCache_FullAchievementLoadsUseDefinitionOrder()
        {
            var store = File.ReadAllText(FindRepoFile("source", "Services", "Database", "SqlNadoCacheStore.cs"));

            // Recent-unlock queries stay unlock-time ordered for LIMIT correctness; full comparison
            // loads order by AchievementDefinitions id so friend surfaces see the same canonical
            // definition order as the non-friend surfaces.
            AssertContainsAll(
                store,
                "sql.Append(requireUnlockTime",
                "? \" ORDER BY ua.UnlockTimeUtc DESC, u.DisplayName, g.GameName, ad.Id\"",
                ": \" ORDER BY u.DisplayName, g.GameName, ad.Id\");");
        }

        [TestMethod]
        public void ManageCategoriesTab_OrdersCategoryRowsFromDefinitionOrderedRows()
        {
            var code = File.ReadAllText(FindRepoFile(
                "source", "ViewModels", "ManageAchievements", "ManageAchievementsCategoryViewModel.cs"));

            AssertContainsAll(
                code,
                "private List<ManageAchievementsCategoryItem> _definitionOrderedRows",
                "_definitionOrderedRows.Count > 0 ? _definitionOrderedRows : _allRows");
        }

        [TestMethod]
        public void ManageFiltersTab_BuildsCategoryOptionsFromCanonicalOrder()
        {
            var code = File.ReadAllText(FindRepoFile(
                "source", "ViewModels", "ManageAchievements", "ManageAchievementsFiltersViewModel.cs"));

            AssertContainsAll(
                code,
                "canonicalAchievements = orderedAchievements;",
                "canonicalAchievements = rawAchievements;",
                "                    canonicalAchievements,");
        }

        private static void AssertContainsAll(string content, params string[] expected)
        {
            var missing = expected
                .Where(value => !content.Contains(value))
                .ToList();

            CollectionAssert.AreEqual(new List<string>(), missing);
        }

        private static string FindRepoFile(params string[] parts)
        {
            var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (directory != null)
            {
                var path = Path.Combine(new[] { directory.FullName }.Concat(parts).ToArray());
                if (File.Exists(path))
                {
                    return path;
                }

                directory = directory.Parent;
            }

            Assert.Fail("Could not find " + Path.Combine(parts));
            return null;
        }
    }
}
