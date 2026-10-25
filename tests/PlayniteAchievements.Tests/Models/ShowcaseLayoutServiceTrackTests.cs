using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Showcase;

namespace PlayniteAchievements.Tests.Models
{
    [TestClass]
    public class ShowcaseLayoutServiceTrackTests
    {
        [TestMethod]
        public void Normalize_MigratesLegacyGridSizeToBothCounts()
        {
            var settings = new ShowcaseSettings
            {
                Pages =
                {
                    new ShowcasePageSettings { Name = "Legacy", GridSize = 3 },
                    new ShowcasePageSettings { Name = "Unset" }
                }
            };

            ShowcaseLayoutService.Normalize(settings);

            var legacy = settings.Pages[0];
            Assert.AreEqual(3, legacy.RowCount);
            Assert.AreEqual(3, legacy.ColumnCount);
            Assert.IsNull(legacy.GridSize);
            Assert.AreEqual(9, CellBlockCount(legacy));
            Assert.AreEqual(ShowcaseLayoutService.DefaultTrackCount, settings.Pages[1].RowCount);
            Assert.AreEqual(ShowcaseLayoutService.DefaultTrackCount, settings.Pages[1].ColumnCount);
        }

        [TestMethod]
        public void InsertTrack_AtAnInteriorLine_StretchesTheBlockThatCrossesIt()
        {
            var (settings, page) = BlankPage();
            var widget = Place(settings, page, 0, 1);
            Assert.IsTrue(ShowcaseLayoutService.TryMerge(
                settings, page.PageId, Block(page, 0, 1).BlockId, Block(page, 0, 2).BlockId));
            var before = CellBlockCount(page);

            Assert.IsTrue(ShowcaseLayoutService.TryInsertTrack(settings, page.PageId, vertical: true, 2));

            var stretched = BlockOf(page, widget);
            Assert.AreEqual(1, stretched.Column);
            Assert.AreEqual(3, stretched.ColumnSpan);
            Assert.AreEqual(6, page.ColumnCount);
            Assert.AreEqual(before + 4, CellBlockCount(page));
            AssertValid(page);
        }

        [TestMethod]
        public void InsertTrack_AtTheEdges_ShiftsOrAppendsAndCopiesTheNeighbourWeight()
        {
            var (settings, page) = BlankPage();
            var widget = Place(settings, page, 0, 0);
            page.ColumnWeights = new List<double> { 2, 1, 1, 1, 0.5 };

            Assert.IsTrue(ShowcaseLayoutService.TryInsertTrack(settings, page.PageId, vertical: true, 0));
            Assert.AreEqual(1, BlockOf(page, widget).Column);
            Assert.IsTrue(ShowcaseLayoutService.TryInsertTrack(settings, page.PageId, vertical: true, 6));

            CollectionAssert.AreEqual(new[] { 2d, 2, 1, 1, 1, 0.5, 0.5 }, page.ColumnWeights);
            Assert.AreEqual(7, page.ColumnCount);
            Assert.IsNull(page.RowWeights);
            AssertValid(page);
        }

        [TestMethod]
        public void InsertTrack_IsRefusedOnceATrackWouldFallBelowTheMinimumSize()
        {
            // 100 px of equal rows: 8 rows are 12.5 px each, a 9th would make them 11.1 px.
            var (settings, page) = BlankPage();
            while (ShowcaseLayoutService.TryInsertTrack(settings, page.PageId, vertical: false, 0, extentPixels: 100))
            {
            }

            Assert.AreEqual(8, page.RowCount);
            Assert.IsFalse(ShowcaseLayoutService.CanInsertTrack(page, vertical: false, 8, 100));
            Assert.IsTrue(ShowcaseLayoutService.CanInsertTrack(page, vertical: false, 8, 0));
            AssertValid(page);
        }

        [TestMethod]
        public void InsertTrack_MeasuresTheMinimumAgainstTheNarrowestTrack()
        {
            var (settings, page) = BlankPage();
            page.ColumnWeights = new List<double> { 0.5, 1, 1, 1, 1 };

            // 4.5 stars over 120 px: the half-weight column is 13.3 px, and one more full
            // column would leave it 10.9 px.
            Assert.IsFalse(ShowcaseLayoutService.CanInsertTrack(page, vertical: true, 5, 120));
            Assert.IsTrue(ShowcaseLayoutService.CanInsertTrack(page, vertical: true, 5, 160));
        }

        [TestMethod]
        public void Normalize_BoundsAStoredTrackCount()
        {
            var settings = new ShowcaseSettings
            {
                Pages = { new ShowcasePageSettings { RowCount = 100000, ColumnCount = 1 } }
            };

            ShowcaseLayoutService.Normalize(settings);

            Assert.AreEqual(ShowcaseLayoutService.MaxStoredTrackCount, settings.Pages[0].RowCount);
            Assert.AreEqual(ShowcaseLayoutService.MaxStoredTrackCount, CellBlockCount(settings.Pages[0]));
        }

        [TestMethod]
        public void DeleteTrack_OfAnEmptyRow_LosesNothingAndDropsItsWeight()
        {
            var (settings, page) = BlankPage();
            page.RowWeights = new List<double> { 1, 2, 3, 1, 1 };

            var deletion = ShowcaseLayoutService.PreviewDeleteTrack(settings, page.PageId, vertical: false, 2);
            Assert.IsTrue(deletion.IsAllowed);
            Assert.AreEqual(0, deletion.LostWidgetIds.Count);
            Assert.IsTrue(ShowcaseLayoutService.TryDeleteTrack(settings, page.PageId, vertical: false, 2));

            Assert.AreEqual(4, page.RowCount);
            CollectionAssert.AreEqual(new[] { 1d, 2, 1, 1 }, page.RowWeights);
            Assert.AreEqual(20, CellBlockCount(page));
            AssertValid(page);
        }

        [TestMethod]
        public void DeleteTrack_StepsAConfinedWidgetIntoThePrecedingTrack()
        {
            var (settings, page) = BlankPage();
            var widget = Place(settings, page, 2, 1);

            var deletion = ShowcaseLayoutService.PreviewDeleteTrack(settings, page.PageId, vertical: false, 2);
            Assert.AreEqual(1, deletion.Shifts.Count);
            Assert.AreEqual(-1, deletion.Shifts[0].Direction);
            Assert.AreEqual(0, deletion.LostWidgetIds.Count);
            Assert.IsTrue(ShowcaseLayoutService.TryDeleteTrack(settings, page.PageId, vertical: false, 2));

            var block = BlockOf(page, widget);
            Assert.AreEqual(1, block.Row);
            Assert.AreEqual(1, block.Column);
            Assert.IsTrue(settings.WidgetInstances.Contains(widget));
            AssertValid(page);
        }

        [TestMethod]
        public void DeleteTrack_StepsIntoTheFollowingTrackWhenThePrecedingOneIsTaken()
        {
            var (settings, page) = BlankPage();
            var above = Place(settings, page, 1, 1);
            var widget = Place(settings, page, 2, 1);

            Assert.IsTrue(ShowcaseLayoutService.TryDeleteTrack(settings, page.PageId, vertical: false, 2));

            Assert.AreEqual(1, BlockOf(page, above).Row);
            Assert.AreEqual(2, BlockOf(page, widget).Row);
            Assert.IsTrue(settings.WidgetInstances.Contains(widget));
            AssertValid(page);
        }

        [TestMethod]
        public void DeleteTrack_MovesWhatFitsAndDeletesOnlyTheWidgetsThatCannotMove()
        {
            var (settings, page) = BlankPage();
            Place(settings, page, 1, 1);
            var trapped = Place(settings, page, 2, 1);
            Place(settings, page, 3, 1);
            var free = Place(settings, page, 2, 3);

            var deletion = ShowcaseLayoutService.PreviewDeleteTrack(settings, page.PageId, vertical: false, 2);
            CollectionAssert.AreEqual(new[] { trapped.InstanceId }, deletion.LostWidgetIds.ToList());
            Assert.AreEqual(1, deletion.Shifts.Count);
            Assert.IsTrue(ShowcaseLayoutService.TryDeleteTrack(settings, page.PageId, vertical: false, 2));

            Assert.IsFalse(settings.WidgetInstances.Contains(trapped));
            Assert.IsFalse(page.Blocks.Any(block => block.WidgetInstanceId == trapped.InstanceId));
            Assert.AreEqual(1, BlockOf(page, free).Row);
            AssertValid(page);
        }

        [TestMethod]
        public void DeleteTrack_ShrinksAWidgetThatSpansIt()
        {
            var (settings, page) = BlankPage();
            var widget = Place(settings, page, 1, 0);
            Assert.IsTrue(ShowcaseLayoutService.TryMerge(
                settings, page.PageId, Block(page, 1, 0).BlockId, Block(page, 2, 0).BlockId));

            var deletion = ShowcaseLayoutService.PreviewDeleteTrack(settings, page.PageId, vertical: false, 1);
            Assert.AreEqual(0, deletion.Shifts.Count);
            Assert.AreEqual(0, deletion.LostWidgetIds.Count);
            Assert.IsTrue(ShowcaseLayoutService.TryDeleteTrack(settings, page.PageId, vertical: false, 1));

            var block = BlockOf(page, widget);
            Assert.AreEqual(1, block.Row);
            Assert.AreEqual(1, block.RowSpan);
            AssertValid(page);
        }

        [TestMethod]
        public void DeleteTrack_OfAColumn_StepsLeft()
        {
            var (settings, page) = BlankPage();
            var widget = Place(settings, page, 3, 4);

            Assert.IsTrue(ShowcaseLayoutService.TryDeleteTrack(settings, page.PageId, vertical: true, 4));

            var block = BlockOf(page, widget);
            Assert.AreEqual(3, block.Row);
            Assert.AreEqual(3, block.Column);
            Assert.AreEqual(4, page.ColumnCount);
            AssertValid(page);
        }

        [TestMethod]
        public void DeleteTrack_IsRefusedAtTheMinimum()
        {
            var (settings, page) = BlankPage();
            while (page.ColumnCount > ShowcaseLayoutService.MinTrackCount)
            {
                Assert.IsTrue(ShowcaseLayoutService.TryDeleteTrack(settings, page.PageId, vertical: true, 0));
            }

            Assert.IsFalse(ShowcaseLayoutService.PreviewDeleteTrack(settings, page.PageId, vertical: true, 0).IsAllowed);
            Assert.IsFalse(ShowcaseLayoutService.TryDeleteTrack(settings, page.PageId, vertical: true, 0));
            Assert.AreEqual(1, page.ColumnCount);
            AssertValid(page);
        }

        [TestMethod]
        public void PreviewDeleteTrack_DoesNotMutate()
        {
            var (settings, page) = BlankPage();
            Place(settings, page, 2, 1);
            var before = Describe(page);

            ShowcaseLayoutService.PreviewDeleteTrack(settings, page.PageId, vertical: false, 2);

            Assert.AreEqual(before, Describe(page));
            Assert.AreEqual(5, page.RowCount);
        }

        private static (ShowcaseSettings, ShowcasePageSettings) BlankPage()
        {
            var settings = new ShowcaseSettings();
            var page = ShowcaseLayoutService.AddPage(settings, ShowcasePageTemplate.Blank);
            return (settings, page);
        }

        private static ShowcaseWidgetInstanceSettings Place(
            ShowcaseSettings settings,
            ShowcasePageSettings page,
            int row,
            int column)
        {
            var widget = ShowcaseLayoutService.CreateWidget(settings, ShowcaseWidgetKind.Pie);
            Assert.IsTrue(ShowcaseLayoutService.PlaceWidget(
                settings, page.PageId, Block(page, row, column).BlockId, widget.InstanceId));
            return widget;
        }

        private static ShowcaseBlockSettings Block(ShowcasePageSettings page, int row, int column) =>
            ShowcaseLayoutService.ResolveCell(page, row, column);

        private static int CellBlockCount(ShowcasePageSettings page) =>
            ShowcaseLayoutService.DenseBlocks(page).Count;

        private static ShowcaseBlockSettings BlockOf(
            ShowcasePageSettings page,
            ShowcaseWidgetInstanceSettings widget) =>
            page.Blocks.Single(block => block.WidgetInstanceId == widget.InstanceId);

        private static void AssertValid(ShowcasePageSettings page) =>
            Assert.IsTrue(ShowcaseLayoutService.IsValidPartition(page.Blocks, page.RowCount, page.ColumnCount));

        private static string Describe(ShowcasePageSettings page) =>
            string.Join(";", page.Blocks.Select(block =>
                $"{block.BlockId}:{block.Row},{block.Column},{block.RowSpan},{block.ColumnSpan},{block.WidgetInstanceId}"));
    }
}
