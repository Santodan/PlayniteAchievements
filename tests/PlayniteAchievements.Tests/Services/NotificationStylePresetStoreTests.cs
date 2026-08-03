using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Images;
using PlayniteAchievements.Services.Notifications;
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Services.Tests
{
    [TestClass]
    [DoNotParallelize]
    public class NotificationStylePresetStoreTests
    {
        [TestMethod]
        public void SaveToastPreset_CarriesToastSurfaceImagesAndHeaderTexts_FrameAtDefault()
        {
            var tempDir = CreateTempDirectory();
            try
            {
                var store = CreateStore(tempDir);
                var backgroundSource = Path.Combine(tempDir, "bg.png");
                WritePngFile(backgroundSource);

                var style = NotificationStyleSettings.CreateDefault();
                style.Toast.ShowHeader = false;
                style.Toast.CardWidth = 480;
                style.Frame.ShowUnlockTime = false;
                style.ToastBackgroundImagePath = backgroundSource;
                style.Toast.HeaderTexts.UnlockHeader = "Preset Unlock!";
                style.Frame.HeaderTexts.UnlockHeader = "Frame header";

                store.SavePreset(isFrame: false, "My Toast", style, templateXamlOrNull: null);

                var preset = store.ListPresets(isFrame: false).Single();
                Assert.AreEqual("My Toast", preset.Name);
                Assert.IsFalse(preset.IsFrame);

                var manifest = ReadManifest(preset.FilePath);
                Assert.IsFalse(manifest.Style.Toast.ShowHeader);
                Assert.AreEqual(480d, manifest.Style.Toast.CardWidth);
                Assert.AreEqual("Preset Unlock!", manifest.Style.Toast.HeaderTexts.UnlockHeader);
                Assert.AreEqual("images/background.png", manifest.Style.ToastBackgroundImagePath);
                // The frame surface (including its header texts) must not travel with a toast preset.
                Assert.IsTrue(manifest.Style.Frame.ShowUnlockTime);
                Assert.IsNull(manifest.Style.Frame.HeaderTexts?.UnlockHeader);
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public void SaveFramePreset_CarriesFrameSurfaceOnly_ToastDataDoesNotTravel()
        {
            var tempDir = CreateTempDirectory();
            try
            {
                var store = CreateStore(tempDir);
                var backgroundSource = Path.Combine(tempDir, "bg.png");
                WritePngFile(backgroundSource);

                var style = NotificationStyleSettings.CreateDefault();
                style.Frame.ShowUnlockTime = false;
                style.Frame.TitleFontSize = 30;
                style.Frame.HeaderTexts.UnlockHeader = "Frame header";
                style.Toast.ShowHeader = false;
                style.ToastBackgroundImagePath = backgroundSource;
                style.Toast.HeaderTexts.UnlockHeader = "Should not travel";

                store.SavePreset(isFrame: true, "My Frame", style, templateXamlOrNull: null);

                var preset = store.ListPresets(isFrame: true).Single();
                Assert.IsTrue(preset.IsFrame);

                using (var archive = ZipFile.OpenRead(preset.FilePath))
                {
                    // No frame badge images are set, so the package bundles no image entries
                    // (the toast-only background never travels with a frame preset).
                    Assert.IsFalse(
                        archive.Entries.Any(entry => entry.FullName.StartsWith("images/", StringComparison.OrdinalIgnoreCase)),
                        "A frame preset without frame badge images must bundle no images.");
                }

                var manifest = ReadManifest(preset.FilePath);
                Assert.IsFalse(manifest.Style.Frame.ShowUnlockTime);
                Assert.AreEqual(30d, manifest.Style.Frame.TitleFontSize);
                // The frame's own header texts travel with the frame preset.
                Assert.AreEqual("Frame header", manifest.Style.Frame.HeaderTexts.UnlockHeader);
                // The toast surface, its header texts, and the background must not travel.
                Assert.IsTrue(manifest.Style.Toast.ShowHeader);
                Assert.IsNull(manifest.Style.ToastBackgroundImagePath);
                Assert.IsNull(manifest.Style.Toast.HeaderTexts?.UnlockHeader);
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public void SavePreset_WithTemplate_EmbedsTemplateForItsSurfaceOnly()
        {
            var tempDir = CreateTempDirectory();
            try
            {
                var store = CreateStore(tempDir, out var portableStore);
                var style = NotificationStyleSettings.CreateDefault();
                const string toastXaml = "<ResourceDictionary xmlns=\"toast\"><!--toast--></ResourceDictionary>";
                const string frameXaml = "<ResourceDictionary xmlns=\"frame\"><!--frame--></ResourceDictionary>";

                store.SavePreset(isFrame: false, "with-toast-template", style, toastXaml);
                store.SavePreset(isFrame: true, "with-frame-template", style, frameXaml);

                var toastPreset = store.ListPresets(isFrame: false).Single();
                var toastContents = portableStore.InspectPackage(toastPreset.FilePath);
                Assert.IsTrue(toastContents.HasToastTemplate);
                Assert.IsFalse(toastContents.HasFrameTemplate);
                Assert.AreEqual(toastXaml, store.ReadPresetTemplateXaml(toastPreset));

                var framePreset = store.ListPresets(isFrame: true).Single();
                var frameContents = portableStore.InspectPackage(framePreset.FilePath);
                Assert.IsTrue(frameContents.HasFrameTemplate);
                Assert.IsFalse(frameContents.HasToastTemplate);
                Assert.AreEqual(frameXaml, store.ReadPresetTemplateXaml(framePreset));

                store.SavePreset(isFrame: false, "no-template", style, templateXamlOrNull: null);
                var bare = store.ListPresets(isFrame: false)
                    .Single(preset => preset.Name == "no-template");
                Assert.IsNull(store.ReadPresetTemplateXaml(bare));
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public void ListPresets_FiltersBySurface_SortsByName_IgnoresForeignFiles()
        {
            var tempDir = CreateTempDirectory();
            try
            {
                var store = CreateStore(tempDir);
                var style = NotificationStyleSettings.CreateDefault();

                store.SavePreset(isFrame: false, "beta", style, null);
                store.SavePreset(isFrame: false, "Alpha", style, null);
                store.SavePreset(isFrame: true, "frame-only", style, null);

                var toastDir = Path.Combine(tempDir, "data", "notification_style_presets", "toast");
                File.WriteAllText(Path.Combine(toastDir, "notes.txt"), "not a preset");

                var toastPresets = store.ListPresets(isFrame: false);
                CollectionAssert.AreEqual(
                    new[] { "Alpha", "beta" },
                    toastPresets.Select(preset => preset.Name).ToArray());
                Assert.AreEqual(1, store.CountPresets(isFrame: true));
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public void SavePreset_SameName_Overwrites_AndPresetExistsIsCaseInsensitive()
        {
            var tempDir = CreateTempDirectory();
            try
            {
                var store = CreateStore(tempDir);
                var style = NotificationStyleSettings.CreateDefault();
                style.Toast.CardWidth = 300;
                store.SavePreset(isFrame: false, "look", style, null);

                Assert.IsTrue(store.PresetExists(isFrame: false, "look"));
                Assert.IsTrue(store.PresetExists(isFrame: false, "LOOK"));
                Assert.IsFalse(store.PresetExists(isFrame: true, "look"));

                style.Toast.CardWidth = 555;
                store.SavePreset(isFrame: false, "look", style, null);

                Assert.AreEqual(1, store.CountPresets(isFrame: false));
                var manifest = ReadManifest(store.ListPresets(isFrame: false).Single().FilePath);
                Assert.AreEqual(555d, manifest.Style.Toast.CardWidth);
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public void SanitizeName_StripsInvalidChars_TrimsAndCapsLength()
        {
            Assert.AreEqual("my preset", NotificationStylePresetStore.SanitizeName("  my preset  "));
            Assert.AreEqual("ab", NotificationStylePresetStore.SanitizeName("a<>:\"/\\|?*b"));
            Assert.AreEqual(string.Empty, NotificationStylePresetStore.SanitizeName("   "));
            Assert.AreEqual(string.Empty, NotificationStylePresetStore.SanitizeName(null));

            var oversized = new string('x', NotificationStylePresetStore.MaxNameLength + 10);
            Assert.AreEqual(
                NotificationStylePresetStore.MaxNameLength,
                NotificationStylePresetStore.SanitizeName(oversized).Length);
        }

        [TestMethod]
        public void DeletePreset_RemovesTheFile()
        {
            var tempDir = CreateTempDirectory();
            try
            {
                var store = CreateStore(tempDir);
                store.SavePreset(isFrame: false, "doomed", NotificationStyleSettings.CreateDefault(), null);
                var preset = store.ListPresets(isFrame: false).Single();

                store.DeletePreset(preset);

                Assert.IsFalse(File.Exists(preset.FilePath));
                Assert.AreEqual(0, store.CountPresets(isFrame: false));
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public async Task LoadPresetStyleAsync_ToastPreset_MaterializesImagesIntoTargetOwner()
        {
            var tempDir = CreateTempDirectory();
            try
            {
                var store = CreateStore(tempDir);
                var backgroundSource = Path.Combine(tempDir, "bg.png");
                WritePngFile(backgroundSource);

                var style = NotificationStyleSettings.CreateDefault();
                style.Toast.ShowHeader = false;
                style.ToastBackgroundImagePath = backgroundSource;
                store.SavePreset(isFrame: false, "imaged", style, null);
                var preset = store.ListPresets(isFrame: false).Single();

                var loaded = await store.LoadPresetStyleAsync(
                    preset,
                    NotificationImageOwner.ForProvider("steam"),
                    CancellationToken.None);

                Assert.IsFalse(loaded.Toast.ShowHeader);
                var expectedSuffix = Path.Combine(
                    "notification_images", "providers", "steam", "background.png");
                Assert.IsTrue(loaded.ToastBackgroundImagePath.EndsWith(
                    expectedSuffix, StringComparison.OrdinalIgnoreCase));
                Assert.IsTrue(File.Exists(loaded.ToastBackgroundImagePath));
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public async Task LoadPresetStyleAsync_FramePreset_ReturnsNullImagePaths()
        {
            var tempDir = CreateTempDirectory();
            try
            {
                var store = CreateStore(tempDir);
                var style = NotificationStyleSettings.CreateDefault();
                style.Frame.ShowUnlockTime = false;
                store.SavePreset(isFrame: true, "plain-frame", style, null);
                var preset = store.ListPresets(isFrame: true).Single();

                var loaded = await store.LoadPresetStyleAsync(
                    preset,
                    NotificationImageOwner.Global,
                    CancellationToken.None);

                Assert.IsFalse(loaded.Frame.ShowUnlockTime);
                Assert.IsNull(loaded.ToastBackgroundImagePath);
                Assert.IsNull(loaded.Toast.BadgeImages.CommonPath);
                Assert.IsNull(loaded.Frame.BadgeImages.CommonPath);
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        private static NotificationStylePresetStore CreateStore(string tempDir)
        {
            return CreateStore(tempDir, out _);
        }

        private static NotificationStylePresetStore CreateStore(
            string tempDir,
            out NotificationStylePortableStore portableStore)
        {
            var diskImageService = new DiskImageService(logger: null, cacheRoot: tempDir);
            var imageStore = new NotificationImageStore(diskImageService, logger: null);
            portableStore = new NotificationStylePortableStore(imageStore, logger: null);
            return new NotificationStylePresetStore(portableStore, Path.Combine(tempDir, "data"));
        }

        private static NotificationStylePortableFile ReadManifest(string packagePath)
        {
            using (var archive = ZipFile.OpenRead(packagePath))
            using (var reader = new StreamReader(
                archive.GetEntry(NotificationStylePortableStore.ManifestEntryName).Open()))
            {
                return JsonConvert.DeserializeObject<NotificationStylePortableFile>(reader.ReadToEnd());
            }
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
