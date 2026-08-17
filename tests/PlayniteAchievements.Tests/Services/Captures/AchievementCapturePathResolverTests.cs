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
        private CaptureTestDirectory _captures;
        private PersistedSettings _settings;

        [TestInitialize]
        public void Setup()
        {
            _captures = new CaptureTestDirectory();
            _settings = _captures.Settings;
            AchievementCapturePathResolver.CaptureLibraryAccessor = null;
        }

        [TestCleanup]
        public void Cleanup()
        {
            AchievementCapturePathResolver.CaptureLibraryAccessor = null;
            _captures.Dispose();
        }

        private CaptureLibraryService CreateService() => _captures.CreateService();

        private string WriteCapture(string gameName, string fileName) =>
            _captures.WriteCapture(gameName, fileName);

        /// <summary>
        /// Writes a capture using the real writer path builder, so the test pins writer/reader
        /// agreement on sanitization instead of hardcoding an already-sanitized filename.
        /// </summary>
        private string WriteCaptureAsWriter(
            string gameName,
            string achievementDisplayName,
            int number,
            int total,
            string variantSuffix,
            string extension = ".png")
        {
            var built = UnlockScreenshotService.BuildRelativePath(
                providerKey: null,
                gameName: gameName,
                achievementName: achievementDisplayName,
                number: number,
                total: total,
                variantSuffix: variantSuffix,
                extension: extension);
            return _captures.WriteCapture(gameName, built.FileName);
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
        [DataRow("Hero: Rise of the Wolf", DisplayName = "colon")]
        [DataRow(@"What?! Why\ How/ When*", DisplayName = "wildcards and separators")]
        [DataRow("Dungeon <Master> | \"Quoted\"", DisplayName = "angle brackets, pipe, quotes")]
        [DataRow("100% Done — 5,000 pts (finally!)", DisplayName = "punctuation kept verbatim")]
        [DataRow("Trailing dots...", DisplayName = "trailing dots trimmed")]
        [DataRow("COM1", DisplayName = "reserved device name")]
        public void ResolvePaths_RoundTripsNamesWithFilesystemSymbols(string achievementDisplayName)
        {
            // Written through the real path builder, then looked up by the RAW display name: the
            // reader must apply the writer's sanitization, not assume the name is already safe.
            var clean = WriteCaptureAsWriter(
                "Portal",
                achievementDisplayName,
                number: 7,
                total: 30,
                variantSuffix: _settings.UnlockScreenshotSuffixClean);
            var video = WriteCaptureAsWriter(
                "Portal",
                achievementDisplayName,
                number: 7,
                total: 30,
                variantSuffix: null,
                extension: ".mp4");
            var set = CreateService().ScanGame("Portal");

            var stamp = AchievementCapturePathResolver.ResolvePaths(set, achievementDisplayName);

            Assert.AreEqual(clean, stamp.Clean, "Clean capture must resolve from the unsanitized display name.");
            Assert.AreEqual(video, stamp.Video, "Video capture must resolve from the unsanitized display name.");
        }

        [TestMethod]
        public void ResolvePaths_RoundTripsNamesLongerThanTheStemCap()
        {
            // The writer caps the stem at 96 chars; the reader must cap identically or long
            // achievement names would never match their own files.
            var longName = new string('a', 80) + ": " + new string('b', 80);
            var clean = WriteCaptureAsWriter(
                "Portal",
                longName,
                number: 1,
                total: 10,
                variantSuffix: _settings.UnlockScreenshotSuffixClean);
            var set = CreateService().ScanGame("Portal");

            var stamp = AchievementCapturePathResolver.ResolvePaths(set, longName);

            Assert.AreEqual(clean, stamp.Clean);
        }

        [TestMethod]
        public void ResolvePaths_TreatsNamesThatSanitizeIdentically_AsTheSameAchievement()
        {
            // Documented consequence of the writer's naming: the on-disk stem is the sanitized
            // display name, so two names differing only in stripped symbols share capture files.
            var clean = WriteCaptureAsWriter(
                "Portal",
                "Ready: Set",
                number: 3,
                total: 10,
                variantSuffix: _settings.UnlockScreenshotSuffixClean);
            var set = CreateService().ScanGame("Portal");

            Assert.AreEqual(clean, AchievementCapturePathResolver.ResolvePaths(set, "Ready: Set").Clean);
            Assert.AreEqual(clean, AchievementCapturePathResolver.ResolvePaths(set, "Ready_ Set").Clean);
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
