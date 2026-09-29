using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Showcase;

namespace PlayniteAchievements.Tests.Services
{
    [TestClass]
    public class ShowcaseImageStoreTests
    {
        private string _root;
        private string _dataPath;
        private ShowcaseImageStore _store;

        [TestInitialize]
        public void Initialize()
        {
            _root = Path.Combine(
                Path.GetTempPath(),
                "PlayniteAchievements.Tests",
                nameof(ShowcaseImageStoreTests),
                Guid.NewGuid().ToString("N"));
            _dataPath = Path.Combine(_root, "data");
            Directory.CreateDirectory(_dataPath);
            _store = new ShowcaseImageStore(_dataPath);
        }

        [TestCleanup]
        public void Cleanup()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        [TestMethod]
        public void Import_NamesTheCopyByContentAndKeepsTheExtension()
        {
            var first = WriteSource("one.PNG", 1, 2, 3);
            var second = WriteSource("two.png", 1, 2, 3);
            var different = WriteSource("three.jpg", 4, 5, 6);

            var storedFirst = _store.Import(first);
            var storedSecond = _store.Import(second);
            var storedDifferent = _store.Import(different);

            Assert.IsTrue(_store.IsManaged(storedFirst));
            Assert.AreEqual(storedFirst, storedSecond, "same bytes share one file");
            Assert.AreNotEqual(storedFirst, storedDifferent);
            Assert.AreEqual(".png", Path.GetExtension(storedFirst));
            Assert.AreEqual(".jpg", Path.GetExtension(storedDifferent));
            Assert.AreEqual(2, Directory.GetFiles(_store.RootDirectory).Length);
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, File.ReadAllBytes(storedFirst));
            Assert.AreEqual(storedFirst, _store.Import(storedFirst), "a stored path imports to itself");
        }

        [TestMethod]
        public void Import_RejectsMissingBlankAndUnsupportedFiles()
        {
            Assert.IsNull(_store.Import(null));
            Assert.IsNull(_store.Import("   "));
            Assert.IsNull(_store.Import(Path.Combine(_root, "missing.png")));
            Assert.IsNull(_store.Import(WriteSource("notes.txt", 1)));
            Assert.IsFalse(Directory.Exists(_store.RootDirectory) && Directory.GetFiles(_store.RootDirectory).Any());
        }

        [TestMethod]
        public void MigrateAndPrune_RehomesRawPathsRemovesTheLegacyFolderAndDeletesOrphans()
        {
            var raw = WriteSource("raw.png", 1);
            var legacyDirectory = Path.Combine(_dataPath, "showcase", "profile");
            Directory.CreateDirectory(legacyDirectory);
            var legacy = Path.Combine(legacyDirectory, "background.jpg");
            File.WriteAllBytes(legacy, new byte[] { 2 });
            var orphan = _store.Import(WriteSource("orphan.png", 3));
            var missing = Path.Combine(_root, "gone.png");

            var settings = new ShowcaseSettings();
            var page = ShowcaseLayoutService.CreateWidget(settings, ShowcaseWidgetKind.Profile);
            page.Profile = new ShowcaseProfileSettings { AvatarPath = raw, BackgroundPath = legacy };
            settings.StartPageInstances["start"] = new ShowcaseWidgetInstanceSettings
            {
                Kind = ShowcaseWidgetKind.Profile,
                Profile = new ShowcaseProfileSettings { AvatarPath = raw, BackgroundPath = missing }
            };

            Assert.IsTrue(_store.MigrateAndPrune(settings));

            Assert.IsTrue(_store.IsManaged(page.Profile.AvatarPath));
            Assert.IsTrue(_store.IsManaged(page.Profile.BackgroundPath));
            Assert.AreEqual(page.Profile.AvatarPath, settings.StartPageInstances["start"].Profile.AvatarPath);
            Assert.AreEqual(missing, settings.StartPageInstances["start"].Profile.BackgroundPath, "nothing to copy is left alone");
            Assert.IsFalse(Directory.Exists(legacyDirectory));
            Assert.IsFalse(File.Exists(orphan));
            Assert.AreEqual(2, Directory.GetFiles(_store.RootDirectory).Length);
            Assert.IsTrue(File.Exists(raw), "sources are copied, never moved");

            Assert.IsFalse(_store.MigrateAndPrune(settings), "a second pass changes nothing");
        }

        [TestMethod]
        public void Prune_KeepsAFileWhileAnyWidgetStillReferencesIt()
        {
            var stored = _store.Import(WriteSource("shared.png", 7));
            var settings = new ShowcaseSettings();
            var one = ShowcaseLayoutService.CreateWidget(settings, ShowcaseWidgetKind.Profile);
            one.Profile.BackgroundPath = stored;
            var two = ShowcaseLayoutService.CreateWidget(settings, ShowcaseWidgetKind.Profile);
            two.Profile.BackgroundPath = stored;

            ShowcaseLayoutService.DeleteWidget(settings, one.InstanceId);
            _store.Prune(settings);
            Assert.IsTrue(File.Exists(stored));

            ShowcaseLayoutService.DeleteWidget(settings, two.InstanceId);
            _store.Prune(settings);
            Assert.IsFalse(File.Exists(stored));
        }

        private string WriteSource(string name, params byte[] bytes)
        {
            var path = Path.Combine(_root, name);
            File.WriteAllBytes(path, bytes);
            return path;
        }
    }
}
