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
        public void TryParse_Mp4WithVariantSuffix_ClassifiesTheClipVariant()
        {
            CaptureFileNameParser.TryParse(@"C:\Shots\Game\007_First Win_clean.mp4", DefaultResolver(), out var clean);
            CaptureFileNameParser.TryParse(@"C:\Shots\Game\007_First Win_notification.mp4", DefaultResolver(), out var withToast);
            CaptureFileNameParser.TryParse(@"C:\Shots\Game\007_First Win_framed (2).mp4", DefaultResolver(), out var framed);

            Assert.AreEqual(CaptureVariant.CleanVideo, clean.Variant);
            Assert.AreEqual(CaptureVariant.Video, withToast.Variant);
            Assert.AreEqual(CaptureVariant.FramedVideo, framed.Variant);
            Assert.AreEqual(2, framed.DedupCounter);
            Assert.IsTrue(clean.IsVideo && withToast.IsVideo && framed.IsVideo);
            Assert.AreEqual("First Win", clean.AchievementStem);
            Assert.AreEqual("First Win", framed.AchievementStem);
        }

        [TestMethod]
        public void TryParse_Mp4WithBlankSuffixOwner_ClassifiesThatVariant()
        {
            var resolver = CaptureFileNameParser.CreateResolver("clean", "", "framed");
            CaptureFileNameParser.TryParse(@"C:\Shots\Game\007_First Win.mp4", resolver, out var item);

            Assert.AreEqual(CaptureVariant.Video, item.Variant);
            Assert.AreEqual("First Win", item.AchievementStem);
        }

        [TestMethod]
        public void VariantPairs_MapBothWays()
        {
            Assert.AreEqual(CaptureVariant.CleanVideo, CaptureVariant.Clean.ToVideo());
            Assert.AreEqual(CaptureVariant.Video, CaptureVariant.Notification.ToVideo());
            Assert.AreEqual(CaptureVariant.FramedVideo, CaptureVariant.Framed.ToVideo());
            Assert.AreEqual(CaptureVariant.Framed, CaptureVariant.FramedVideo.ToImage());
            Assert.AreEqual(CaptureVariant.Clean, CaptureVariant.Clean.ToImage());
            Assert.IsFalse(CaptureVariant.Framed.IsVideoVariant());
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

        [DataTestMethod]
        [DataRow(@"C:\Shots\Game\20240101123456_1.png")]
        [DataRow(@"C:\Shots\Game\Counter-strike 2 Screenshot 2024.01.01 - 12.34.56.78.png")]
        [DataRow(@"C:\Shots\Game\First Win_clean.png")]
        [DataRow(@"C:\Shots\Game\12_First Win.png")]
        [DataRow(@"C:\Shots\Game\007_.png")]
        [DataRow(@"C:\Shots\Game\007_ (2).mp4")]
        public void TryParse_ForeignScreenshotName_IsRejected(string path)
        {
            Assert.IsFalse(CaptureFileNameParser.HasCaptureSignature(path));
            Assert.IsFalse(CaptureFileNameParser.TryParse(path, DefaultResolver(), out var item));
            Assert.IsNull(item);
        }

        [DataTestMethod]
        [DataRow(@"C:\Shots\Game\007_First Win_toast.png", 7, "First Win_toast")]
        [DataRow(@"C:\Shots\Game\0012_First Win_clean (2).png", 12, "First Win")]
        [DataRow(@"C:\Shots\Game\10250_First Win.mp4", 10250, "First Win")]
        public void TryParse_CaptureShape_IsAcceptedWhateverTheSuffix(string path, int number, string stem)
        {
            Assert.IsTrue(CaptureFileNameParser.HasCaptureSignature(path));
            Assert.IsTrue(CaptureFileNameParser.TryParse(path, DefaultResolver(), out var item));
            Assert.AreEqual(number, item.Number);
            Assert.AreEqual(stem, item.AchievementStem);
        }
    }
}
