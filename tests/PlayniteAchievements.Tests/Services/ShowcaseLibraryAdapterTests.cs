using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Library;
using PlayniteAchievements.Services.Showcase;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PlayniteAchievements.Services.Tests
{
    [TestClass]
    [DoNotParallelize]
    public class ShowcaseLibraryAdapterTests
    {
        private string _root;
        private LibraryStore _library;
        private LibraryApplyService _apply;
        private PersistedSettings _live;
        private PersistedSettings _snapshot;
        private ShowcaseLibraryTargets _targets;
        private LibraryUpdateService _updates;
        private DirectoryPackageFolder _folder;

        // The author's side: the page the package is exported from.
        private ShowcaseSettings _author;
        private GridOptionsCatalog _authorGrids;
        private ShowcasePageSettings _authorPage;
        private ShowcaseWidgetInstanceSettings _authorRecent;
        private ShowcaseWidgetInstanceSettings _authorPie;

        [TestInitialize]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "PlayAchShowcaseLibrary_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _library = new LibraryStore(_root);
            _apply = new LibraryApplyService(_library, new LibraryBaselineStore(_library.LibraryDirectory));
            _live = new PersistedSettings();
            _snapshot = new PersistedSettings();
            _targets = new ShowcaseLibraryTargets(() => _live, path => path);
            _updates = new LibraryUpdateService(
                _apply,
                new GameLinkStore(_library.LibraryDirectory),
                Enumerable.Empty<ISettingsLibraryAdapter>(),
                () => _live,
                update => update(_live),
                update => update(_live),
                new ILibraryTargetResolver[] { _targets });
            _folder = new DirectoryPackageFolder(
                LibraryStore.PresetDirectory(_root, LibraryItemKind.ShowcasePage),
                ShowcasePagePortableStore.PackageFileExtension);

            _author = new ShowcaseSettings();
            _authorPage = ShowcaseLayoutService.AddPage(_author, ShowcasePageTemplate.Blank, "Trophies");
            _author.Pages.RemoveAll(page => page != _authorPage);
            _authorRecent = ShowcaseLayoutService.CreateWidget(_author, ShowcaseWidgetKind.RecentAchievements);
            _authorRecent.CustomTitle = "Latest";
            _authorRecent.SetOption("MaxPerGame", 3);
            _authorPie = ShowcaseLayoutService.CreateWidget(_author, ShowcaseWidgetKind.Pie);
            _authorPie.CustomTitle = "Rarity";
            var blocks = AuthorBlocks();
            Assert.IsTrue(ShowcaseLayoutService.PlaceWidget(_author, _authorPage.PageId, blocks[0].BlockId, _authorRecent.InstanceId));
            Assert.IsTrue(ShowcaseLayoutService.PlaceWidget(_author, _authorPage.PageId, blocks[1].BlockId, _authorPie.InstanceId));
            _authorGrids = new GridOptionsCatalog();
            _authorGrids.GetAchievement(ShowcaseGridSurfaces.ResolveWidgetSurface(_authorRecent.Kind, _authorRecent.InstanceId)).UseCoverImages = true;
        }

        [TestCleanup]
        public void TearDown()
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch
            {
            }
        }

        [TestMethod]
        public void Install_ThenUntouched_IsNotEdited_AndEditingATitleIs()
        {
            var page = Install("1.0.0");
            var adapter = _targets.AdapterFor(page.PageId);

            var state = _apply.GetSettingsState(adapter, _live);
            Assert.IsTrue(state.IsFollowing);
            Assert.IsFalse(state.IsEdited);
            Assert.AreEqual(2, _live.GetLibraryLink(adapter.TargetKey).IdMap.Count);

            Local(page, ShowcaseWidgetKind.Pie).CustomTitle = "Mine";
            Assert.IsTrue(_apply.GetSettingsState(adapter, _live).IsEdited);
        }

        [TestMethod]
        public void Update_MergesPerWidget_KeepingTheUsersTitleAndOwnOptions()
        {
            var page = Install("1.0.0");
            var pie = Local(page, ShowcaseWidgetKind.Pie);
            var recent = Local(page, ShowcaseWidgetKind.RecentAchievements);
            pie.CustomTitle = "Mine";
            ShowcaseWidgetOptions.SetPinCollectionId(recent, "my-pins");

            _authorPie.CustomTitle = "Rarity spread";
            _authorRecent.SetOption("MaxPerGame", 9);
            _authorRecent.CustomTitle = "Newest";
            WritePackage("1.1.0");
            var report = _updates.MergeIntoTargets("ws:trophies", LibraryApplyMode.Merge);

            CollectionAssert.AreEqual(new[] { LibraryTargetKeys.Showcase(page.PageId) }, report.UpdatedTargets);
            Assert.AreEqual(1, report.KeptEdits);
            Assert.AreSame(pie, Local(page, ShowcaseWidgetKind.Pie), "the widgets keep their ids on the page");
            Assert.AreEqual("Mine", pie.CustomTitle, "the user's title stays");
            Assert.AreEqual("Newest", recent.CustomTitle);
            Assert.AreEqual(9, recent.GetOption("MaxPerGame", 0));
            Assert.AreEqual("my-pins", ShowcaseWidgetOptions.GetPinCollectionId(recent), "the user's own options stay");
            Assert.AreEqual(1, _live.Showcase.Pages.Count(candidate => candidate.Name.StartsWith("Trophies")), "no new page");
        }

        [TestMethod]
        public void Update_CarriesTheAuthorsCategoryListSettings()
        {
            var page = Install("1.0.0");
            var recent = Local(page, ShowcaseWidgetKind.RecentAchievements);
            var liveKey = CategoryKey(recent);
            Assert.IsFalse(_live.GridOptions.GetCategorySummaries(liveKey).ShowNameAboveProgress);

            _authorGrids.GetCategorySummaries(CategoryKey(_authorRecent)).ShowNameAboveProgress = true;
            WritePackage("1.1.0");
            _updates.MergeIntoTargets("ws:trophies", LibraryApplyMode.Merge);

            Assert.IsTrue(_live.GridOptions.GetCategorySummaries(liveKey).ShowNameAboveProgress);
        }

        private static string CategoryKey(ShowcaseWidgetInstanceSettings widget) =>
            ShowcaseGridSurfaces.ResolveCategorySurface(
                ShowcaseGridSurfaces.ResolveWidgetSurface(widget.Kind, widget.InstanceId));

        [TestMethod]
        public void Update_AddsANewWidget_WhenTheUserLeftTheLayoutAlone()
        {
            var page = Install("1.0.0");

            var games = ShowcaseLayoutService.CreateWidget(_author, ShowcaseWidgetKind.GameSummaries);
            Assert.IsTrue(ShowcaseLayoutService.PlaceWidget(_author, _authorPage.PageId, AuthorBlocks()[2].BlockId, games.InstanceId));
            WritePackage("1.1.0");
            _updates.MergeIntoTargets("ws:trophies", LibraryApplyMode.Merge);

            var added = Local(page, ShowcaseWidgetKind.GameSummaries);
            Assert.IsNotNull(added);
            Assert.AreNotEqual(games.InstanceId, added.InstanceId);
            Assert.AreEqual(_live.Showcase.DefaultGamePinCollectionId, ShowcaseWidgetOptions.GetPinCollectionId(added));
            var link = _live.GetLibraryLink(LibraryTargetKeys.Showcase(page.PageId));
            Assert.AreEqual(added.InstanceId, link.IdMap[games.InstanceId]);
            Assert.IsFalse(_apply.GetSettingsState(_targets.AdapterFor(page.PageId), _live).IsEdited);
        }

        [TestMethod]
        public void Update_MergesEachControlBarOnItsOwn_AndKeepsGameSelections()
        {
            var page = Install("1.0.0");
            var recent = Local(page, ShowcaseWidgetKind.RecentAchievements);
            var pie = Local(page, ShowcaseWidgetKind.Pie);
            var gameId = Guid.NewGuid().ToString("D");
            recent.Options["ControlBar.Achievements"] = "{\"SearchText\":\"mine\",\"Games\":[\"" + gameId + "\"]}";
            pie.Options["ControlBar.Achievements"] = "{\"Games\":[\"" + gameId + "\"]}";

            _authorRecent.Options["ControlBar.Achievements"] = "{\"SearchText\":\"author\"}";
            _authorRecent.SetOption("MaxPerGame", 9);
            _authorPie.Options["ControlBar.Achievements"] = "{\"Rarities\":[1]}";
            WritePackage("1.1.0");
            _updates.MergeIntoTargets("ws:trophies", LibraryApplyMode.Merge);

            var recentState = Newtonsoft.Json.Linq.JObject.Parse(recent.Options["ControlBar.Achievements"]);
            Assert.AreEqual("mine", (string)recentState["SearchText"], "the user's filters win");
            Assert.AreEqual(gameId, (string)recentState["Games"][0]);
            Assert.AreEqual(9, recent.GetOption("MaxPerGame", 0), "a changed filter does not hold back the widget's options");

            var pieState = Newtonsoft.Json.Linq.JObject.Parse(pie.Options["ControlBar.Achievements"]);
            CollectionAssert.AreEqual(new[] { 1 }, pieState["Rarities"].ToObject<int[]>(), "an untouched bar takes the update");
            Assert.AreEqual(gameId, (string)pieState["Games"][0], "game selections are this library's own");
        }

        [TestMethod]
        public void Update_KeepsAWidgetTheUserRemoved_Removed()
        {
            var page = Install("1.0.0");
            var pie = Local(page, ShowcaseWidgetKind.Pie);
            Assert.IsTrue(ShowcaseLayoutService.DeleteWidget(_live.Showcase, pie.InstanceId));

            _authorPie.CustomTitle = "Rarity spread";
            WritePackage("1.1.0");
            _updates.MergeIntoTargets("ws:trophies", LibraryApplyMode.Merge);

            Assert.IsNull(Local(page, ShowcaseWidgetKind.Pie));
            Assert.IsNotNull(Local(page, ShowcaseWidgetKind.RecentAchievements));
        }

        [TestMethod]
        public void Reset_TakesThePackageAsPublished_OnTheSameWidgets()
        {
            var page = Install("1.0.0");
            var pie = Local(page, ShowcaseWidgetKind.Pie);
            pie.CustomTitle = "Mine";

            Assert.IsTrue(_updates.Reset(LibraryTargetKeys.Showcase(page.PageId)));

            Assert.AreSame(pie, Local(page, ShowcaseWidgetKind.Pie));
            Assert.AreEqual("Rarity", pie.CustomTitle);
            Assert.IsFalse(_apply.GetSettingsState(_targets.AdapterFor(page.PageId), _live).IsEdited);
        }

        [TestMethod]
        public void Update_OfALinkWithoutBaselineOrIdMap_KeepsTheUsersOwnWidgetOptions_AndRecordsBoth()
        {
            var page = Install("1.0.0");
            var key = LibraryTargetKeys.Showcase(page.PageId);
            // A link as installs made before links kept a baseline and an id map wrote it.
            _live.SetLibraryLink(key, new LibraryLink { LibraryItemId = "ws:trophies", AppliedVersion = "1.0.0" });
            var recent = Local(page, ShowcaseWidgetKind.RecentAchievements);
            var pie = Local(page, ShowcaseWidgetKind.Pie);
            ShowcaseWidgetOptions.SetPinCollectionId(recent, "my-pins");
            recent.Options["ControlBar.Achievements"] = "{\"SearchText\":\"zelda\"}";
            pie.CustomTitle = "Mine";

            _authorRecent.SetOption("MaxPerGame", 9);
            WritePackage("1.1.0");
            var report = _updates.MergeIntoTargets("ws:trophies", LibraryApplyMode.Merge);

            CollectionAssert.AreEqual(new[] { key }, report.UpdatedTargets);
            Assert.AreSame(recent, Local(page, ShowcaseWidgetKind.RecentAchievements), "the widget keeps its id on the page");
            Assert.AreSame(pie, Local(page, ShowcaseWidgetKind.Pie));
            Assert.AreEqual("my-pins", ShowcaseWidgetOptions.GetPinCollectionId(recent));
            Assert.AreEqual("{\"SearchText\":\"zelda\"}", recent.Options["ControlBar.Achievements"]);
            Assert.AreEqual(9, recent.GetOption("MaxPerGame", 0), "the item is applied as published");
            Assert.AreEqual("Rarity", pie.CustomTitle, "without a baseline nothing tells an edit apart, so the published title is taken");

            var link = _live.GetLibraryLink(key);
            Assert.IsFalse(string.IsNullOrEmpty(link.BaselineFile));
            Assert.AreEqual(recent.InstanceId, link.IdMap[_authorRecent.InstanceId]);
            Assert.AreEqual(pie.InstanceId, link.IdMap[_authorPie.InstanceId]);
            Assert.IsFalse(_apply.GetSettingsState(_targets.AdapterFor(page.PageId), _live).IsEdited);
        }

        [TestMethod]
        public void Update_KeepsAPictureTheUserSwappedOnAnImageWidget_AndNeverMergesItsPathAsAnOption()
        {
            var authorPicture = Path.Combine(_root, "author-1.png");
            File.WriteAllBytes(authorPicture, new byte[] { 1, 1, 1 });
            var authorImage = ShowcaseLayoutService.CreateWidget(_author, ShowcaseWidgetKind.Image);
            ShowcaseWidgetOptions.SetImagePath(authorImage, authorPicture);
            Assert.IsTrue(ShowcaseLayoutService.PlaceWidget(_author, _authorPage.PageId, AuthorBlocks()[2].BlockId, authorImage.InstanceId));
            var page = Install("1.0.0");
            var image = Local(page, ShowcaseWidgetKind.Image);
            Assert.IsNotNull(ShowcaseWidgetOptions.GetImagePath(image), "the installed widget got the bundled picture");

            var userPicture = Path.Combine(_root, "mine.png");
            File.WriteAllBytes(userPicture, new byte[] { 2, 2, 2 });
            ShowcaseWidgetOptions.SetImagePath(image, userPicture);
            var authorPicture2 = Path.Combine(_root, "author-2.png");
            File.WriteAllBytes(authorPicture2, new byte[] { 3, 3, 3 });
            ShowcaseWidgetOptions.SetImagePath(authorImage, authorPicture2);
            ShowcaseWidgetOptions.SetImageFitMode(authorImage, ShowcaseImageFitMode.Fit);
            WritePackage("1.1.0");
            _updates.MergeIntoTargets("ws:trophies", LibraryApplyMode.Merge);

            Assert.AreSame(image, Local(page, ShowcaseWidgetKind.Image));
            Assert.AreEqual(userPicture, ShowcaseWidgetOptions.GetImagePath(image), "the user's picture stays");
            Assert.AreEqual(ShowcaseImageFitMode.Fit, ShowcaseWidgetOptions.GetImageFitMode(image), "other options follow the update");
            var projected = _targets.AdapterFor(page.PageId).Project(_live);
            StringAssert.DoesNotMatch(projected.ToString(), new System.Text.RegularExpressions.Regex("ImagePath"));
        }

        // ---- helpers ----------------------------------------------------------------------------

        private List<ShowcaseBlockSettings> AuthorBlocks()
        {
            return ShowcaseLayoutService.DenseBlocks(_authorPage);
        }

        private LibraryItem WritePackage(string version)
        {
            var package = Path.Combine(_root, "downloads", version, "trophies" + ShowcasePagePortableStore.PackageFileExtension);
            ShowcasePagePortableStore.Write(package, ShowcasePagePortableStore.BuildPortable(_author, _authorGrids, _authorPage.PageId));
            return _updates.WriteWorkshopPart(
                new LibraryItem
                {
                    Id = "ws:trophies",
                    Kind = LibraryItemKind.ShowcasePage,
                    Name = "Trophies",
                    Origin = LibraryItemOrigin.Workshop,
                    WorkshopItemId = "trophies",
                    Version = version
                },
                package,
                _folder).Item;
        }

        /// <summary>Installs the package as a new page that follows the item, as the Workshop installer does.</summary>
        private ShowcasePageSettings Install(string version)
        {
            var item = WritePackage(version);
            var portable = ShowcasePagePortableStore.Read(_library.FullPath(item));
            try
            {
                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var page = ShowcasePagePortableStore.ApplyPortable(_live.Showcase, _live.GridOptions, portable, null, path => path, map);
                ShowcaseLayoutService.Normalize(_live.Showcase);
                ShowcaseLayoutService.PruneOrphanedWidgets(_live.Showcase);
                var adapter = _targets.AdapterFor(page.PageId);
                _live.SetLibraryLink(adapter.TargetKey, _apply.Link(adapter, item, _live, map));
                return page;
            }
            finally
            {
                ShowcasePagePortableStore.DeleteExtractedImages(portable);
            }
        }

        private ShowcaseWidgetInstanceSettings Local(ShowcasePageSettings page, ShowcaseWidgetKind kind)
        {
            return page.Blocks
                .Where(block => block.WidgetInstanceId != null)
                .Select(block => _live.Showcase.WidgetInstances.FirstOrDefault(widget => widget.InstanceId == block.WidgetInstanceId))
                .FirstOrDefault(widget => widget != null && widget.Kind == kind);
        }
    }
}
