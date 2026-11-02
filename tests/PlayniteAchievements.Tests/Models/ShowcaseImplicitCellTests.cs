using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Showcase;

namespace PlayniteAchievements.Tests.Models
{
    [TestClass]
    public class ShowcaseImplicitCellTests
    {
        [TestMethod]
        public void BlankPage_StoresNoBlocks_AndResolvesEveryCell()
        {
            var (_, page) = BlankPage();

            Assert.AreEqual(0, page.Blocks.Count);
            var cell = ShowcaseLayoutService.ResolveCell(page, 2, 3);
            Assert.IsTrue(cell.IsImplicit);
            Assert.AreEqual(ShowcaseLayoutService.CellBlockId(page.PageId, 2, 3), cell.BlockId);
            Assert.AreEqual(cell.BlockId, ShowcaseLayoutService.ResolveBlock(page, cell.BlockId).BlockId);
            Assert.IsNull(ShowcaseLayoutService.ResolveCell(page, 5, 0));
            Assert.AreEqual(25, ShowcaseLayoutService.DenseBlocks(page).Count);
        }

        [TestMethod]
        public void Normalize_DropsStoredEmptyCells()
        {
            var settings = new ShowcaseSettings();
            var page = new ShowcasePageSettings { RowCount = 2, ColumnCount = 2 };
            // A page saved before empty cells were left out: every cell stored under a random id.
            for (var row = 0; row < 2; row++)
            {
                for (var column = 0; column < 2; column++)
                {
                    page.Blocks.Add(new ShowcaseBlockSettings { Row = row, Column = column });
                }
            }

            settings.Pages.Add(page);
            ShowcaseLayoutService.Normalize(settings);

            Assert.AreEqual(0, page.Blocks.Count);
            Assert.AreEqual(4, ShowcaseLayoutService.DenseBlocks(page).Count);
        }

        [TestMethod]
        public void PlacingAWidget_StoresOnlyThatCell_UnderItsCellId()
        {
            var (settings, page) = BlankPage();
            var cellId = ShowcaseLayoutService.CellBlockId(page.PageId, 1, 1);
            var widget = ShowcaseLayoutService.CreateWidget(settings, ShowcaseWidgetKind.Pie);

            Assert.IsTrue(ShowcaseLayoutService.PlaceWidget(settings, page.PageId, cellId, widget.InstanceId));
            ShowcaseLayoutService.Normalize(settings);

            Assert.AreEqual(1, page.Blocks.Count);
            Assert.AreEqual(cellId, page.Blocks[0].BlockId);
            Assert.AreEqual(widget.InstanceId, page.Blocks[0].WidgetInstanceId);
        }

        [TestMethod]
        public void MergingImplicitCells_StoresOneSpanningBlock()
        {
            var (settings, page) = BlankPage();

            Assert.IsTrue(ShowcaseLayoutService.TryMerge(
                settings,
                page.PageId,
                ShowcaseLayoutService.CellBlockId(page.PageId, 3, 3),
                ShowcaseLayoutService.CellBlockId(page.PageId, 3, 4)));
            ShowcaseLayoutService.Normalize(settings);

            Assert.AreEqual(1, page.Blocks.Count);
            Assert.AreEqual(2, page.Blocks[0].ColumnSpan);
        }

        [TestMethod]
        public void SplittingAMergedEmptyBlock_ReturnsItsCellsToImplicit()
        {
            var (settings, page) = BlankPage();
            Assert.IsTrue(ShowcaseLayoutService.TryMerge(
                settings,
                page.PageId,
                ShowcaseLayoutService.CellBlockId(page.PageId, 0, 0),
                ShowcaseLayoutService.CellBlockId(page.PageId, 0, 1)));

            Assert.IsTrue(ShowcaseLayoutService.TrySplit(settings, page.PageId, page.Blocks[0].BlockId, vertical: true, 1));
            ShowcaseLayoutService.Normalize(settings);

            Assert.AreEqual(0, page.Blocks.Count);
        }

        [TestMethod]
        public void Normalize_RenamesAStoredBlockThatCarriesAnotherCellsId()
        {
            var (settings, page) = BlankPage();
            var cellId = ShowcaseLayoutService.CellBlockId(page.PageId, 0, 0);
            var widget = ShowcaseLayoutService.CreateWidget(settings, ShowcaseWidgetKind.Pie);
            Assert.IsTrue(ShowcaseLayoutService.PlaceWidget(settings, page.PageId, cellId, widget.InstanceId));

            // A row inserted above moves the widget to (1, 0), freeing (0, 0) and its id.
            Assert.IsTrue(ShowcaseLayoutService.TryInsertTrack(settings, page.PageId, vertical: false, 0));
            ShowcaseLayoutService.Normalize(settings);

            Assert.AreNotEqual(cellId, page.Blocks.Single().BlockId);
            Assert.IsTrue(ShowcaseLayoutService.ResolveBlock(page, cellId).IsImplicit);
        }

        [TestMethod]
        public void Serialization_WritesOnlyStoredBlocks()
        {
            var (settings, page) = BlankPage();
            var widget = ShowcaseLayoutService.CreateWidget(settings, ShowcaseWidgetKind.Pie);
            Assert.IsTrue(ShowcaseLayoutService.PlaceWidget(
                settings, page.PageId, ShowcaseLayoutService.CellBlockId(page.PageId, 1, 1), widget.InstanceId));
            ShowcaseLayoutService.Normalize(settings);

            var json = JsonConvert.SerializeObject(settings);
            var storedPage = JObject.Parse(json)["Pages"]
                .First(token => (string)token["PageId"] == page.PageId);
            Assert.AreEqual(1, ((JArray)storedPage["Blocks"]).Count);
            Assert.IsNull(storedPage["Blocks"][0]["IsImplicit"]);
        }

        [TestMethod]
        public void TransferTrackWeight_StopsAtTheMinimumWeight()
        {
            var result = ShowcaseLayoutService.TransferTrackWeight(new[] { 1d, 1d, 1d }, 0, -0.9, minWeight: 0.25);

            Assert.AreEqual(0.25, result[0], 1e-9);
            Assert.AreEqual(1.75, result[1], 1e-9);
        }

        [TestMethod]
        public void TransferTrackWeight_LetsATrackBelowTheMinimumGrowButNotShrink()
        {
            var weights = new[] { 0.1d, 1d, 1d };

            var shrunk = ShowcaseLayoutService.TransferTrackWeight(weights, 0, -0.05, minWeight: 0.25);
            var grown = ShowcaseLayoutService.TransferTrackWeight(weights, 0, 0.05, minWeight: 0.25);

            Assert.AreEqual(0.1, shrunk[0], 1e-9);
            Assert.AreEqual(0.15, grown[0], 1e-9);
        }

        private static (ShowcaseSettings, ShowcasePageSettings) BlankPage()
        {
            var settings = new ShowcaseSettings();
            var page = ShowcaseLayoutService.AddPage(settings, ShowcasePageTemplate.Blank);
            ShowcaseLayoutService.Normalize(settings);
            return (settings, page);
        }
    }
}
