using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services.Achievements;

namespace PlayniteAchievements.Services.Tests
{
    /// <summary>
    /// Covers who may mark an achievement unlocked. This is the guard that keeps a real provider's
    /// unlocked counts and completion out of reach of a context menu, so every branch is pinned.
    /// </summary>
    [TestClass]
    public class UserOwnedUnlockRulesTests
    {
        [TestMethod]
        public void AProviderAchievement_IsNeverUnlockable()
        {
            // Editing it would move unlocked counts and completion, and read as a real unlock to
            // the in-game monitor.
            Assert.IsFalse(UserOwnedUnlockRules.CanUserUnlock(
                isUnlocked: false,
                isCustomAchievement: false,
                gameHasManualLink: false));
        }

        [TestMethod]
        public void AnAuthoredAchievement_IsUnlockable()
        {
            Assert.IsTrue(UserOwnedUnlockRules.CanUserUnlock(
                isUnlocked: false,
                isCustomAchievement: true,
                gameHasManualLink: false));
        }

        [TestMethod]
        public void AnyAchievementOfAManuallyTrackedGame_IsUnlockable()
        {
            // Recording unlocks by hand is the whole of manual tracking.
            Assert.IsTrue(UserOwnedUnlockRules.CanUserUnlock(
                isUnlocked: false,
                isCustomAchievement: false,
                gameHasManualLink: true));
        }

        [TestMethod]
        public void AnAlreadyUnlockedAchievement_IsNotOffered()
        {
            Assert.IsFalse(UserOwnedUnlockRules.CanUserUnlock(
                isUnlocked: true,
                isCustomAchievement: true,
                gameHasManualLink: true));
        }
    }
}
