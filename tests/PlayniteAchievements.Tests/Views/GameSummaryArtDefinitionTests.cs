using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PlayniteAchievements.Tests.Views
{
    // Guards the "Use for Game Summaries" category selection: both game-summary
    // projection paths (hydrated builder and cached overview fast path) must resolve
    // the selected category art, and the Manage Categories grid must expose the
    // exclusive radio column that persists the selection.
    [TestClass]
    public class GameSummaryArtDefinitionTests
    {
        [TestMethod]
        public void BothGameSummaryProjectionPaths_ResolveSummaryCategoryArt()
        {
            var builder = File.ReadAllText(FindRepoFile(
                "source", "Services", "Summaries", "GameSummaryItemBuilder.cs"));
            var overview = File.ReadAllText(FindRepoFile(
                "source", "Services", "Overview", "OverviewDataBuilder.cs"));

            AssertContainsAll(
                builder,
                "GameSummaryArtResolver.Resolve(",
                "GameLogo = summaryArt ?? presentation.IconPath",
                "GameCoverPath = summaryArt ?? presentation.CoverPath");
            AssertContainsAll(
                overview,
                "GameSummaryArtResolver.ResolveForGame(game.PlayniteGameId)",
                "GameLogo = summaryArt ?? presentation.IconPath",
                "GameCoverPath = summaryArt ?? presentation.CoverPath");
        }

        [TestMethod]
        public void ManageCategoriesTab_ExposesExclusiveSummarySelectionColumn()
        {
            var xaml = File.ReadAllText(FindRepoFile(
                "source", "Views", "ManageAchievements", "ManageAchievementsCategoryTab.xaml"));
            var code = ReadCategoryViewModelSources();

            AssertContainsAll(
                xaml,
                "LOCPlayAch_Column_UseForGameSummaries",
                "IsChecked=\"{Binding IsSummarySelected, Mode=OneWay}\"",
                "SummaryCategoryRadioButton_PreviewMouseLeftButtonDown");
            AssertContainsAll(
                code,
                "public bool IsSummarySelected",
                "row.IsSummarySelected = false;");
        }

        [TestMethod]
        public void HydrationPaths_CarrySummaryCategorySelection()
        {
            var hydrator = File.ReadAllText(FindRepoFile(
                "source", "Services", "Hydration", "GameDataHydrator.cs"));

            // Both Hydrate and HydrateForOverview must copy the selection.
            var occurrences = CountOccurrences(
                hydrator,
                "data.GameSummaryCategory = customData.GameSummaryCategory;");
            Assert.AreEqual(2, occurrences,
                "Both hydrate paths must copy GameSummaryCategory onto the game data.");
        }

        [TestMethod]
        public void VisibleProjectionClone_PreservesSummaryCategorySelection()
        {
            var service = File.ReadAllText(FindRepoFile(
                "source", "Services", "Achievements", "AchievementDataService.cs"));

            // Games with filtered achievements go through the projection clone; it must
            // carry the selection or their summary art silently falls back to defaults.
            AssertContainsAll(
                service,
                "GameSummaryCategory = source.GameSummaryCategory,");
        }

        [TestMethod]
        public void ManageAchievementsCover_ResolvesSummaryCategoryArt()
        {
            var viewModel = File.ReadAllText(FindRepoFile(
                "source", "ViewModels", "ManageAchievements", "ManageAchievementsViewModel.cs"));

            AssertContainsAll(
                viewModel,
                "GameSummaryArtResolver.ResolveForGame(_gameId)");
        }

        private static int CountOccurrences(string content, string value)
        {
            var count = 0;
            var index = 0;
            while ((index = content.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += value.Length;
            }

            return count;
        }

        private static void AssertContainsAll(string content, params string[] expected)
        {
            var missing = expected
                .Where(value => !content.Contains(value))
                .ToList();

            CollectionAssert.AreEqual(new List<string>(), missing);
        }

        /// <summary>
        /// The category view model is split across partials, with its row types in a fourth file.
        /// Assert against the whole set so relocating a member between them is a refactor rather
        /// than a test failure.
        /// </summary>
        private static string ReadCategoryViewModelSources()
        {
            var anchor = FindRepoFile(
                "source", "ViewModels", "ManageAchievements", "ManageAchievementsCategoryViewModel.cs");

            return string.Concat(Directory
                .EnumerateFiles(Path.GetDirectoryName(anchor), "ManageAchievementsCategory*.cs")
                .OrderBy(path => path, StringComparer.Ordinal)
                .Select(File.ReadAllText));
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
