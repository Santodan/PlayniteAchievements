using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PlayniteAchievements.Tests.Views
{
    [TestClass]
    public class ViewFriendsAchievementsDefinitionTests
    {
        [TestMethod]
        public void Window_WiresSeparateAggregateAndSelectedFriendSummaryGrids()
        {
            var xaml = File.ReadAllText(FindRepoFile("source", "Views", "ViewFriendsAchievementsControl.xaml"));

            AssertContainsAll(
                xaml,
                "x:Name=\"GameSummaryGridControl\"",
                "x:Name=\"SelectedFriendGameSummaryGridControl\"",
                "ColumnSettingsKey=\"ViewFriendsAchievementsGameSummaries\"",
                "ColumnSettingsKey=\"ViewFriendsAchievementsSelectedFriendGameSummaries\"",
                "Visibility=\"{Binding HasFriendSelection, Converter={StaticResource InverseBoolToVis}}\"",
                "Visibility=\"{Binding HasFriendSelection, Converter={StaticResource BoolToVis}}\"",
                "RowPreviewMouseRightButtonDown=\"GameSummaryRow_PreviewMouseRightButtonDown\"",
                "RowPreviewMouseRightButtonUp=\"GameSummaryRow_PreviewMouseRightButtonUp\"");
            AssertContainsNone(
                xaml,
                "ColumnSettingsKey=\"ViewAchievementsGameSummaries\"",
                "ViewFriendsHeaderButtonStyle",
                "Command=\"{Binding RefreshCommand}\"",
                "Command=\"{Binding OpenGameInLibraryCommand}\"");
        }

        [TestMethod]
        public void Window_EnablesCategoryModeForSelectedFriend()
        {
            var xaml = File.ReadAllText(FindRepoFile("source", "Views", "ViewFriendsAchievementsControl.xaml"));
            var code = File.ReadAllText(FindRepoFile("source", "Views", "ViewFriendsAchievementsControl.xaml.cs"));

            AssertContainsAll(
                xaml,
                "ColumnSettingsKey=\"{Binding AchievementColumnSettingsKey}\"",
                "EnableCategoryMode=\"{Binding HasFriendSelection}\"",
                "CategoryColumnSettingsKey=\"ViewFriendsAchievementsCategorySummaries\"",
                "HideBackButton=\"True\"",
                "HideCategorySummaryRow=\"{Binding HideCategorySummaryRow}\"",
                "DrilledCategory=\"{Binding SelectedCategoryName, Mode=OneWayToSource}\"",
                "ShowGameColumn=\"True\"",
                "ShowFriendColumn=\"True\"",
                "MouseLeftButtonUp=\"GameNameBreadcrumb_Click\"");
            AssertContainsAll(
                code,
                "GameRowContextMenuBuilder.BuildGameMenu",
                "OpenManageAchievementsView",
                "ExitDrilledCategory");
        }

        [TestMethod]
        public void GameSummariesGrid_DefinesViewFriendsAchievementsSurfaces()
        {
            var code = File.ReadAllText(FindRepoFile("source", "Views", "Controls", "GameSummariesGridControl.xaml.cs"));

            AssertContainsAll(
                code,
                "ViewFriendsAchievementsGameSummaries",
                "ViewFriendsAchievementsSelectedFriendGameSummaries",
                "ViewFriendsAchievementsCategorySummaries",
                "GridSurface.ViewFriendsAchievements",
                "GridSurface.ViewFriendsAchievementsSelectedFriend",
                "GridSurface.ViewFriendsAchievementsCategory",
                "GetGameSummaries(GridOptionKeys.GameSummaries.ViewFriendsAchievements)",
                "GetGameSummaries(GridOptionKeys.GameSummaries.ViewFriendsAchievementsSelectedFriend)",
                "GetCategorySummaries(GridOptionKeys.CategorySummaries.ViewFriendsAchievements)");
        }

        [TestMethod]
        public void AchievementsGrid_DefaultsFriendColumnsVisibleForViewFriendsAchievements()
        {
            var code = File.ReadAllText(FindRepoFile("source", "Views", "Controls", "AchievementDataGridControl.xaml.cs"));

            AssertContainsAll(
                code,
                "[\"ViewFriendsAchievements\"] = CreateAchievementVisibility(",
                "[\"ViewFriendsAchievementsSelectedFriendAchievements\"] = CreateAchievementVisibility(",
                "[\"ViewFriendsAchievements\"] = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)",
                "[\"ViewFriendsAchievementsSelectedFriendAchievements\"] = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)",
                "UnlockDateDisplayMode");
        }

        [TestMethod]
        public void DisplaySettings_RegistersFriendsAchievementsSection()
        {
            var tab = File.ReadAllText(FindRepoFile("source", "Views", "Settings", "Display", "DisplaySettingsTab.xaml.cs"));
            var section = File.ReadAllText(FindRepoFile("source", "Views", "Settings", "Display", "FriendsAchievementsWindowDisplaySection.xaml"));
            var overviewSection = File.ReadAllText(FindRepoFile("source", "Views", "Settings", "Display", "FriendsOverviewDisplaySection.xaml"));

            AssertContainsAll(
                tab,
                "FriendsAchievementsWindow",
                "FriendsAchievementsWindowDisplaySection",
                "_friendsAchievementsNavigationItem");
            AssertContainsAll(
                section,
                "FriendSummaries[ViewFriendsAchievements]",
                "GameSummaries[ViewFriendsAchievements]",
                "Achievement[ViewFriendsAchievements]",
                "CategorySummaries[ViewFriendsAchievements]",
                "ShowCategoryModeRow=\"True\"");
            AssertContainsNone(
                section,
                "GameSummaries[ViewFriendsAchievementsSelectedFriend]");
            AssertContainsNone(
                overviewSection,
                "Achievement[ViewFriendsAchievements]",
                "GameSummaries[FriendsOverviewSelectedFriend]");
        }

        private static void AssertContainsAll(string content, params string[] expected)
        {
            var missing = expected
                .Where(value => !content.Contains(value))
                .ToList();

            CollectionAssert.AreEqual(new List<string>(), missing);
        }

        private static void AssertContainsNone(string content, params string[] unexpected)
        {
            var present = unexpected
                .Where(value => content.Contains(value))
                .ToList();

            CollectionAssert.AreEqual(new List<string>(), present);
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
