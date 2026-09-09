using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Images;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PlayniteAchievements.Services.Images.Tests
{
    /// <summary>
    /// Covers the managed storage for the user's global locked and hidden fallback images:
    /// materialization from a local file, replacement, deletion, and orphan pruning.
    /// </summary>
    [TestClass]
    public class FallbackIconStoreTests
    {
        [TestMethod]
        public void GetRootDirectory_SitsBesideIconCacheNotInsideIt()
        {
            var tempDir = CreateTempDirectory();
            try
            {
                using (var diskImageService = new DiskImageService(logger: null, cacheRoot: tempDir))
                {
                    var store = new FallbackIconStore(diskImageService);

                    var root = Path.GetFullPath(store.GetRootDirectory());
                    var iconCache = Path.GetFullPath(diskImageService.GetCacheDirectoryPath());

                    Assert.AreEqual(Path.Combine(tempDir, "fallback_icons"), root);
                    Assert.IsFalse(
                        root.StartsWith(iconCache, StringComparison.OrdinalIgnoreCase),
                        "Fallback images must live outside icon_cache so cache clears cannot delete them.");
                }
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public void MaterializeAsync_CopiesLocalFileIntoTheSlot()
        {
            var tempDir = CreateTempDirectory();
            try
            {
                var source = Path.Combine(tempDir, "source", "picked.png");
                WriteSolidColorPng(source, Colors.Red);

                using (var diskImageService = new DiskImageService(logger: null, cacheRoot: tempDir))
                {
                    var store = new FallbackIconStore(diskImageService);

                    var resolved = store
                        .MaterializeAsync(source, FallbackIconSlot.Locked, CancellationToken.None)
                        .GetAwaiter()
                        .GetResult();

                    Assert.IsNotNull(resolved);
                    Assert.IsTrue(File.Exists(resolved));
                    Assert.AreEqual(
                        Path.GetFullPath(Path.Combine(store.GetRootDirectory(), "locked.png")),
                        Path.GetFullPath(resolved));
                    Assert.IsTrue(File.Exists(source), "The picked file must be copied, not moved.");
                }
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public void MaterializeAsync_KeepsTheTwoSlotsIndependent()
        {
            var tempDir = CreateTempDirectory();
            try
            {
                var lockedSource = Path.Combine(tempDir, "source", "locked-pick.png");
                var hiddenSource = Path.Combine(tempDir, "source", "hidden-pick.png");
                WriteSolidColorPng(lockedSource, Colors.Red);
                WriteSolidColorPng(hiddenSource, Colors.Blue);

                using (var diskImageService = new DiskImageService(logger: null, cacheRoot: tempDir))
                {
                    var store = new FallbackIconStore(diskImageService);

                    var locked = store
                        .MaterializeAsync(lockedSource, FallbackIconSlot.Locked, CancellationToken.None)
                        .GetAwaiter().GetResult();
                    var hidden = store
                        .MaterializeAsync(hiddenSource, FallbackIconSlot.Hidden, CancellationToken.None)
                        .GetAwaiter().GetResult();

                    Assert.AreNotEqual(Path.GetFullPath(locked), Path.GetFullPath(hidden));
                    Assert.IsTrue(File.Exists(locked));
                    Assert.IsTrue(File.Exists(hidden));
                    StringAssert.EndsWith(locked, "locked.png");
                    StringAssert.EndsWith(hidden, "hidden.png");
                }
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public void MaterializeAsync_ReplacingAnImageLeavesOnlyOneSlotFile()
        {
            var tempDir = CreateTempDirectory();
            try
            {
                var first = Path.Combine(tempDir, "source", "first.png");
                var second = Path.Combine(tempDir, "source", "second.png");
                WriteSolidColorPng(first, Colors.Red);
                WriteSolidColorPng(second, Colors.Blue);

                using (var diskImageService = new DiskImageService(logger: null, cacheRoot: tempDir))
                {
                    var store = new FallbackIconStore(diskImageService);

                    store.MaterializeAsync(first, FallbackIconSlot.Locked, CancellationToken.None)
                        .GetAwaiter().GetResult();
                    var resolved = store
                        .MaterializeAsync(second, FallbackIconSlot.Locked, CancellationToken.None)
                        .GetAwaiter().GetResult();

                    Assert.IsNotNull(resolved);
                    var slotFiles = Directory
                        .EnumerateFiles(store.GetRootDirectory(), "locked.*")
                        .ToList();
                    Assert.AreEqual(1, slotFiles.Count, "A replaced slot must not leave stale siblings.");
                }
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public void MaterializeAsync_ReturnsNullForAMissingSource()
        {
            var tempDir = CreateTempDirectory();
            try
            {
                using (var diskImageService = new DiskImageService(logger: null, cacheRoot: tempDir))
                {
                    var store = new FallbackIconStore(diskImageService);

                    var resolved = store
                        .MaterializeAsync(
                            Path.Combine(tempDir, "does-not-exist.png"),
                            FallbackIconSlot.Locked,
                            CancellationToken.None)
                        .GetAwaiter().GetResult();

                    Assert.IsNull(resolved);
                }
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public void DeleteSlot_RemovesOnlyTheRequestedSlot()
        {
            var tempDir = CreateTempDirectory();
            try
            {
                var source = Path.Combine(tempDir, "source", "picked.png");
                WriteSolidColorPng(source, Colors.Red);

                using (var diskImageService = new DiskImageService(logger: null, cacheRoot: tempDir))
                {
                    var store = new FallbackIconStore(diskImageService);
                    var locked = store
                        .MaterializeAsync(source, FallbackIconSlot.Locked, CancellationToken.None)
                        .GetAwaiter().GetResult();
                    var hidden = store
                        .MaterializeAsync(source, FallbackIconSlot.Hidden, CancellationToken.None)
                        .GetAwaiter().GetResult();

                    store.DeleteSlot(FallbackIconSlot.Locked);

                    Assert.IsFalse(File.Exists(locked));
                    Assert.IsTrue(File.Exists(hidden));
                }
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public void PruneOrphans_DeletesFilesTheSettingsNoLongerPointAt()
        {
            var tempDir = CreateTempDirectory();
            try
            {
                var source = Path.Combine(tempDir, "source", "picked.png");
                WriteSolidColorPng(source, Colors.Red);

                using (var diskImageService = new DiskImageService(logger: null, cacheRoot: tempDir))
                {
                    var store = new FallbackIconStore(diskImageService);
                    var locked = store
                        .MaterializeAsync(source, FallbackIconSlot.Locked, CancellationToken.None)
                        .GetAwaiter().GetResult();
                    var hidden = store
                        .MaterializeAsync(source, FallbackIconSlot.Hidden, CancellationToken.None)
                        .GetAwaiter().GetResult();

                    // Mirrors cancelling the settings dialog after picking a hidden image: the
                    // path reverted to null but the copied file is still on disk.
                    var settings = new PersistedSettings
                    {
                        LockedFallbackIconPath = locked,
                        HiddenFallbackIconPath = null
                    };

                    store.PruneOrphans(settings);

                    Assert.IsTrue(File.Exists(locked), "A slot the settings still reference must survive.");
                    Assert.IsFalse(File.Exists(hidden), "An unreferenced slot file must be reclaimed.");
                }
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public void GetAndSetPath_RoundTripEverySlot()
        {
            var settings = new PersistedSettings();

            foreach (var slot in FallbackIconStore.Slots)
            {
                Assert.IsNull(FallbackIconStore.GetPath(settings, slot));

                FallbackIconStore.SetPath(settings, slot, @"C:\images\" + slot + ".png");
                Assert.AreEqual(@"C:\images\" + slot + ".png", FallbackIconStore.GetPath(settings, slot));
            }

            // Distinct properties, not one shared field.
            Assert.AreNotEqual(settings.LockedFallbackIconPath, settings.HiddenFallbackIconPath);
        }

        private static string CreateTempDirectory()
        {
            var path = Path.Combine(Path.GetTempPath(), "PlayniteAchievementsTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        private static void DeleteDirectory(string path)
        {
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }

        private static void WriteSolidColorPng(string path, Color color)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var pixels = new byte[]
            {
                color.B, color.G, color.R, color.A,
                color.B, color.G, color.R, color.A,
                color.B, color.G, color.R, color.A,
                color.B, color.G, color.R, color.A
            };
            var bitmap = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, pixels, 8);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                encoder.Save(stream);
            }
        }
    }
}
