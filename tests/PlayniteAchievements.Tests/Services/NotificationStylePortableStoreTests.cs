using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Images;
using PlayniteAchievements.Services.Notifications;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Services.Tests
{
    [TestClass]
    [DoNotParallelize]
    public class NotificationStylePortableStoreTests
    {
        [TestMethod]
        public async Task ExportPackage_AndImport_RoundTripsFieldsAndBundledImages()
        {
            var tempDir = CreateTempDirectory();
            try
            {
                var store = CreateStore(tempDir, out _);

                var sourceDir = Path.Combine(tempDir, "src");
                Directory.CreateDirectory(sourceDir);
                var backgroundSource = Path.Combine(sourceDir, "bg.png");
                var commonSource = Path.Combine(sourceDir, "common.png");
                WritePlaceholderFile(backgroundSource, "background-bytes");
                WritePlaceholderFile(commonSource, "common-bytes");

                var style = NotificationStyleSettings.CreateDefault();
                // Flip several booleans that default to TRUE to prove they survive the round trip
                // (a DefaultValueHandling.Ignore serializer would corrupt these).
                style.Toast.ShowHeader = false;
                style.Toast.ShowProviderIcon = false;
                style.Frame.ShowUnlockTime = false;
                style.Toast.CountdownBarColor = "#FF00FF";
                style.Toast.LineOrder = new List<string> { "Title", "Header" };
                style.Toast.CardWidth = 500;
                style.Toast.FontFamily = "Arial";
                style.HeaderTexts.UnlockHeader = "Custom Unlock!";
                style.ToastBackgroundImagePath = backgroundSource;
                style.BadgeImages.CommonPath = commonSource;

                var packagePath = Path.Combine(tempDir, "share.pastyle.zip");
                store.ExportPackage(style, packagePath);

                using (var archive = ZipFile.OpenRead(packagePath))
                {
                    var entryNames = archive.Entries.Select(entry => entry.FullName).ToList();
                    CollectionAssert.Contains(entryNames, NotificationStylePortableStore.ManifestEntryName);
                    CollectionAssert.Contains(entryNames, "images/background.png");
                    CollectionAssert.Contains(entryNames, "images/badge_common.png");

                    using (var reader = new StreamReader(
                        archive.GetEntry(NotificationStylePortableStore.ManifestEntryName).Open()))
                    {
                        var portable = JsonConvert.DeserializeObject<NotificationStylePortableFile>(reader.ReadToEnd());
                        Assert.AreEqual(NotificationStylePortableFile.NotificationStyleKind, portable.Kind);
                        Assert.AreEqual(NotificationStylePortableStore.CurrentVersion, portable.Version);
                        Assert.AreEqual("images/background.png", portable.Style.ToastBackgroundImagePath);
                        Assert.AreEqual("images/badge_common.png", portable.Style.BadgeImages.CommonPath);
                    }
                }

                var imported = await store.ImportAsync(packagePath, targetProviderKeyOrNull: null, CancellationToken.None);

                Assert.IsFalse(imported.Toast.ShowHeader);
                Assert.IsFalse(imported.Toast.ShowProviderIcon);
                Assert.IsFalse(imported.Frame.ShowUnlockTime);
                Assert.AreEqual("#FF00FF", imported.Toast.CountdownBarColor);
                CollectionAssert.AreEqual(new List<string> { "Title", "Header" }, imported.Toast.LineOrder);
                Assert.AreEqual(500d, imported.Toast.CardWidth);
                Assert.AreEqual("Arial", imported.Toast.FontFamily);
                Assert.AreEqual("Custom Unlock!", imported.HeaderTexts.UnlockHeader);

                var expectedBackgroundSuffix = Path.Combine("notification_images", "global", "background.png");
                var expectedCommonSuffix = Path.Combine("notification_images", "global", "badge_common.png");
                Assert.IsTrue(imported.ToastBackgroundImagePath.EndsWith(expectedBackgroundSuffix, StringComparison.OrdinalIgnoreCase));
                Assert.IsTrue(imported.BadgeImages.CommonPath.EndsWith(expectedCommonSuffix, StringComparison.OrdinalIgnoreCase));
                Assert.IsTrue(File.Exists(imported.ToastBackgroundImagePath));
                Assert.IsTrue(File.Exists(imported.BadgeImages.CommonPath));
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public async Task ExportPa_AndImport_RoundTripsImageFreeStyle()
        {
            var tempDir = CreateTempDirectory();
            try
            {
                var store = CreateStore(tempDir, out _);

                var style = NotificationStyleSettings.CreateDefault();
                style.Toast.ShowHeader = false;
                style.Toast.TitleFontSize = 22;
                style.HeaderTexts.CompletionHeader = "Done!";

                var filePath = Path.Combine(tempDir, "share.pastyle");
                store.ExportPa(style, filePath);

                var imported = await store.ImportAsync(filePath, targetProviderKeyOrNull: null, CancellationToken.None);

                Assert.IsFalse(imported.Toast.ShowHeader);
                Assert.AreEqual(22d, imported.Toast.TitleFontSize);
                Assert.AreEqual("Done!", imported.HeaderTexts.CompletionHeader);
                Assert.IsNull(imported.ToastBackgroundImagePath);
                Assert.IsNull(imported.BadgeImages.CommonPath);
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public async Task ImportPackage_ForGame_MaterializesImagesIntoIsolatedGameFolder()
        {
            var tempDir = CreateTempDirectory();
            try
            {
                var store = CreateStore(tempDir, out _);
                var source = Path.Combine(tempDir, "background.png");
                WritePngFile(source);

                var style = NotificationStyleSettings.CreateDefault();
                style.ToastBackgroundImagePath = source;
                var packagePath = Path.Combine(tempDir, "game-style.pastyle.zip");
                store.ExportPackage(style, packagePath);

                var gameId = Guid.NewGuid();
                var imported = await store.ImportAsync(
                    packagePath,
                    NotificationImageOwner.ForGame(gameId),
                    CancellationToken.None);

                var expectedSuffix = Path.Combine(
                    "notification_images",
                    "games",
                    gameId.ToString("D"),
                    "background.png");
                Assert.IsTrue(imported.ToastBackgroundImagePath.EndsWith(
                    expectedSuffix,
                    StringComparison.OrdinalIgnoreCase));
                Assert.IsTrue(File.Exists(imported.ToastBackgroundImagePath));
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public async Task PruneOrphans_UsesGameRowsWithoutTreatingLegacyCallsAsAuthoritative()
        {
            var tempDir = CreateTempDirectory();
            try
            {
                CreateStore(tempDir, out var imageStore);
                var source = Path.Combine(tempDir, "background.png");
                WritePngFile(source);
                var gameId = Guid.NewGuid();
                var owner = NotificationImageOwner.ForGame(gameId);
                var managedPath = await imageStore.MaterializeAsync(
                    source,
                    owner,
                    NotificationImageSlot.Background,
                    CancellationToken.None);

                imageStore.PruneOrphans(new PersistedSettings());
                Assert.IsTrue(File.Exists(managedPath));

                var style = NotificationStyleSettings.CreateDefault();
                style.ToastBackgroundImagePath = managedPath;
                imageStore.PruneOrphans(
                    new PersistedSettings(),
                    new[]
                    {
                        new GameCustomDataFile
                        {
                            PlayniteGameId = gameId,
                            NotificationAppearanceOverride =
                                new GameNotificationAppearanceOverride
                                {
                                    Style = style
                                }
                        }
                    });
                Assert.IsTrue(File.Exists(managedPath));

                imageStore.PruneOrphans(
                    new PersistedSettings(),
                    Array.Empty<GameCustomDataFile>());
                Assert.IsFalse(File.Exists(managedPath));
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public void ExportPa_WithLocalImages_Throws()
        {
            var tempDir = CreateTempDirectory();
            try
            {
                var store = CreateStore(tempDir, out _);

                var sourceDir = Path.Combine(tempDir, "src");
                Directory.CreateDirectory(sourceDir);
                var backgroundSource = Path.Combine(sourceDir, "bg.png");
                WritePlaceholderFile(backgroundSource, "background-bytes");

                var style = NotificationStyleSettings.CreateDefault();
                style.ToastBackgroundImagePath = backgroundSource;

                Assert.ThrowsException<InvalidOperationException>(() =>
                    store.ExportPa(style, Path.Combine(tempDir, "share.pastyle")));
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public async Task ImportAsync_ForeignKind_Throws()
        {
            var tempDir = CreateTempDirectory();
            try
            {
                var store = CreateStore(tempDir, out _);

                var filePath = Path.Combine(tempDir, "foreign.pastyle");
                File.WriteAllText(filePath, "{\"Kind\":\"PlayniteAchievements.CustomData\",\"Version\":1,\"Style\":{}}");

                await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                    store.ImportAsync(filePath, targetProviderKeyOrNull: null, CancellationToken.None));
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public async Task ImportPackage_TraversalEntry_Throws()
        {
            var tempDir = CreateTempDirectory();
            try
            {
                var store = CreateStore(tempDir, out _);

                var packagePath = Path.Combine(tempDir, "evil.pastyle.zip");
                using (var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create))
                {
                    var manifest = archive.CreateEntry(NotificationStylePortableStore.ManifestEntryName);
                    using (var writer = new StreamWriter(manifest.Open()))
                    {
                        writer.Write("{\"Kind\":\"" + NotificationStylePortableFile.NotificationStyleKind +
                                     "\",\"Version\":1,\"Style\":{}}");
                    }

                    // A background-slot entry that matches the slot prefix but smuggles a traversal
                    // segment; the store must reject it before extracting.
                    var evil = archive.CreateEntry("images/background./../secret.png");
                    using (var writer = new StreamWriter(evil.Open()))
                    {
                        writer.Write("payload");
                    }
                }

                await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                    store.ImportAsync(packagePath, targetProviderKeyOrNull: null, CancellationToken.None));
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        private static NotificationStylePortableStore CreateStore(string tempDir, out NotificationImageStore imageStore)
        {
            var diskImageService = new DiskImageService(logger: null, cacheRoot: tempDir);
            imageStore = new NotificationImageStore(diskImageService, logger: null);
            return new NotificationStylePortableStore(imageStore, logger: null);
        }

        private static void WritePlaceholderFile(string path, string content)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(path, content);
        }

        private static void WritePngFile(string path)
        {
            var pngBytes = Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIW2NkYGD4DwABBAEAgh8sXQAAAABJRU5ErkJggg==");
            File.WriteAllBytes(path, pngBytes);
        }

        private static string CreateTempDirectory()
        {
            var path = Path.Combine(Path.GetTempPath(), "PlayniteAchievementsTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        private static void DeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch
            {
                // Best-effort cleanup.
            }
        }
    }
}
