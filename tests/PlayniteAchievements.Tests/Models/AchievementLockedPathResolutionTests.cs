using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services.Achievements;

namespace PlayniteAchievements.Services.Achievements.Tests
{
    /// <summary>
    /// Covers which locked path gets persisted for an achievement. The result is written back onto
    /// the cached data, so collapsing it onto the unlocked path discards the provider's locked icon
    /// and leaves nothing real to reveal behind a locked cover.
    /// </summary>
    [TestClass]
    public class AchievementLockedPathResolutionTests
    {
        private const string Unlocked = @"C:\icons\unlocked.png";
        private const string ProviderLocked = @"C:\icons\locked.png";
        private const string CustomLocked = @"C:\icons\custom.locked.png";

        [TestMethod]
        public void ExplicitLockedOverrideAlwaysWins()
        {
            var resolved = AchievementIconOverrideHelper.ResolveEffectiveLockedPath(
                Unlocked,
                CustomLocked,
                useSeparateLockedIcons: false,
                hasExplicitLockedIcon: true);

            Assert.AreEqual(CustomLocked, resolved);
        }

        [TestMethod]
        public void SeparateLockedIconsOffCollapsesOntoTheUnlockedPath()
        {
            // The documented default: one icon per achievement, grayscaled when locked.
            var resolved = AchievementIconOverrideHelper.ResolveEffectiveLockedPath(
                Unlocked,
                ProviderLocked,
                useSeparateLockedIcons: false,
                hasExplicitLockedIcon: false);

            Assert.AreEqual(Unlocked, resolved);
        }

        [TestMethod]
        public void SeparateLockedIconsOnKeepsTheProviderLockedPath()
        {
            var resolved = AchievementIconOverrideHelper.ResolveEffectiveLockedPath(
                Unlocked,
                ProviderLocked,
                useSeparateLockedIcons: true,
                hasExplicitLockedIcon: false);

            Assert.AreEqual(ProviderLocked, resolved);
        }

        [TestMethod]
        public void SeparateLockedIconsOnFallsBackToUnlockedWhenNoLockedPathExists()
        {
            var resolved = AchievementIconOverrideHelper.ResolveEffectiveLockedPath(
                Unlocked,
                null,
                useSeparateLockedIcons: true,
                hasExplicitLockedIcon: false);

            Assert.AreEqual(Unlocked, resolved);
        }

        [TestMethod]
        public void BlankExplicitLockedPathDoesNotWin()
        {
            var resolved = AchievementIconOverrideHelper.ResolveEffectiveLockedPath(
                Unlocked,
                "   ",
                useSeparateLockedIcons: false,
                hasExplicitLockedIcon: true);

            Assert.AreEqual(Unlocked, resolved);
        }
    }
}
