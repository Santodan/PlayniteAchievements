using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services.Captures;

namespace PlayniteAchievements.Services.Captures.Tests
{
    /// <summary>
    /// Covers parsing a capture filename back into its variant, number, and achievement stem — the
    /// inverse of <c>UnlockScreenshotService.BuildRelativePath</c>. Uses the default suffixes
    /// (clean / notification / framed; video has none).
    /// </summary>
    [TestClass]
    public class CaptureFileNameParserTests
    {
        private static CaptureFileNameParser.SuffixResolver DefaultResolver() =>
            CaptureFileNameParser.CreateResolver("clean", "notification", "framed");

        [TestMethod]
        public void TryParse_CleanPng_ClassifiesCleanWithNumberAndStem()
        {
            var ok = CaptureFileNameParser.TryParse(@"C:\Shots\Game\007_First Win_clean.png", DefaultResolver(), out var item);

            Assert.IsTrue(ok);
            Assert.AreEqual(CaptureVariant.Clean, item.Variant);
            Assert.AreEqual(7, item.Number);
            Assert.AreEqual("First Win", item.AchievementStem);
            Assert.IsFalse(item.IsVideo);
        }

        [TestMethod]
        public void TryParse_NotificationPng_ClassifiesNotification()
        {
            CaptureFileNameParser.TryParse(@"C:\Shots\Game\007_First Win_notification.png", DefaultResolver(), out var item);

            Assert.AreEqual(CaptureVariant.Notification, item.Variant);
            Assert.AreEqual("First Win", item.AchievementStem);
        }

        [TestMethod]
        public void TryParse_FramedPng_ClassifiesFramed()
        {
            CaptureFileNameParser.TryParse(@"C:\Shots\Game\007_First Win_framed.png", DefaultResolver(), out var item);

            Assert.AreEqual(CaptureVariant.Framed, item.Variant);
            Assert.AreEqual("First Win", item.AchievementStem);
        }

        [TestMethod]
        public void TryParse_Mp4_ClassifiesVideoWithNoSuffix()
        {
            var ok = CaptureFileNameParser.TryParse(@"C:\Shots\Game\007_First Win.mp4", DefaultResolver(), out var item);

            Assert.IsTrue(ok);
            Assert.AreEqual(CaptureVariant.Video, item.Variant);
            Assert.IsTrue(item.IsVideo);
            Assert.AreEqual("First Win", item.AchievementStem);
        }

        [TestMethod]
        public void TryParse_DedupMarker_IsStrippedAndGroupsWithBaseName()
        {
            CaptureFileNameParser.TryParse(@"C:\Shots\Game\007_First Win_clean (2).png", DefaultResolver(), out var item);

            Assert.AreEqual(CaptureVariant.Clean, item.Variant);
            Assert.AreEqual("First Win", item.AchievementStem);
        }

        [TestMethod]
        public void TryParse_NonCaptureExtension_ReturnsFalse()
        {
            var ok = CaptureFileNameParser.TryParse(@"C:\Shots\Game\notes.txt", DefaultResolver(), out var item);

            Assert.IsFalse(ok);
            Assert.IsNull(item);
        }

        [TestMethod]
        public void TryParse_BlankCleanSuffix_SuffixlessPngIsClean()
        {
            // A blanked clean suffix means the suffix-less .png form is the clean variant.
            var resolver = CaptureFileNameParser.CreateResolver("", "notification", "framed");

            CaptureFileNameParser.TryParse(@"C:\Shots\Game\007_First Win.png", resolver, out var item);

            Assert.AreEqual(CaptureVariant.Clean, item.Variant);
            Assert.AreEqual("First Win", item.AchievementStem);
        }
    }
}
