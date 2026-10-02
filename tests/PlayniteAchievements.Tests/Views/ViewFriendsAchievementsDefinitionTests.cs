using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Views.Settings.Controls;

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

        /// <summary>
        /// The friends achievements window's grids are configured from each grid's own display
        /// settings popup, so what matters is that every surface it uses has a capability entry
        /// and that the entries describe the right rows.
        /// </summary>
        [TestMethod]
        public void DisplaySurfaces_CoverFriendsAchievementsGrids()
        {
            var achievements = GridDisplaySurfaces.Resolve(
                GridOptionKind.Achievement,
                GridOptionKeys.Achievement.ViewFriendsAchievements);
            Assert.IsTrue(achievements.ShowCategoryModeRow);

            // The selected-friend surface reads StartInCategoryMode from its parent, so offering
            // the row there would present an inert control.
            var selectedFriend = GridDisplaySurfaces.Resolve(
                GridOptionKind.Achievement,
                GridOptionKeys.Achievement.ViewFriendsAchievementsSelectedFriend);
            Assert.IsFalse(selectedFriend.ShowCategoryModeRow);

            // This window's game grids are full lists, unlike the single-game header row in the
            // achievements window, so they keep every row.
            var gameSummaries = GridDisplaySurfaces.Resolve(
                GridOptionKind.GameSummaries,
                GridOptionKeys.GameSummaries.ViewFriendsAchievements);
            Assert.IsTrue(gameSummaries.ShowControlBarRow);

            var friendSummaries = GridDisplaySurfaces.Resolve(
                GridOptionKind.FriendSummaries,
                GridOptionKeys.FriendSummaries.ViewFriendsAchievements);
            Assert.IsTrue(friendSummaries.ShowSortRow);
        }

        /// <summary>
        /// Every surface in the catalog needs an explicit capability entry: without one a new
        /// surface silently falls back to offering every row, including rows it cannot honour.
        /// </summary>
        [TestMethod]
        public void DisplaySurfaces_DescribeEverySurfaceKey()
        {
            var missing = new List<string>();
            AssertEveryKeyMapped(typeof(GridOptionKeys.Achievement), GridOptionKind.Achievement, missing);
            AssertEveryKeyMapped(typeof(GridOptionKeys.GameSummaries), GridOptionKind.GameSummaries, missing);
            AssertEveryKeyMapped(typeof(GridOptionKeys.FriendSummaries), GridOptionKind.FriendSummaries, missing);
            AssertEveryKeyMapped(typeof(GridOptionKeys.CategorySummaries), GridOptionKind.CategorySummaries, missing);

            CollectionAssert.AreEqual(new List<string>(), missing);
        }

        /// <summary>
        /// Showcase and start page grids reach their options two ways -- the widget gear and the
        /// grid's own popup -- so both must describe the same rows. The live surfaces carry a
        /// per-instance suffix, so every instance has to resolve through its base key.
        /// </summary>
        [TestMethod]
        public void DisplaySurfaces_ResolveShowcaseInstancesThroughTheirBaseKey()
        {
            var gameBase = GridDisplaySurfaces.Resolve(
                GridOptionKind.GameSummaries,
                ShowcaseGridSurfaces.GameSummaries);
            var gameInstance = GridDisplaySurfaces.Resolve(
                GridOptionKind.GameSummaries,
                ShowcaseGridSurfaces.ForInstance(ShowcaseGridSurfaces.GameSummaries, "abc"));
            Assert.AreSame(gameBase, gameInstance);

            // The pinned-games grid is the one surface whose order the user controls, so it is the
            // only one offering the order-preserving sort choice.
            Assert.IsTrue(gameInstance.ShowGameSortPinOrderChoice);
            Assert.IsFalse(GridDisplaySurfaces
                .Resolve(GridOptionKind.GameSummaries, GridOptionKeys.GameSummaries.Overview)
                .ShowGameSortPinOrderChoice);

            var achievementInstance = GridDisplaySurfaces.Resolve(
                GridOptionKind.Achievement,
                ShowcaseGridSurfaces.ForInstance(ShowcaseGridSurfaces.RecentAchievements, "abc"));
            Assert.AreSame(
                GridDisplaySurfaces.Resolve(GridOptionKind.Achievement, ShowcaseGridSurfaces.RecentAchievements),
                achievementInstance);
        }

        private static void AssertEveryKeyMapped(Type keyHolder, GridOptionKind kind, List<string> missing)
        {
            var table = GridDisplaySurfaces.GetTable(kind);
            var keys = keyHolder
                .GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(field => field.IsLiteral && field.FieldType == typeof(string))
                .Select(field => (string)field.GetRawConstantValue());

            foreach (var key in keys)
            {
                if (!table.ContainsKey(key))
                {
                    missing.Add(kind + "." + key);
                }
            }
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
