using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Common;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.Tests.ViewModels
{
    [TestClass]
    [DoNotParallelize]
    public class FriendGameSummaryItemCompletionPercentTests
    {
        [TestInitialize]
        public void PinFormattingCulture()
        {
            FormattingCulture.Initialize(() => "english");
        }

        [TestMethod]
        public void FriendCompletionPercent_NearCompleteFloorsToNinetyNine()
        {
            var item = new FriendGameSummaryItem
            {
                TotalAchievements = 200,
                UniqueFriendUnlockedAchievementsCount = 199,
            };

            Assert.AreEqual(99, item.FriendCompletionPercent);
            Assert.AreEqual("99%", item.FriendCompletionText);
        }

        [TestMethod]
        public void FriendCompletionPercent_FullUnlockIsOneHundred()
        {
            var item = new FriendGameSummaryItem
            {
                TotalAchievements = 200,
                UniqueFriendUnlockedAchievementsCount = 200,
            };

            Assert.AreEqual(100, item.FriendCompletionPercent);
            Assert.AreEqual("100%", item.FriendCompletionText);
        }

        [TestMethod]
        public void FriendCompletionPercent_MidpointRoundsAwayFromZero()
        {
            var item = new FriendGameSummaryItem
            {
                TotalAchievements = 8,
                UniqueFriendUnlockedAchievementsCount = 1,
            };

            Assert.AreEqual(13, item.FriendCompletionPercent);
        }
    }
}
