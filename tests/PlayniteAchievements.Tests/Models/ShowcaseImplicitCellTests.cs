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
        public void Serialization_LeavesOutEmptyCells_AndNormalizeRestoresThem()
        {
            var (settings, page) = BlankPage();
            var widget = Place(settings, page, 1, 1);
            Assert.IsTrue(ShowcaseLayoutService.TryMerge(
                settings, page.PageId, Block(page, 3, 3).BlockId, Block(page, 3, 4).BlockId));
            ShowcaseLayoutService.Normalize(settings);
            var widgetBlockId = BlockOf(page, widget).BlockId;

            var json = JsonConvert.SerializeObject(settings);
            var storedPage = JObject.Parse(json)["Pages"]
                .First(token => (string)token["PageId"] == page.PageId);
            Assert.AreEqual(2, ((JArray)storedPage["Blocks"]).Count);

            var loaded = JsonConvert.DeserializeObject<ShowcaseSettings>(json);
            ShowcaseLayoutService.Normalize(loaded);
            var loadedPage = loaded.Pages.Single(candidate => candidate.PageId == page.PageId);
            Assert.AreEqual(page.Blocks.Count, loadedPage.Blocks.Count);
            Assert.AreEqual(widgetBlockId, loadedPage.Blocks.Single(block => block.WidgetInstanceId == widget.InstanceId).BlockId);
            CollectionAssert.AreEqual(
                page.Blocks.Select(block => block.BlockId).ToList(),
                loadedPage.Blocks.Select(block => block.BlockId).ToList());
            Assert.IsTrue(ShowcaseLayoutService.IsValidPartition(loadedPage.Blocks, loadedPage.RowCount, loadedPage.ColumnCount));
        }

        [TestMethod]
        public void Normalize_GivesEachEmptyCellItsCellId()
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
            var first = page.Blocks.Select(block => block.BlockId).ToList();
            ShowcaseLayoutService.Normalize(settings);

            CollectionAssert.AreEqual(first, page.Blocks.Select(block => block.BlockId).ToList());
            Assert.AreEqual(ShowcaseLayoutService.CellBlockId(page.PageId, 1, 0), page.Blocks[2].BlockId);
        }

        [TestMethod]
        public void SparseClone_KeepsWidgetsAndMergedEmptyBlocksOnly()
        {
            var (settings, page) = BlankPage();
            Place(settings, page, 0, 0);
            Assert.IsTrue(ShowcaseLayoutService.TryMerge(
                settings, page.PageId, Block(page, 4, 0).BlockId, Block(page, 4, 1).BlockId));

            var clone = page.Clone(includeImplicit: false);

            Assert.AreEqual(2, clone.Blocks.Count);
            Assert.AreEqual(page.Blocks.Count, page.Clone().Blocks.Count);
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
            page.Blocks.Single(block => block.Row == row && block.Column == column);

        private static ShowcaseBlockSettings BlockOf(
            ShowcasePageSettings page,
            ShowcaseWidgetInstanceSettings widget) =>
            page.Blocks.Single(block => block.WidgetInstanceId == widget.InstanceId);
    }
}
