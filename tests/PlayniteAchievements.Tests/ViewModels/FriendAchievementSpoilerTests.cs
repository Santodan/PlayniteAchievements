using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.Tests.ViewModels
{
    [TestClass]
    public class FriendAchievementSpoilerTests
    {
        [DataTestMethod]
        [DataRow(false, false, true, false, DisplayName = "Spoilers hidden, friend unlocked, user has not: obscured")]
        [DataRow(false, false, false, false, DisplayName = "Spoilers hidden, neither unlocked: obscured")]
        [DataRow(false, true, false, true, DisplayName = "Spoilers hidden, user unlocked, friend has not: revealed")]
        [DataRow(false, true, true, true, DisplayName = "Spoilers hidden, both unlocked: revealed")]
        [DataRow(true, false, true, true, DisplayName = "Spoilers shown, friend unlock governs: revealed")]
        [DataRow(true, true, false, false, DisplayName = "Spoilers shown, friend unlock governs: obscured")]
        public void UnlockedForVisibility_AppliesOwnUnlockStateUnlessSpoilersShown(
            bool showFriendSpoilers,
            bool unlockedBySelf,
            bool friendUnlocked,
            bool expected)
        {
            var item = new FriendAchievementDisplayItem
            {
                ShowFriendSpoilers = showFriendSpoilers,
                UnlockedBySelf = unlockedBySelf,
                Unlocked = friendUnlocked
            };

            Assert.AreEqual(expected, item.UnlockedForVisibility);
        }
    }
}
