using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Captures;

namespace PlayniteAchievements.Tests.Services
{
    [TestClass]
    public class CaptureLibraryShowcaseTests
    {
        [TestMethod]
        public void GetScreenshots_UsesConfiguredParserAndIgnoresVideosAndCorruptImages()
        {
            var directory = Path.Combine(
                Path.GetTempPath(),
                "PlayniteAchievements.Tests",
                Guid.NewGuid().ToString("N"));
            var gameDirectory = Path.Combine(directory, "Example Game");
            Directory.CreateDirectory(gameDirectory);
            try
            {
                var image = Convert.FromBase64String(
                    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
                File.WriteAllBytes(Path.Combine(gameDirectory, "001_First Win_clean.png"), image);
                File.WriteAllBytes(Path.Combine(gameDirectory, "002_Second Win_framed.png"), image);
                File.WriteAllText(Path.Combine(gameDirectory, "003_Broken_notification.png"), "not an image");
                File.WriteAllBytes(Path.Combine(gameDirectory, "004_Recorded Win.mp4"), new byte[] { 1 });

                var settings = new PersistedSettings
                {
                    UnlockScreenshotDirectory = directory,
                    UnlockRecordingDirectory = directory
                };
                using (var library = new CaptureLibraryService(() => settings, null))
                {
                    var all = library.GetScreenshots();
                    Assert.AreEqual(2, all.Count);
                    Assert.IsTrue(all.All(item => !item.IsVideo));
                    Assert.AreEqual(1, library.GetScreenshots(CaptureVariant.Clean).Count);
                    Assert.AreEqual(1, library.GetScreenshots(CaptureVariant.Framed).Count);
                    Assert.AreEqual(0, library.GetScreenshots(CaptureVariant.Notification).Count);
                }
            }
            finally
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
        }

        [TestMethod]
        public void GetScreenshots_RefreshesWhenCaptureConfigurationChanges()
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "PlayniteAchievements.Tests",
                Guid.NewGuid().ToString("N"));
            var firstDirectory = Path.Combine(root, "First");
            var secondDirectory = Path.Combine(root, "Second");
            Directory.CreateDirectory(Path.Combine(firstDirectory, "First Game"));
            Directory.CreateDirectory(Path.Combine(secondDirectory, "Second Game"));
            try
            {
                var image = Convert.FromBase64String(
                    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
                File.WriteAllBytes(
                    Path.Combine(firstDirectory, "First Game", "001_First Win_clean.png"),
                    image);
                File.WriteAllBytes(
                    Path.Combine(secondDirectory, "Second Game", "001_Second Win_custom.png"),
                    image);

                var settings = new PersistedSettings
                {
                    UnlockScreenshotDirectory = firstDirectory,
                    UnlockRecordingDirectory = firstDirectory
                };
                using (var library = new CaptureLibraryService(() => settings, null))
                {
                    Assert.AreEqual(1, library.GetScreenshots(CaptureVariant.Clean).Count);

                    settings.UnlockScreenshotDirectory = secondDirectory;
                    settings.UnlockRecordingDirectory = secondDirectory;
                    settings.UnlockScreenshotSuffixClean = "custom";

                    var refreshed = library.GetScreenshots(CaptureVariant.Clean);
                    Assert.AreEqual(1, refreshed.Count);
                    StringAssert.Contains(refreshed[0].FilePath, "Second Game");
                }
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
        }

        [TestMethod]
        public void GetScreenshots_ForceRefreshRevalidatesPreviouslyCorruptImages()
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "PlayniteAchievements.Tests",
                Guid.NewGuid().ToString("N"));
            var gameDirectory = Path.Combine(root, "Example Game");
            Directory.CreateDirectory(gameDirectory);
            var path = Path.Combine(gameDirectory, "001_Repaired_clean.png");
            try
            {
                File.WriteAllText(path, "not an image");
                var settings = new PersistedSettings
                {
                    UnlockScreenshotDirectory = root,
                    UnlockRecordingDirectory = root
                };
                using (var library = new CaptureLibraryService(() => settings, null))
                {
                    Assert.AreEqual(0, library.GetScreenshots().Count);

                    File.WriteAllBytes(
                        path,
                        Convert.FromBase64String(
                            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="));

                    Assert.AreEqual(1, library.GetScreenshots(forceRefresh: true).Count);
                }
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
        }

        [TestMethod]
        public void GetScreenshots_ExcludesReservedTestCapturesFromEveryConfiguredRoot()
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "PlayniteAchievements.Tests",
                Guid.NewGuid().ToString("N"));
            var gameDirectory = Path.Combine(root, "Real Game");
            var testRoot = Path.Combine(root, "Test");
            var testGameDirectory = Path.Combine(testRoot, "Test Game");
            Directory.CreateDirectory(gameDirectory);
            Directory.CreateDirectory(testGameDirectory);
            try
            {
                var image = Convert.FromBase64String(
                    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
                File.WriteAllBytes(
                    Path.Combine(gameDirectory, "001_Real Win_clean.png"),
                    image);
                File.WriteAllBytes(
                    Path.Combine(testGameDirectory, "001_Test Win_notification.png"),
                    image);

                var settings = new PersistedSettings
                {
                    UnlockScreenshotDirectory = root,
                    UnlockRecordingDirectory = testRoot
                };
                using (var library = new CaptureLibraryService(() => settings, null))
                {
                    var screenshots = library.GetScreenshots();
                    Assert.AreEqual(1, screenshots.Count);
                    StringAssert.Contains(screenshots[0].FilePath, "Real Game");
                    Assert.AreEqual(0, library.GetScreenshots(CaptureVariant.Notification).Count);
                }
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
        }
    }
}
