using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Showcase;

namespace PlayniteAchievements.Tests.Models
{
    [TestClass]
    public class ShowcasePagePortableStoreTests
    {
        private string _tempDirectory;

        [TestInitialize]
        public void Initialize()
        {
            _tempDirectory = Path.Combine(
                Path.GetTempPath(),
                "PlayniteAchievements.Tests",
                nameof(ShowcasePagePortableStoreTests),
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDirectory);
        }

        [TestCleanup]
        public void Cleanup()
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
        }

        [TestMethod]
        public void RoundTrip_ReproducesLayoutUnderFreshIds()
        {
            var (settings, grids, page, recent, games) = BuildSource();
            var path = Path.Combine(_tempDirectory, "page.pashowcase");

            ShowcasePagePortableStore.Write(
                path,
                ShowcasePagePortableStore.BuildPortable(settings, grids, page.PageId));
            var portable = ShowcasePagePortableStore.Read(path);
            var imported = ShowcasePagePortableStore.ApplyPortable(settings, grids, portable, page.PageId);

            Assert.AreEqual(2, settings.Pages.Count);
            Assert.AreSame(imported, settings.Pages[1]);
            Assert.AreEqual(imported.PageId, settings.LastSelectedPageId);
            Assert.AreNotEqual(page.PageId, imported.PageId);
            Assert.AreEqual("Mine 2", imported.Name);
            Assert.AreEqual(page.GridSize, imported.GridSize);
            CollectionAssert.AreEqual(page.RowWeights, imported.RowWeights);
            Assert.AreEqual(page.Blocks.Count, imported.Blocks.Count);

            var originalBlockIds = new HashSet<string>(page.Blocks.Select(block => block.BlockId));
            var originalWidgetIds = new HashSet<string>(page.Blocks
                .Select(block => block.WidgetInstanceId)
                .Where(id => id != null));
            var sourceBlocks = page.Blocks.OrderBy(block => block.Row).ThenBy(block => block.Column).ToList();
            var copyBlocks = imported.Blocks.OrderBy(block => block.Row).ThenBy(block => block.Column).ToList();
            for (var i = 0; i < sourceBlocks.Count; i++)
            {
                var source = sourceBlocks[i];
                var copy = copyBlocks[i];
                Assert.AreEqual(source.Row, copy.Row);
                Assert.AreEqual(source.Column, copy.Column);
                Assert.AreEqual(source.RowSpan, copy.RowSpan);
                Assert.AreEqual(source.ColumnSpan, copy.ColumnSpan);
                Assert.IsFalse(originalBlockIds.Contains(copy.BlockId));
                if (source.WidgetInstanceId == null)
                {
                    Assert.IsNull(copy.WidgetInstanceId);
                    continue;
                }

                Assert.IsFalse(originalWidgetIds.Contains(copy.WidgetInstanceId));

                var sourceWidget = settings.WidgetInstances.Single(w => w.InstanceId == source.WidgetInstanceId);
                var copyWidget = settings.WidgetInstances.Single(w => w.InstanceId == copy.WidgetInstanceId);
                Assert.AreEqual(sourceWidget.Kind, copyWidget.Kind);
                Assert.AreEqual(sourceWidget.CustomTitle, copyWidget.CustomTitle);
            }

            var importedRecent = WidgetOf(settings, imported, ShowcaseWidgetKind.RecentAchievements);
            Assert.AreEqual(
                recent.GetOption("MaxPerGame", 0),
                importedRecent.GetOption("MaxPerGame", 0));
        }

        [TestMethod]
        public void Export_StripsPinsControlBarStateAndProfileData()
        {
            var (settings, grids, page, recent, _) = BuildSource();
            settings.Profile.DisplayName = "Secret Name";
            settings.GamePinCollections.Add(new PinnedGameCollection
            {
                CollectionId = "mine",
                Name = "Mine",
                GameIds = new List<Guid> { Guid.NewGuid() }
            });
            var path = Path.Combine(_tempDirectory, "page.pashowcase");

            var portable = ShowcasePagePortableStore.BuildPortable(settings, grids, page.PageId);
            ShowcasePagePortableStore.Write(path, portable);

            var exportedRecent = portable.Widgets.Single(w => w.Kind == ShowcaseWidgetKind.RecentAchievements);
            Assert.IsNull(ShowcaseWidgetOptions.GetPinCollectionId(exportedRecent));
            Assert.IsFalse(exportedRecent.Options.Keys.Any(key => key.StartsWith("ControlBar.")));
            Assert.AreEqual("custom-pins", ShowcaseWidgetOptions.GetPinCollectionId(recent), "source untouched");

            var json = ReadManifest(path);
            StringAssert.DoesNotMatch(json, new System.Text.RegularExpressions.Regex(@"Secret Name|custom-pins|zelda|ControlBar\.|GameIds|""Profile"""));
        }

        [TestMethod]
        public void Import_ReseedsDefaultPinCollections()
        {
            var (settings, grids, page, _, _) = BuildSource();
            var portable = ShowcasePagePortableStore.BuildPortable(settings, grids, page.PageId);

            var imported = ShowcasePagePortableStore.ApplyPortable(settings, grids, portable, page.PageId);

            Assert.AreEqual(
                settings.DefaultAchievementPinCollectionId,
                ShowcaseWidgetOptions.GetPinCollectionId(
                    WidgetOf(settings, imported, ShowcaseWidgetKind.RecentAchievements)));
            Assert.AreEqual(
                settings.DefaultGamePinCollectionId,
                ShowcaseWidgetOptions.GetPinCollectionId(
                    WidgetOf(settings, imported, ShowcaseWidgetKind.GameSummaries)));
        }

        [TestMethod]
        public void Import_RekeysGridSurfacesToTheNewInstances()
        {
            var (settings, grids, page, _, _) = BuildSource();
            var portable = ShowcasePagePortableStore.BuildPortable(settings, grids, page.PageId);

            var imported = ShowcasePagePortableStore.ApplyPortable(settings, grids, portable, page.PageId);
            ShowcaseGridSurfaces.PruneOrphaned(grids, settings);

            var recentKey = ShowcaseGridSurfaces.ResolveWidgetSurface(
                ShowcaseWidgetKind.RecentAchievements,
                WidgetOf(settings, imported, ShowcaseWidgetKind.RecentAchievements).InstanceId);
            var gamesKey = ShowcaseGridSurfaces.ResolveWidgetSurface(
                ShowcaseWidgetKind.GameSummaries,
                WidgetOf(settings, imported, ShowcaseWidgetKind.GameSummaries).InstanceId);
            Assert.IsTrue(grids.Achievement.ContainsKey(recentKey));
            Assert.IsTrue(grids.Achievement[recentKey].UseCoverImages);
            Assert.IsTrue(grids.GameSummaries.ContainsKey(gamesKey));
            Assert.IsFalse(grids.GameSummaries[gamesKey].UseCoverImages);
        }

        [TestMethod]
        public void Import_DropsUnknownWidgetKindsAndLeavesTheirBlocksEmpty()
        {
            var (settings, grids, page, _, _) = BuildSource();
            var portable = ShowcasePagePortableStore.BuildPortable(settings, grids, page.PageId);
            portable.Widgets[0].Kind = (ShowcaseWidgetKind)6;
            var retiredId = portable.Widgets[0].InstanceId;
            var blockIndex = portable.Page.Blocks.FindIndex(block => block.WidgetInstanceId == retiredId);

            var imported = ShowcasePagePortableStore.ApplyPortable(settings, grids, portable, page.PageId);

            Assert.IsNull(imported.Blocks[blockIndex].WidgetInstanceId);
            Assert.AreEqual(
                page.Blocks.Count(block => block.WidgetInstanceId != null) - 1,
                imported.Blocks.Count(block => block.WidgetInstanceId != null));
        }

        [TestMethod]
        public void Read_RejectsForeignAndNewerFiles()
        {
            var (settings, grids, page, _, _) = BuildSource();
            var portable = ShowcasePagePortableStore.BuildPortable(settings, grids, page.PageId);

            var newer = Path.Combine(_tempDirectory, "newer.pashowcase");
            portable.Version = ShowcasePagePortableStore.CurrentVersion + 1;
            WriteRawManifest(newer, Newtonsoft.Json.JsonConvert.SerializeObject(portable));
            Assert.ThrowsException<InvalidOperationException>(() => ShowcasePagePortableStore.Read(newer));

            var foreign = Path.Combine(_tempDirectory, "foreign.pashowcase");
            WriteRawManifest(foreign, "{\"Kind\":\"PlayniteAchievements.NotificationStyle\",\"Version\":1}");
            Assert.ThrowsException<InvalidOperationException>(() => ShowcasePagePortableStore.Read(foreign));

            var noManifest = Path.Combine(_tempDirectory, "empty.pashowcase");
            using (ZipFile.Open(noManifest, ZipArchiveMode.Create))
            {
            }

            Assert.ThrowsException<InvalidOperationException>(() => ShowcasePagePortableStore.Read(noManifest));

            var notZip = Path.Combine(_tempDirectory, "plain.pashowcase");
            File.WriteAllText(notZip, "{}");
            Assert.ThrowsException<InvalidOperationException>(() => ShowcasePagePortableStore.Read(notZip));
        }

        [TestMethod]
        public void DuplicatePage_WithCatalog_CopiesGridSurfaces()
        {
            var (settings, grids, page, _, _) = BuildSource();

            var duplicate = ShowcaseLayoutService.DuplicatePage(settings, page.PageId, gridOptions: grids);

            var key = ShowcaseGridSurfaces.ResolveWidgetSurface(
                ShowcaseWidgetKind.RecentAchievements,
                WidgetOf(settings, duplicate, ShowcaseWidgetKind.RecentAchievements).InstanceId);
            Assert.IsTrue(grids.Achievement.ContainsKey(key));
            Assert.IsTrue(grids.Achievement[key].UseCoverImages);
        }

        [TestMethod]
        public void SuggestFileName_RemovesInvalidCharacters()
        {
            Assert.AreEqual("ab.pashowcase", ShowcasePagePortableStore.SuggestFileName("a/b?"));
            Assert.AreEqual("Showcase.pashowcase", ShowcasePagePortableStore.SuggestFileName("  "));
            Assert.AreEqual(
                @"C:\x\page.pashowcase",
                ShowcasePagePortableStore.NormalizeExportPath(@"C:\x\page.pashowcase.zip"));
        }

        private static (ShowcaseSettings, GridOptionsCatalog, ShowcasePageSettings,
            ShowcaseWidgetInstanceSettings, ShowcaseWidgetInstanceSettings) BuildSource()
        {
            var settings = new ShowcaseSettings();
            var page = ShowcaseLayoutService.AddPage(settings, ShowcasePageTemplate.Blank, "Mine");
            page.RowWeights = Enumerable.Repeat(1.5, page.GridSize).ToList();

            var recent = ShowcaseLayoutService.CreateWidget(settings, ShowcaseWidgetKind.RecentAchievements);
            recent.CustomTitle = "Latest";
            recent.SetOption("MaxPerGame", 7);
            recent.Options["ControlBar.Achievements"] = "{\"SearchText\":\"zelda\"}";
            ShowcaseWidgetOptions.SetPinCollectionId(recent, "custom-pins");
            var games = ShowcaseLayoutService.CreateWidget(settings, ShowcaseWidgetKind.GameSummaries);
            var pie = ShowcaseLayoutService.CreateWidget(settings, ShowcaseWidgetKind.Pie);

            var blocks = page.Blocks.OrderBy(block => block.Row).ThenBy(block => block.Column).ToList();
            Assert.IsTrue(ShowcaseLayoutService.PlaceWidget(settings, page.PageId, blocks[0].BlockId, recent.InstanceId));
            Assert.IsTrue(ShowcaseLayoutService.PlaceWidget(settings, page.PageId, blocks[1].BlockId, games.InstanceId));
            Assert.IsTrue(ShowcaseLayoutService.PlaceWidget(settings, page.PageId, blocks[2].BlockId, pie.InstanceId));

            var grids = new GridOptionsCatalog();
            grids.GetAchievement(ShowcaseGridSurfaces.ResolveWidgetSurface(recent.Kind, recent.InstanceId))
                .UseCoverImages = true;
            grids.GetGameSummaries(ShowcaseGridSurfaces.ResolveWidgetSurface(games.Kind, games.InstanceId))
                .UseCoverImages = false;

            // Drop the pages AddPage's default settings may have seeded, keeping only this one.
            settings.Pages.RemoveAll(candidate => candidate != page);
            return (settings, grids, page, recent, games);
        }

        private static ShowcaseWidgetInstanceSettings WidgetOf(
            ShowcaseSettings settings,
            ShowcasePageSettings page,
            ShowcaseWidgetKind kind)
        {
            return page.Blocks
                .Where(block => block.WidgetInstanceId != null)
                .Select(block => settings.WidgetInstances.Single(w => w.InstanceId == block.WidgetInstanceId))
                .First(widget => widget.Kind == kind);
        }

        private static string ReadManifest(string path)
        {
            using (var archive = ZipFile.OpenRead(path))
            using (var reader = new StreamReader(
                archive.GetEntry(ShowcasePagePortableStore.ManifestEntryName).Open()))
            {
                return reader.ReadToEnd();
            }
        }

        private static void WriteRawManifest(string path, string json)
        {
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(
                archive.CreateEntry(ShowcasePagePortableStore.ManifestEntryName).Open()))
            {
                writer.Write(json);
            }
        }
    }
}
