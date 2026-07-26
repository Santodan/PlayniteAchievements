using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Providers.RetroAchievements;

namespace PlayniteAchievements.Tests.Providers
{
    [TestClass]
    public class RetroAchievementsRarityCalculatorTests
    {
        [TestMethod]
        public void ResolveTotalPlayers_PrefersTotalThenCasualThenHardcore()
        {
            Assert.AreEqual(100, RetroAchievementsRarityCalculator.ResolveTotalPlayers(100, 80, 40));
            Assert.AreEqual(80, RetroAchievementsRarityCalculator.ResolveTotalPlayers(0, 80, 40));
            Assert.AreEqual(40, RetroAchievementsRarityCalculator.ResolveTotalPlayers(0, 0, 40));
            Assert.AreEqual(0, RetroAchievementsRarityCalculator.ResolveTotalPlayers(0, 0, 0));
        }

        [TestMethod]
        public void ComputePercent_HardcoreUnlock_UsesHardcoreOverTotalPlayers()
        {
            // 25 hardcore unlocks out of 100 total players -> 25%, regardless of the global setting.
            var pct = RetroAchievementsRarityCalculator.ComputePercent(
                numAwarded: 60,
                numAwardedHardcore: 25,
                totalPlayers: 100,
                earnedInHardcore: true,
                earnedSoftcore: false,
                useHardcoreRarityForLocked: false);

            Assert.AreEqual(25.0, pct.Value, 1e-9);
        }

        [TestMethod]
        public void ComputePercent_SoftcoreUnlock_UsesCasualOverTotalPlayers()
        {
            // NumAwarded already includes hardcore earners: 60 / 100 = 60%.
            var pct = RetroAchievementsRarityCalculator.ComputePercent(
                numAwarded: 60,
                numAwardedHardcore: 25,
                totalPlayers: 100,
                earnedInHardcore: false,
                earnedSoftcore: true,
                useHardcoreRarityForLocked: true);

            Assert.AreEqual(60.0, pct.Value, 1e-9);
        }

        [TestMethod]
        public void ComputePercent_Locked_FollowsGlobalCasualSetting()
        {
            var pct = RetroAchievementsRarityCalculator.ComputePercent(
                numAwarded: 60,
                numAwardedHardcore: 25,
                totalPlayers: 100,
                earnedInHardcore: false,
                earnedSoftcore: false,
                useHardcoreRarityForLocked: false);

            Assert.AreEqual(60.0, pct.Value, 1e-9);
        }

        [TestMethod]
        public void ComputePercent_Locked_FollowsGlobalHardcoreSetting()
        {
            var pct = RetroAchievementsRarityCalculator.ComputePercent(
                numAwarded: 60,
                numAwardedHardcore: 25,
                totalPlayers: 100,
                earnedInHardcore: false,
                earnedSoftcore: false,
                useHardcoreRarityForLocked: true);

            Assert.AreEqual(25.0, pct.Value, 1e-9);
        }

        [TestMethod]
        public void ComputePercent_ReturnsNull_WhenNoPlayersOrNoAwards()
        {
            Assert.IsNull(RetroAchievementsRarityCalculator.ComputePercent(60, 25, 0, false, true, false));
            Assert.IsNull(RetroAchievementsRarityCalculator.ComputePercent(0, 25, 100, false, true, false));
            Assert.IsNull(RetroAchievementsRarityCalculator.ComputePercent(60, 0, 100, true, false, false));
        }
    }
}
