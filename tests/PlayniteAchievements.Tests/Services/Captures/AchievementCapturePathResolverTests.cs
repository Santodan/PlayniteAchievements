using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Captures;
using PlayniteAchievements.Services.UI;

namespace PlayniteAchievements.Services.Tests.Captures
{
    /// <summary>
    /// Covers the per-achievement capture path contract: each of the four variants resolves to
    /// exactly one file (the original wins over " (n)" collision duplicates), absent variants
    /// resolve to null, and a missing capture library clears rather than leaks stale paths.
    /// </summary>
    [TestClass]
    public class AchievementCapturePathResolverTests
    {
        private string _root;
        private PersistedSettings _settings;

        [TestInitialize]
        public void Setup()
        {
            _root = Path.Combine(
                Path.GetTempPath(),
                "PlayAchCapturePathTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _settings = new PersistedSettings
            {
                UnlockScreenshotDirectory = _root,
                UnlockRecordingDirectory = _root
            };
            AchievementCapturePathResolver.CaptureLibraryAccessor = null;
        }

        [TestCleanup]
        public void Cleanup()
        {
            AchievementCapturePathResolver.CaptureLibraryAccessor = null;
            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, recursive: true);
                }
            }
            catch (IOException)
            {
                // A leftover temp folder must never fail a test run.
            }
        }

        private CaptureLibraryService CreateService() =>
            new CaptureLibraryService(() => _settings, null);

        private string WriteCapture(string gameName, string fileName)
        {
            var folder = Path.Combine(_root, UnlockScreenshotService.SanitizeCaptureGameName(gameName));
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, fileName);
            File.WriteAllText(path, "x");
            return path;
        }

        [TestMethod]
        public void ResolvePaths_ResolvesEachVariant_AndNullsTheAbsentOne()
        {
            var clean = WriteCapture("Portal", "001_Cake_clean.png");
            var notification = WriteCapture("Portal", "001_Cake_notification.png");
            var video = WriteCapture("Portal", "001_Cake.mp4");
            var set = CreateService().ScanGame("Portal");

            var stamp = AchievementCapturePathResolver.ResolvePaths(set, "Cake");

            Assert.AreEqual(clean, stamp.Clean);
            Assert.AreEqual(notification, stamp.Notification);
            Assert.IsNull(stamp.Framed, "No framed file was written.");
            Assert.AreEqual(video, stamp.Video);
        }

        [TestMethod]
        public void ResolvePaths_ReturnsAllNulls_ForAnAchievementWithoutCaptures()
        {
            WriteCapture("Portal", "001_Cake_clean.png");
            var set = CreateService().ScanGame("Portal");

            var stamp = AchievementCapturePathResolver.ResolvePaths(set, "Companion Cube");

            Assert.IsNull(stamp.Clean);
            Assert.IsNull(stamp.Notification);
            Assert.IsNull(stamp.Framed);
            Assert.IsNull(stamp.Video);
        }

        [TestMethod]
        public void ResolvePaths_PrefersTheOriginalFile_OverCollisionDuplicates()
        {
            var original = WriteCapture("Portal", "001_Cake_clean.png");
            WriteCapture("Portal", "001_Cake_clean (2).png");
            WriteCapture("Portal", "001_Cake_clean (3).png");
            var set = CreateService().ScanGame("Portal");

            var stamp = AchievementCapturePathResolver.ResolvePaths(set, "Cake");

            Assert.AreEqual(original, stamp.Clean);
        }

        [TestMethod]
        public void ResolvePaths_FallsBackToTheLowestCounter_WhenTheOriginalWasDeleted()
        {
            var second = WriteCapture("Portal", "001_Cake_clean (2).png");
            WriteCapture("Portal", "001_Cake_clean (3).png");
            var set = CreateService().ScanGame("Portal");

            var stamp = AchievementCapturePathResolver.ResolvePaths(set, "Cake");

            Assert.AreEqual(second, stamp.Clean);
        }

        [TestMethod]
        public void ResolvePaths_HonorsCustomAndBlankSuffixSettings()
        {
            // A blank clean suffix owns the suffix-less png form.
            _settings.UnlockScreenshotSuffixClean = string.Empty;
            _settings.UnlockScreenshotSuffixWithToast = "toasty";
            var plain = WriteCapture("Portal", "001_Cake.png");
            var toasty = WriteCapture("Portal", "001_Cake_toasty.png");
            var set = CreateService().ScanGame("Portal");

            var stamp = AchievementCapturePathResolver.ResolvePaths(set, "Cake");

            Assert.AreEqual(plain, stamp.Clean);
            Assert.AreEqual(toasty, stamp.Notification);
        }

        [TestMethod]
        public void ResolveGameSet_ReturnsNull_WithoutAnAccessor()
        {
            WriteCapture("Portal", "001_Cake_clean.png");

            Assert.IsNull(AchievementCapturePathResolver.ResolveGameSet("Portal"));
        }

        [TestMethod]
        public void ResolveGameSet_GatesOnFolderMembership_AndReturnsTheScannedSet()
        {
            WriteCapture("Portal", "001_Cake_clean.png");
            var service = CreateService();
            AchievementCapturePathResolver.CaptureLibraryAccessor = () => service;

            Assert.IsNull(AchievementCapturePathResolver.ResolveGameSet("Braid"));
            var set = AchievementCapturePathResolver.ResolveGameSet("Portal");
            Assert.IsNotNull(set);
            Assert.IsTrue(set.HasAny);
        }

        [TestMethod]
        public void Apply_WithANullSet_ClearsAllFourPaths()
        {
            var achievement = new PlayniteAchievements.Models.Achievements.AchievementDetail
            {
                DisplayName = "Cake",
                CleanCapturePath = "stale-clean",
                NotificationCapturePath = "stale-notification",
                FramedCapturePath = "stale-framed",
                VideoCapturePath = "stale-video"
            };

            AchievementCapturePathResolver.Apply(achievement, null);

            Assert.IsNull(achievement.CleanCapturePath);
            Assert.IsNull(achievement.NotificationCapturePath);
            Assert.IsNull(achievement.FramedCapturePath);
            Assert.IsNull(achievement.VideoCapturePath);
            Assert.IsFalse(achievement.HasAnyCapture);
        }

        [TestMethod]
        public void FindGroup_MatchesStemCaseInsensitively_AndMissesUnknownStems()
        {
            WriteCapture("Portal", "001_Cake_clean.png");
            var set = CreateService().ScanGame("Portal");

            Assert.IsNotNull(set.FindGroup("cake"));
            Assert.IsNull(set.FindGroup("Companion Cube"));
            Assert.IsNull(set.FindGroup(null));
        }
    }
}
