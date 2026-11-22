using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Providers.Local;
using PlayniteAchievements.Services.UI;

namespace PlayniteAchievements.Tests.Services.UI
{
    [TestClass]
    public class SantodanMemoriesCapturePolicyTests
    {
        private static PersistedSettings Upstream(double delaySeconds = 2, bool dual = true) =>
            new PersistedSettings
            {
                CaptureDelaySeconds = delaySeconds,
                CaptureAtUnlockAndAfterDelay = dual
            };

        private static LocalSettings Memories(bool screenshots, int delayMs = 1500) =>
            new LocalSettings
            {
                EnableUnlockScreenshots = screenshots,
                ScreenshotDelayMilliseconds = delayMs
            };

        [TestMethod]
        public void MemoriesOff_UsesUpstreamSettings()
        {
            var memories = Memories(screenshots: false);

            Assert.AreEqual(2, SantodanMemoriesCapturePolicy.ResolveCaptureDelaySeconds(null, memories, Upstream()));
            Assert.IsTrue(SantodanMemoriesCapturePolicy.ResolveCaptureAtUnlockAndAfterDelay(null, memories, Upstream()));
        }

        [TestMethod]
        public void NoMemoriesSettings_UsesUpstreamSettings()
        {
            Assert.AreEqual(2, SantodanMemoriesCapturePolicy.ResolveCaptureDelaySeconds(null, null, Upstream()));
            Assert.IsTrue(SantodanMemoriesCapturePolicy.ResolveCaptureAtUnlockAndAfterDelay(null, null, Upstream()));
        }

        [TestMethod]
        public void MemoriesOn_UsesMemoriesDelayAndDisablesDualCapture()
        {
            var memories = Memories(screenshots: true, delayMs: 1500);

            Assert.AreEqual(1.5, SantodanMemoriesCapturePolicy.ResolveCaptureDelaySeconds(null, memories, Upstream()), 1e-9);
            Assert.IsFalse(SantodanMemoriesCapturePolicy.ResolveCaptureAtUnlockAndAfterDelay(null, memories, Upstream()));
        }

        [TestMethod]
        public void MemoriesOn_ZeroDelayCapturesImmediately()
        {
            var memories = Memories(screenshots: true, delayMs: 0);

            Assert.AreEqual(0, SantodanMemoriesCapturePolicy.ResolveCaptureDelaySeconds(null, memories, Upstream()));
        }

        [TestMethod]
        public void GameCaptureOverride_WinsOverMemoriesAndUpstream()
        {
            var game = new GameCaptureOverride
            {
                CaptureDelaySeconds = 4,
                CaptureAtUnlockAndAfterDelay = true
            };
            var memories = Memories(screenshots: true, delayMs: 1500);

            Assert.AreEqual(4, SantodanMemoriesCapturePolicy.ResolveCaptureDelaySeconds(game, memories, Upstream()));
            Assert.IsTrue(SantodanMemoriesCapturePolicy.ResolveCaptureAtUnlockAndAfterDelay(game, memories, Upstream()));
        }
    }
}
