using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services.Captures;
using PlayniteAchievements.Services.UI;

namespace PlayniteAchievements.Services.Captures.Tests
{
    /// <summary>
    /// Covers the capture-to-achievement join the Screenshot Slideshow's info panel relies on. The
    /// load-bearing property is the round trip: a key built from an achievement must equal the key
    /// built from the capture the writer named for it, via the real writer and the real parser.
    /// </summary>
    [TestClass]
    public class CaptureAchievementIndexTests
    {
        private sealed class Row
        {
            public string Game { get; set; }

            public string Name { get; set; }
        }

        private static CaptureFileNameParser.SuffixResolver DefaultResolver() =>
            CaptureFileNameParser.CreateResolver("clean", "notification", "framed");

        /// <summary>
        /// Writes the capture path the real writer would produce, parses it back, and returns the
        /// index key for the resulting capture.
        /// </summary>
        private static string RoundTripCaptureKey(
            string gameName,
            string achievementName,
            string variantSuffix = "clean")
        {
            var built = UnlockScreenshotService.BuildRelativePath(
                "steam",
                gameName,
                achievementName,
                number: 7,
                total: 100,
                variantSuffix: variantSuffix);
            var fullPath = Path.Combine(@"C:\Shots", built.Folder, built.FileName);

            Assert.IsTrue(
                CaptureFileNameParser.TryParse(fullPath, DefaultResolver(), out var item),
                "the writer's own filename must parse");

            return CaptureAchievementIndex.KeyForCapture(item.FilePath, item.AchievementStem);
        }

        [TestMethod]
        public void BuildKey_MatchesTheKeyOfTheCaptureTheWriterNamedForIt()
        {
            Assert.AreEqual(
                CaptureAchievementIndex.BuildKey("Elden Ring", "Defeat The Beast"),
                RoundTripCaptureKey("Elden Ring", "Defeat The Beast"));
        }

        [TestMethod]
        public void BuildKey_RoundTripsNamesCarryingInvalidFileNameCharacters()
        {
            // Both the game and the achievement carry characters the sanitizers rewrite, which is
            // exactly where an inverted sanitizer would have gone wrong.
            Assert.AreEqual(
                CaptureAchievementIndex.BuildKey(@"Marvel's Spider-Man: Miles?", "50/50 Split: <Done>"),
                RoundTripCaptureKey(@"Marvel's Spider-Man: Miles?", "50/50 Split: <Done>"));
        }

        [TestMethod]
        public void BuildKey_RoundTripsForEveryVariantIncludingVideo()
        {
            var expected = CaptureAchievementIndex.BuildKey("Hades", "Escaped Tartarus");

            Assert.AreEqual(expected, RoundTripCaptureKey("Hades", "Escaped Tartarus", "clean"));
            Assert.AreEqual(expected, RoundTripCaptureKey("Hades", "Escaped Tartarus", "notification"));
            Assert.AreEqual(expected, RoundTripCaptureKey("Hades", "Escaped Tartarus", "framed"));

            // Video clips are written without a variant suffix.
            var video = UnlockScreenshotService.BuildRelativePath(
                "steam",
                "Hades",
                "Escaped Tartarus",
                number: 7,
                total: 100,
                variantSuffix: null,
                extension: ".mp4");
            CaptureFileNameParser.TryParse(
                Path.Combine(@"C:\Shots", video.Folder, video.FileName),
                DefaultResolver(),
                out var item);
            Assert.AreEqual(
                expected,
                CaptureAchievementIndex.KeyForCapture(item.FilePath, item.AchievementStem));
        }

        [TestMethod]
        public void BuildKey_SeparatesGameFromAchievement()
        {
            // Without a separator "AB" + "C" and "A" + "BC" would collide.
            Assert.AreNotEqual(
                CaptureAchievementIndex.BuildKey("AB", "C"),
                CaptureAchievementIndex.BuildKey("A", "BC"));
        }

        [TestMethod]
        public void BuildKey_DistinguishesSameAchievementNameInDifferentGames()
        {
            Assert.AreNotEqual(
                CaptureAchievementIndex.BuildKey("Game One", "Welcome"),
                CaptureAchievementIndex.BuildKey("Game Two", "Welcome"));
        }

        [TestMethod]
        public void Build_IndexesRowsAndResolvesTheMatchingCapture()
        {
            var row = new Row { Game = "Elden Ring", Name = "Defeat The Beast" };
            var index = CaptureAchievementIndex.Build(
                new[] { row, new Row { Game = "Hades", Name = "Escaped Tartarus" } },
                r => r.Game,
                r => r.Name);

            Assert.IsTrue(index.TryGetValue(
                RoundTripCaptureKey("Elden Ring", "Defeat The Beast"),
                out var resolved));
            Assert.AreSame(row, resolved);
        }

        [TestMethod]
        public void Build_FirstRowWinsWhenTwoNamesSanitizeAlike()
        {
            // Both names sanitize to the same stem, and the captures are equally ambiguous.
            var first = new Row { Game = "Game", Name = "A<B" };
            var index = CaptureAchievementIndex.Build(
                new[] { first, new Row { Game = "Game", Name = "A>B" } },
                r => r.Game,
                r => r.Name);

            Assert.AreEqual(1, index.Count);
            Assert.AreSame(first, index[CaptureAchievementIndex.BuildKey("Game", "A<B")]);
        }

        [TestMethod]
        public void Build_IgnoresNullRowsAndToleratesNullInputs()
        {
            var index = CaptureAchievementIndex.Build(
                new[] { null, new Row { Game = "Game", Name = "Win" } },
                r => r?.Game,
                r => r?.Name);

            Assert.AreEqual(1, index.Count);
            Assert.AreEqual(0, CaptureAchievementIndex.Build<Row>(null, r => r.Game, r => r.Name).Count);
        }

        [TestMethod]
        public void KeyForCapture_IsCaseInsensitiveThroughTheIndexComparer()
        {
            var row = new Row { Game = "Elden Ring", Name = "Defeat The Beast" };
            var index = CaptureAchievementIndex.Build(new[] { row }, r => r.Game, r => r.Name);

            Assert.IsTrue(index.ContainsKey(
                CaptureAchievementIndex.KeyForCapture(@"C:\Shots\ELDEN RING\007_DEFEAT THE BEAST.png", "DEFEAT THE BEAST")));
        }

        [TestMethod]
        public void GetCaptureFolderName_ReturnsTheParentFolderAndToleratesJunk()
        {
            Assert.AreEqual(
                "Elden Ring",
                CaptureAchievementIndex.GetCaptureFolderName(@"C:\Shots\Elden Ring\007_Win_clean.png"));
            Assert.AreEqual(string.Empty, CaptureAchievementIndex.GetCaptureFolderName(null));
            Assert.AreEqual(string.Empty, CaptureAchievementIndex.GetCaptureFolderName(string.Empty));
        }
    }
}
