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
        public void ExportPackage_AndRead_RoundTripsFieldsAndBundledImages()
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
                style.Toast.HeaderTexts.UnlockHeader = "Custom Unlock!";
                style.ToastBackgroundImagePath = backgroundSource;
                style.Toast.BadgeImages.CommonPath = commonSource;

                var packagePath = Path.Combine(tempDir, "share.pastyle.zip");
                store.ExportLegacyBothSurfacesPackage(style, packagePath);

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
                        Assert.AreEqual("images/badge_common.png", portable.Style.Toast.BadgeImages.CommonPath);
                    }
                }

                var imported = Read(store, tempDir, packagePath);

                Assert.IsFalse(imported.Toast.ShowHeader);
                Assert.IsFalse(imported.Toast.ShowProviderIcon);
                Assert.IsFalse(imported.Frame.ShowUnlockTime);
                Assert.AreEqual("#FF00FF", imported.Toast.CountdownBarColor);
                CollectionAssert.AreEqual(new List<string> { "Title", "Header" }, imported.Toast.LineOrder);
                Assert.AreEqual(500d, imported.Toast.CardWidth);
                Assert.AreEqual("Arial", imported.Toast.FontFamily);
                Assert.AreEqual("Custom Unlock!", imported.Toast.HeaderTexts.UnlockHeader);
                AssertScratchFile(ScratchOf(tempDir), imported.ToastBackgroundImagePath, "background-bytes");
                AssertScratchFile(ScratchOf(tempDir), imported.Toast.BadgeImages.CommonPath, "common-bytes");
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public void ExportSurfacePackage_BitmapImage_IsWrittenAsPng()
        {
            var tempDir = CreateTempDirectory();
            try
            {
                var store = CreateStore(tempDir, out _);
                var bitmap = Path.Combine(tempDir, "badge.bmp");
                WriteBmpFile(bitmap);

                var style = NotificationStyleSettings.CreateDefault();
                style.Toast.BadgeImages.CommonPath = bitmap;

                var packagePath = Path.Combine(tempDir, "pack.panotif");
                store.ExportSurfacePackage(isFrame: false, style, packagePath);

                using (var archive = ZipFile.OpenRead(packagePath))
                {
                    // The Workshop takes PNG, JPEG, GIF, WebP and WebM only.
                    var entry = archive.GetEntry("images/badge_common.png");
                    Assert.IsNotNull(entry);
                    Assert.IsNull(archive.GetEntry("images/badge_common.bmp"));
                    using (var stream = entry.Open())
                    {
                        var head = new byte[16];
                        Array.Resize(ref head, stream.Read(head, 0, head.Length));
                        Assert.IsTrue(PackageImageFormat.IsPortable(head));
                    }
                }
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public void ExportSurfacePackage_CarriesEachKindStyleAndItsOwnImages()
        {
            var tempDir = CreateTempDirectory();
            try
            {
                var store = CreateStore(tempDir, out _);

                var sourceDir = Path.Combine(tempDir, "src");
                Directory.CreateDirectory(sourceDir);
                var sharedBadge = Path.Combine(sourceDir, "shared.png");
                var capstoneBadge = Path.Combine(sourceDir, "capstone.png");
                WritePlaceholderFile(sharedBadge, "shared-bytes");
                WritePlaceholderFile(capstoneBadge, "capstone-bytes");

                var style = NotificationStyleSettings.CreateDefault();
                style.Toast.HeaderTexts.UnlockHeader = "Shared header";
                style.Toast.BadgeImages.CommonPath = sharedBadge;

                var capstone = style.EnableKindStyle(NotificationKind.Capstone);
                capstone.Toast.HeaderTexts.UnlockHeader = "Capstone header";
                capstone.Toast.CardWidth = 640;
                capstone.Toast.BadgeImages.CommonPath = capstoneBadge;

                var rare = style.EnableKindStyle(NotificationKind.Rare);
                rare.Toast.ShowIcon = false;

                var packagePath = Path.Combine(tempDir, "pack.panotif");
                store.ExportSurfacePackage(isFrame: false, style, packagePath);

                using (var archive = ZipFile.OpenRead(packagePath))
                {
                    var entryNames = archive.Entries.Select(entry => entry.FullName).ToList();
                    CollectionAssert.Contains(entryNames, "images/badge_common.png");
                    CollectionAssert.Contains(entryNames, "images/kind_capstone__badge_common.png");
                }

                var imported = Read(store, tempDir, packagePath);

                Assert.AreEqual("Shared header", imported.Toast.HeaderTexts.UnlockHeader);
                Assert.IsTrue(imported.HasKindStyle(NotificationKind.Capstone));
                Assert.IsTrue(imported.HasKindStyle(NotificationKind.Rare));

                var importedCapstone = imported.ResolveKind(NotificationKind.Capstone);
                Assert.AreEqual("Capstone header", importedCapstone.Toast.HeaderTexts.UnlockHeader);
                Assert.AreEqual(640d, importedCapstone.Toast.CardWidth);
                Assert.IsFalse(imported.ResolveKind(NotificationKind.Rare).Toast.ShowIcon);

                // The kind's image override comes from its own entry, not from the shared slot.
                AssertScratchFile(ScratchOf(tempDir), imported.Toast.BadgeImages.CommonPath, "shared-bytes");
                AssertScratchFile(ScratchOf(tempDir), importedCapstone.Toast.BadgeImages.CommonPath, "capstone-bytes");
                Assert.AreNotEqual(imported.Toast.BadgeImages.CommonPath, importedCapstone.Toast.BadgeImages.CommonPath);
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public void ExportSurfacePackage_Frame_RoundTripsOnlyTheFrameSurface()
        {
            var tempDir = CreateTempDirectory();
            try
            {
                var store = CreateStore(tempDir, out _);

                var sourceDir = Path.Combine(tempDir, "src");
                Directory.CreateDirectory(sourceDir);
                var frameCommonSource = Path.Combine(sourceDir, "frame-common.png");
                WritePlaceholderFile(frameCommonSource, "frame-common-bytes");
                var frameBackgroundSource = Path.Combine(sourceDir, "frame-bg.png");
                WritePlaceholderFile(frameBackgroundSource, "frame-background-bytes");
                var toastBackgroundSource = Path.Combine(sourceDir, "toast-bg.png");
                WritePlaceholderFile(toastBackgroundSource, "toast-background-bytes");

                var style = NotificationStyleSettings.CreateDefault();
                style.Frame.ShowUnlockTime = false;
                style.Frame.HeaderTexts.UnlockHeader = "Frame header";
                style.Frame.BadgeImages.CommonPath = frameCommonSource;
                style.Frame.FrameVignette = FrameVignetteStyle.Image;
                style.Frame.ImageKeyframeMs = 400;
                style.FrameBackgroundImagePath = frameBackgroundSource;
                style.ToastBackgroundImagePath = toastBackgroundSource;
                style.Toast.ShowHeader = false;
                style.Toast.HeaderTexts.UnlockHeader = "Should not travel";

                var packagePath = Path.Combine(tempDir, "share.paframe");
                store.ExportSurfacePackage(isFrame: true, style, packagePath);

                using (var archive = ZipFile.OpenRead(packagePath))
                {
                    var entryNames = archive.Entries.Select(entry => entry.FullName).ToList();
                    CollectionAssert.Contains(entryNames, "images/frame_badge_common.png");
                    CollectionAssert.Contains(entryNames, "images/frame_background.png");
                    CollectionAssert.DoesNotContain(entryNames, "images/background.png");

                    using (var reader = new StreamReader(
                        archive.GetEntry(NotificationStylePortableStore.ManifestEntryName).Open()))
                    {
                        var portable = JsonConvert.DeserializeObject<NotificationStylePortableFile>(reader.ReadToEnd());
                        Assert.IsFalse(portable.HasToast);
                        Assert.IsTrue(portable.HasFrame);
                        // Toast data stays at factory defaults in a frame package.
                        Assert.IsTrue(portable.Style.Toast.ShowHeader);
                        Assert.IsNull(portable.Style.Toast.HeaderTexts?.UnlockHeader);
                    }
                }

                var contents = store.InspectPackage(packagePath);
                Assert.IsTrue(contents.HasStyle);
                Assert.IsTrue(contents.HasFrameStyle);
                Assert.IsFalse(contents.HasToastStyle);

                var imported = Read(store, tempDir, packagePath);
                Assert.IsFalse(imported.Frame.ShowUnlockTime);
                Assert.AreEqual("Frame header", imported.Frame.HeaderTexts.UnlockHeader);
                AssertScratchFile(ScratchOf(tempDir), imported.Frame.BadgeImages.CommonPath, "frame-common-bytes");
                Assert.AreEqual(FrameVignetteStyle.Image, imported.Frame.FrameVignette);
                Assert.AreEqual(400, imported.Frame.ImageKeyframeMs);
                AssertScratchFile(ScratchOf(tempDir), imported.FrameBackgroundImagePath, "frame-background-bytes");
                Assert.IsNull(imported.ToastBackgroundImagePath, "the toast background stays out of a frame package");
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public void ExportSurfacePackage_NoImages_StillWritesPackageAndRoundTrips()
        {
            var tempDir = CreateTempDirectory();
            try
            {
                var store = CreateStore(tempDir, out _);

                var style = NotificationStyleSettings.CreateDefault();
                style.Toast.ShowHeader = false;
                style.Toast.HeaderTexts.UnlockHeader = "Zip Unlock!";

                var filePath = Path.Combine(tempDir, "share.panotif");
                store.ExportSurfacePackage(isFrame: false, style, filePath);

                var contents = store.InspectPackage(filePath);
                Assert.IsTrue(contents.HasStyle);
                Assert.IsTrue(contents.HasToastStyle);
                Assert.IsFalse(contents.HasFrameStyle);

                var imported = Read(store, tempDir, filePath);
                Assert.IsFalse(imported.Toast.ShowHeader);
                Assert.AreEqual("Zip Unlock!", imported.Toast.HeaderTexts.UnlockHeader);
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public void ExportSurfacePackage_Toast_RoundTripsMotionFields()
        {
            var tempDir = CreateTempDirectory();
            try
            {
                var store = CreateStore(tempDir, out _);

                var style = NotificationStyleSettings.CreateDefault();
                style.Toast.EntranceMotion = ToastMotion.Zoom;
                style.Toast.ExitMotion = ToastMotion.Fade;
                style.Toast.MotionFeel = ToastMotionFeel.Smooth;
                style.Toast.MotionSpeed = ToastMotionSpeed.Relaxed;
                style.Toast.Position = ToastScreenCorner.BottomCenter;

                var filePath = Path.Combine(tempDir, "motion.panotif");
                store.ExportSurfacePackage(isFrame: false, style, filePath);

                var imported = Read(store, tempDir, filePath);
                Assert.AreEqual(ToastScreenCorner.BottomCenter, imported.Toast.Position);
                Assert.AreEqual(ToastMotion.Zoom, imported.Toast.EntranceMotion);
                Assert.AreEqual(ToastMotion.Fade, imported.Toast.ExitMotion);
                Assert.AreEqual(ToastMotionFeel.Smooth, imported.Toast.MotionFeel);
                Assert.AreEqual(ToastMotionSpeed.Relaxed, imported.Toast.MotionSpeed);
                Assert.IsTrue(imported.Toast.HasCustomMotion);
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public void ExportSurfacePackage_Toast_RoundTripsBlurFields()
        {
            var tempDir = CreateTempDirectory();
            try
            {
                var store = CreateStore(tempDir, out _);

                var style = NotificationStyleSettings.CreateDefault();
                style.Toast.BlurBackground = true;
                style.Toast.BlurStrength = 80;
                style.Toast.BlurTintOpacity = 25;

                var filePath = Path.Combine(tempDir, "blur.panotif");
                store.ExportSurfacePackage(isFrame: false, style, filePath);

                var imported = Read(store, tempDir, filePath);
                Assert.IsTrue(imported.Toast.BlurBackground);
                Assert.AreEqual(80, imported.Toast.BlurStrength);
                Assert.AreEqual(25, imported.Toast.BlurTintOpacity);
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public void ExportSurfacePackage_Toast_UnsetMotionStaysUnset()
        {
            var tempDir = CreateTempDirectory();
            try
            {
                var store = CreateStore(tempDir, out _);

                var filePath = Path.Combine(tempDir, "plain.panotif");
                store.ExportSurfacePackage(isFrame: false, NotificationStyleSettings.CreateDefault(), filePath);

                var imported = Read(store, tempDir, filePath);
                Assert.IsNull(imported.Toast.EntranceMotion);
                Assert.IsNull(imported.Toast.ExitMotion);
                Assert.IsNull(imported.Toast.MotionFeel);
                Assert.IsNull(imported.Toast.MotionSpeed);
                Assert.IsNull(imported.Toast.Position);
                Assert.IsFalse(imported.Toast.HasCustomMotion);
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public void ExportPackage_WithTemplates_InspectAndReadRoundTrip()
        {
            var tempDir = CreateTempDirectory();
            try
            {
                var store = CreateStore(tempDir, out _);
                var style = NotificationStyleSettings.CreateDefault();

                const string toastXaml = "<ResourceDictionary xmlns=\"toast\"><!--toast--></ResourceDictionary>";
                const string frameXaml = "<ResourceDictionary xmlns=\"frame\"><!--frame--></ResourceDictionary>";

                var withBoth = Path.Combine(tempDir, "both.pastyle.zip");
                store.ExportLegacyBothSurfacesPackage(style, withBoth, toastXaml, frameXaml);

                using (var archive = ZipFile.OpenRead(withBoth))
                {
                    var names = archive.Entries.Select(entry => entry.FullName).ToList();
                    CollectionAssert.Contains(names, NotificationStylePortableStore.ToastTemplateEntryName);
                    CollectionAssert.Contains(names, NotificationStylePortableStore.FrameTemplateEntryName);
                }

                var contents = store.InspectPackage(withBoth);
                Assert.IsTrue(contents.HasStyle);
                Assert.IsTrue(contents.HasToastTemplate);
                Assert.IsTrue(contents.HasFrameTemplate);
                Assert.AreEqual(toastXaml, store.ReadTemplateXaml(withBoth, isFrame: false));
                Assert.AreEqual(frameXaml, store.ReadTemplateXaml(withBoth, isFrame: true));

                // Toast-only package: the frame template is absent.
                var toastOnly = Path.Combine(tempDir, "toast.pastyle.zip");
                store.ExportLegacyBothSurfacesPackage(style, toastOnly, toastTemplateXaml: toastXaml, frameTemplateXaml: null);
                var toastOnlyContents = store.InspectPackage(toastOnly);
                Assert.IsTrue(toastOnlyContents.HasToastTemplate);
                Assert.IsFalse(toastOnlyContents.HasFrameTemplate);
                Assert.IsNull(store.ReadTemplateXaml(toastOnly, isFrame: true));

                // No templates (existing overload path): both absent.
                var styleOnly = Path.Combine(tempDir, "styleonly.pastyle.zip");
                store.ExportLegacyBothSurfacesPackage(style, styleOnly);
                var styleOnlyContents = store.InspectPackage(styleOnly);
                Assert.IsFalse(styleOnlyContents.HasToastTemplate);
                Assert.IsFalse(styleOnlyContents.HasFrameTemplate);
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public void ExportPackage_ToBarePastylePath_RoundTripsImageFreeStyle()
        {
            var tempDir = CreateTempDirectory();
            try
            {
                var store = CreateStore(tempDir, out _);

                var style = NotificationStyleSettings.CreateDefault();
                style.Toast.ShowHeader = false;
                style.Toast.TitleFontSize = 22;
                style.Toast.HeaderTexts.CompletionHeader = "Done!";

                var filePath = Path.Combine(tempDir, "share.pastyle");
                store.ExportLegacyBothSurfacesPackage(style, filePath);

                var imported = Read(store, tempDir, filePath);

                Assert.IsFalse(imported.Toast.ShowHeader);
                Assert.AreEqual(22d, imported.Toast.TitleFontSize);
                Assert.AreEqual("Done!", imported.Toast.HeaderTexts.CompletionHeader);
                Assert.IsNull(imported.ToastBackgroundImagePath);
                Assert.IsNull(imported.Toast.BadgeImages.CommonPath);
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public void ReadForPreview_LegacyBothSurfacesPackage_ExtractsTheBackground()
        {
            var tempDir = CreateTempDirectory();
            try
            {
                var store = CreateStore(tempDir, out _);
                var source = Path.Combine(tempDir, "background.png");
                WritePlaceholderFile(source, "legacy-background");

                var style = NotificationStyleSettings.CreateDefault();
                style.ToastBackgroundImagePath = source;
                var packagePath = Path.Combine(tempDir, "game-style.pastyle.zip");
                store.ExportLegacyBothSurfacesPackage(style, packagePath);

                var preview = store.ReadForPreview(packagePath, ScratchOf(tempDir));

                Assert.IsTrue(preview.Contents.HasToastStyle);
                Assert.IsTrue(preview.Contents.HasFrameStyle);
                AssertScratchFile(ScratchOf(tempDir), preview.Style.ToastBackgroundImagePath, "legacy-background");
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
        public void ReadForPreview_ForeignKind_Throws()
        {
            var tempDir = CreateTempDirectory();
            try
            {
                var store = CreateStore(tempDir, out _);

                var filePath = Path.Combine(tempDir, "foreign.pastyle");
                using (var archive = ZipFile.Open(filePath, ZipArchiveMode.Create))
                {
                    var manifest = archive.CreateEntry(NotificationStylePortableStore.ManifestEntryName);
                    using (var writer = new StreamWriter(manifest.Open()))
                    {
                        writer.Write("{\"Kind\":\"PlayniteAchievements.CustomData\",\"Version\":1,\"Style\":{}}");
                    }
                }

                Assert.ThrowsException<InvalidOperationException>(() => store.ReadForPreview(filePath, ScratchOf(tempDir)));
                Assert.ThrowsException<InvalidOperationException>(() => store.InspectPackage(filePath));
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public void ReadForPreview_LegacyPackageTraversalEntry_Throws()
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

                Assert.ThrowsException<InvalidOperationException>(() => store.ReadForPreview(packagePath, ScratchOf(tempDir)));
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public void ReadForPreview_ExtractsSlotImagesToScratch_AndReturnsTemplateXaml()
        {
            var tempDir = CreateTempDirectory();
            try
            {
                var store = CreateStore(tempDir, out _);

                var sourceDir = Path.Combine(tempDir, "src");
                Directory.CreateDirectory(sourceDir);
                var backgroundSource = Path.Combine(sourceDir, "bg.png");
                var capstoneBadge = Path.Combine(sourceDir, "capstone.png");
                WritePlaceholderFile(backgroundSource, "background-bytes");
                WritePlaceholderFile(capstoneBadge, "capstone-bytes");

                var style = NotificationStyleSettings.CreateDefault();
                style.Toast.HeaderTexts.UnlockHeader = "Preview header";
                style.Toast.ImageKeyframeMs = 250;
                style.ToastBackgroundImagePath = backgroundSource;
                style.FrameBackgroundImagePath = capstoneBadge;
                var capstone = style.EnableKindStyle(NotificationKind.Capstone);
                capstone.Toast.BadgeImages.CommonPath = capstoneBadge;

                const string toastXaml = "<ResourceDictionary xmlns=\"toast\"><!--toast--></ResourceDictionary>";
                var packagePath = Path.Combine(tempDir, "pack.panotif");
                store.ExportSurfacePackage(isFrame: false, style, packagePath, toastXaml);

                var scratch = Path.Combine(tempDir, "scratch");
                var preview = store.ReadForPreview(packagePath, scratch);

                Assert.AreEqual("Preview header", preview.Style.Toast.HeaderTexts.UnlockHeader);
                Assert.AreEqual(250, preview.Style.Toast.ImageKeyframeMs);
                AssertScratchFile(scratch, preview.Style.ToastBackgroundImagePath, "background-bytes");
                Assert.IsNull(preview.Style.Toast.BadgeImages.CommonPath, "a slot with no bundled image stays empty");
                Assert.IsNull(preview.Style.FrameBackgroundImagePath, "the frame background stays out of a notification package");

                var previewCapstone = preview.Style.ResolveKind(NotificationKind.Capstone);
                AssertScratchFile(scratch, previewCapstone.Toast.BadgeImages.CommonPath, "capstone-bytes");

                Assert.IsTrue(preview.Contents.HasStyle);
                Assert.IsTrue(preview.Contents.HasToastStyle);
                Assert.IsFalse(preview.Contents.HasFrameStyle);
                Assert.IsTrue(preview.Contents.HasToastTemplate);
                Assert.IsFalse(preview.Contents.HasFrameTemplate);
                Assert.AreEqual(toastXaml, preview.ToastTemplateXaml);
                Assert.IsNull(preview.FrameTemplateXaml);

                Assert.AreEqual(
                    0,
                    Directory.GetDirectories(tempDir, "notification_images", SearchOption.AllDirectories).Length,
                    "a preview read writes nothing to managed image storage");
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public void ReadForPreview_TraversalEntry_Throws()
        {
            var tempDir = CreateTempDirectory();
            try
            {
                var store = CreateStore(tempDir, out _);

                var packagePath = Path.Combine(tempDir, "evil.panotif");
                using (var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create))
                {
                    var manifest = archive.CreateEntry(NotificationStylePortableStore.ManifestEntryName);
                    using (var writer = new StreamWriter(manifest.Open()))
                    {
                        writer.Write("{\"Kind\":\"" + NotificationStylePortableFile.NotificationStyleKind +
                                     "\",\"Version\":3,\"Style\":{}}");
                    }

                    var evil = archive.CreateEntry("images/background./../secret.png");
                    using (var writer = new StreamWriter(evil.Open()))
                    {
                        writer.Write("payload");
                    }
                }

                var scratch = Path.Combine(tempDir, "scratch");
                Assert.ThrowsException<InvalidOperationException>(() => store.ReadForPreview(packagePath, scratch));
                Assert.IsFalse(File.Exists(Path.Combine(tempDir, "secret.png")));
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        private static void AssertScratchFile(string scratch, string path, string expectedContent)
        {
            Assert.IsNotNull(path);
            Assert.IsTrue(Path.IsPathRooted(path), path);
            Assert.IsTrue(
                Path.GetFullPath(path).StartsWith(
                    Path.GetFullPath(scratch).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase),
                path);
            Assert.IsTrue(File.Exists(path), path);
            Assert.AreEqual(expectedContent, File.ReadAllText(path));
        }

        private static NotificationStylePortableStore CreateStore(string tempDir, out NotificationImageStore imageStore)
        {
            var diskImageService = new DiskImageService(logger: null, cacheRoot: tempDir);
            imageStore = new NotificationImageStore(diskImageService, logger: null);
            return new NotificationStylePortableStore();
        }

        private static string ScratchOf(string tempDir) => Path.Combine(tempDir, "scratch");

        /// <summary>The style a package carries, with its images extracted to the test's scratch folder.</summary>
        private static NotificationStyleSettings Read(NotificationStylePortableStore store, string tempDir, string packagePath)
        {
            return store.ReadForPreview(packagePath, ScratchOf(tempDir)).Style;
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

        internal static void WriteBmpFile(string path)
        {
            var pixels = System.Windows.Media.Imaging.BitmapSource.Create(
                2, 2, 96, 96, System.Windows.Media.PixelFormats.Bgr32, null, new byte[16], 8);
            var encoder = new System.Windows.Media.Imaging.BmpBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(pixels));
            using (var stream = File.Create(path))
            {
                encoder.Save(stream);
            }
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
