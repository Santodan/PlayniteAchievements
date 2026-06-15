using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Settings;

namespace PlayniteAchievements.Tests.StartPage
{
    [TestClass]
    public class StartPageSettingsTests
    {
        [TestMethod]
        public void CloneAndCopyFrom_PreserveStartPageColumnSettings()
        {
            var source = new PersistedSettings
            {
                StartPageAchievementColumnVisibility = new Dictionary<string, bool>
                {
                    ["Achievement"] = false
                },
                StartPageAchievementColumnWidths = new Dictionary<string, double>
                {
                    ["Achievement"] = 320
                },
                StartPageAchievementColumnOrder = new Dictionary<string, int>
                {
                    ["Achievement"] = 2
                },
                StartPageAchievementColumnAlignments = new Dictionary<string, GridAlignment>
                {
                    ["Achievement"] = GridAlignment.Center
                },
                StartPageGameSummariesColumnVisibility = new Dictionary<string, bool>
                {
                    ["GameSummaryProvider"] = false
                },
                StartPageGameSummariesColumnWidths = new Dictionary<string, double>
                {
                    ["GameSummaryProvider"] = 140
                },
                StartPageGameSummariesColumnOrder = new Dictionary<string, int>
                {
                    ["GameSummaryProvider"] = 3
                },
                StartPageGameSummariesColumnAlignments = new Dictionary<string, GridAlignment>
                {
                    ["GameSummaryProvider"] = GridAlignment.Right
                },
                StartPageGameSummariesColumnVerticalAlignments = new Dictionary<string, GridVerticalAlignment>
                {
                    ["GameSummaryProvider"] = GridVerticalAlignment.Bottom
                },
                StartPageGameSummariesColumnHeaderAlignments = new Dictionary<string, GridAlignment>
                {
                    ["GameSummaryProvider"] = GridAlignment.Center
                }
            };

            var clone = source.Clone();
            var copy = new PersistedSettings();
            copy.CopyFrom(source);

            Assert.IsFalse(clone.StartPageAchievementColumnVisibility["Achievement"]);
            Assert.AreEqual(320, clone.StartPageAchievementColumnWidths["Achievement"]);
            Assert.AreEqual(2, clone.StartPageAchievementColumnOrder["Achievement"]);
            Assert.AreEqual(GridAlignment.Center, clone.StartPageAchievementColumnAlignments["Achievement"]);
            Assert.IsFalse(clone.StartPageGameSummariesColumnVisibility["GameSummaryProvider"]);
            Assert.AreEqual(140, clone.StartPageGameSummariesColumnWidths["GameSummaryProvider"]);
            Assert.AreEqual(3, clone.StartPageGameSummariesColumnOrder["GameSummaryProvider"]);
            Assert.AreEqual(GridAlignment.Right, clone.StartPageGameSummariesColumnAlignments["GameSummaryProvider"]);
            Assert.AreEqual(GridVerticalAlignment.Bottom, clone.StartPageGameSummariesColumnVerticalAlignments["GameSummaryProvider"]);
            Assert.AreEqual(GridAlignment.Center, clone.StartPageGameSummariesColumnHeaderAlignments["GameSummaryProvider"]);

            Assert.IsFalse(copy.StartPageAchievementColumnVisibility["Achievement"]);
            Assert.AreEqual(320, copy.StartPageAchievementColumnWidths["Achievement"]);
            Assert.AreEqual(2, copy.StartPageAchievementColumnOrder["Achievement"]);
            Assert.AreEqual(GridAlignment.Center, copy.StartPageAchievementColumnAlignments["Achievement"]);
            Assert.IsFalse(copy.StartPageGameSummariesColumnVisibility["GameSummaryProvider"]);
            Assert.AreEqual(140, copy.StartPageGameSummariesColumnWidths["GameSummaryProvider"]);
            Assert.AreEqual(3, copy.StartPageGameSummariesColumnOrder["GameSummaryProvider"]);
            Assert.AreEqual(GridAlignment.Right, copy.StartPageGameSummariesColumnAlignments["GameSummaryProvider"]);
            Assert.AreEqual(GridVerticalAlignment.Bottom, copy.StartPageGameSummariesColumnVerticalAlignments["GameSummaryProvider"]);
            Assert.AreEqual(GridAlignment.Center, copy.StartPageGameSummariesColumnHeaderAlignments["GameSummaryProvider"]);

            Assert.AreNotSame(source.StartPageAchievementColumnVisibility, clone.StartPageAchievementColumnVisibility);
            Assert.AreNotSame(source.StartPageAchievementColumnWidths, clone.StartPageAchievementColumnWidths);
            Assert.AreNotSame(source.StartPageAchievementColumnOrder, clone.StartPageAchievementColumnOrder);
            Assert.AreNotSame(source.StartPageAchievementColumnAlignments, clone.StartPageAchievementColumnAlignments);
            Assert.AreNotSame(source.StartPageGameSummariesColumnVisibility, copy.StartPageGameSummariesColumnVisibility);
            Assert.AreNotSame(source.StartPageGameSummariesColumnWidths, copy.StartPageGameSummariesColumnWidths);
            Assert.AreNotSame(source.StartPageGameSummariesColumnOrder, copy.StartPageGameSummariesColumnOrder);
            Assert.AreNotSame(source.StartPageGameSummariesColumnAlignments, copy.StartPageGameSummariesColumnAlignments);
            Assert.AreNotSame(source.StartPageGameSummariesColumnVerticalAlignments, copy.StartPageGameSummariesColumnVerticalAlignments);
            Assert.AreNotSame(source.StartPageGameSummariesColumnHeaderAlignments, copy.StartPageGameSummariesColumnHeaderAlignments);
        }

        [TestMethod]
        public void CloneAndCopyFrom_PreserveStartPageWidgetSettings()
        {
            var source = new PersistedSettings();
            source.StartPageGameSummariesGrid.UseCoverImages = false;
            source.StartPageGameSummariesGrid.ShowGameMetadata = false;
            source.StartPageGameSummariesGrid.ShowCompletionBorder = false;
            source.StartPageGameSummariesGrid.ShowColumnHeaders = false;
            source.StartPageGameSummariesGrid.RowHeight = 72d;
            source.StartPageGameSummariesGrid.MaxRows = 11;
            source.StartPageGameSummariesGrid.SortMode = GameSummariesSortMode.Alphabetical;
            source.StartPageGameSummariesGrid.SortDescending = false;

            source.StartPageRecentUnlocksGrid.UseCoverImages = false;
            source.StartPageRecentUnlocksGrid.ShowColumnHeaders = false;
            source.StartPageRecentUnlocksGrid.RowHeight = 84d;
            source.StartPageRecentUnlocksGrid.MaxRows = 12;
            source.StartPageRecentUnlocksGrid.SortMode = CompactListSortMode.Rarity;
            source.StartPageRecentUnlocksGrid.SortDescending = false;

            source.StartPagePieCharts.ShowCenterPercentage = false;
            source.StartPagePieCharts.SmallSliceMode = OverviewPieSmallSliceMode.Hide;

            var clone = source.Clone();
            var copy = new PersistedSettings();
            copy.CopyFrom(source);

            Assert.IsFalse(clone.StartPageGameSummariesGrid.UseCoverImages);
            Assert.IsFalse(clone.StartPageGameSummariesGrid.ShowGameMetadata);
            Assert.IsFalse(clone.StartPageGameSummariesGrid.ShowCompletionBorder);
            Assert.IsFalse(clone.StartPageGameSummariesGrid.ShowColumnHeaders);
            Assert.AreEqual(72d, clone.StartPageGameSummariesGrid.RowHeight);
            Assert.AreEqual(11, clone.StartPageGameSummariesGrid.MaxRows);
            Assert.AreEqual(GameSummariesSortMode.Alphabetical, clone.StartPageGameSummariesGrid.SortMode);
            Assert.IsFalse(clone.StartPageGameSummariesGrid.SortDescending);

            Assert.IsFalse(copy.StartPageRecentUnlocksGrid.UseCoverImages);
            Assert.IsFalse(copy.StartPageRecentUnlocksGrid.ShowColumnHeaders);
            Assert.AreEqual(84d, copy.StartPageRecentUnlocksGrid.RowHeight);
            Assert.AreEqual(12, copy.StartPageRecentUnlocksGrid.MaxRows);
            Assert.AreEqual(CompactListSortMode.Rarity, copy.StartPageRecentUnlocksGrid.SortMode);
            Assert.IsFalse(copy.StartPageRecentUnlocksGrid.SortDescending);

            Assert.IsFalse(clone.StartPagePieCharts.ShowCenterPercentage);
            Assert.AreEqual(OverviewPieSmallSliceMode.Hide, clone.StartPagePieCharts.SmallSliceMode);
            Assert.IsFalse(copy.StartPagePieCharts.ShowCenterPercentage);
            Assert.AreEqual(OverviewPieSmallSliceMode.Hide, copy.StartPagePieCharts.SmallSliceMode);

            Assert.AreNotSame(source.StartPageGameSummariesGrid, clone.StartPageGameSummariesGrid);
            Assert.AreNotSame(source.StartPageRecentUnlocksGrid, copy.StartPageRecentUnlocksGrid);
            Assert.AreNotSame(source.StartPagePieCharts, clone.StartPagePieCharts);
            Assert.AreNotSame(source.StartPagePieCharts, copy.StartPagePieCharts);
        }
    }
}
