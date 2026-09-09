using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Achievements;

namespace PlayniteAchievements.Tests.Models
{
    [TestClass]
    public class AchievementCompletionPercentCalculatorTests
    {
        [TestMethod]
        public void ComputeRoundedPercent_NearCompleteFloorsToNinetyNine()
        {
            Assert.AreEqual(99, AchievementCompletionPercentCalculator.ComputeRoundedPercent(199, 200));
            Assert.AreEqual(99, AchievementCompletionPercentCalculator.ComputeRoundedPercent(999, 1000));
        }

        [TestMethod]
        public void ComputeRoundedPercent_FullUnlockIsOneHundred()
        {
            Assert.AreEqual(100, AchievementCompletionPercentCalculator.ComputeRoundedPercent(200, 200));
            Assert.AreEqual(100, AchievementCompletionPercentCalculator.ComputeRoundedPercent(201, 200));
        }

        [TestMethod]
        public void ComputeRoundedPercent_LowEndStillRoundsMidpointUp()
        {
            Assert.AreEqual(1, AchievementCompletionPercentCalculator.ComputeRoundedPercent(1, 200));
            Assert.AreEqual(99, AchievementCompletionPercentCalculator.ComputeRoundedPercent(197, 200));
            Assert.AreEqual(25, AchievementCompletionPercentCalculator.ComputeRoundedPercent(2, 8));
        }

        [TestMethod]
        public void ComputeRoundedPercent_EmptyOrNothingUnlockedIsZero()
        {
            Assert.AreEqual(0, AchievementCompletionPercentCalculator.ComputeRoundedPercent(0, 200));
            Assert.AreEqual(0, AchievementCompletionPercentCalculator.ComputeRoundedPercent(5, 0));
            Assert.AreEqual(0, AchievementCompletionPercentCalculator.ComputeRoundedPercent(-1, 200));
        }

        [TestMethod]
        public void RoundPercentForDisplay_FloorsBetweenNinetyNineAndOneHundred()
        {
            Assert.AreEqual(99, AchievementCompletionPercentCalculator.RoundPercentForDisplay(99.5));
            Assert.AreEqual(99, AchievementCompletionPercentCalculator.RoundPercentForDisplay(99.01));
            Assert.AreEqual(99, AchievementCompletionPercentCalculator.RoundPercentForDisplay(99.999));
            Assert.AreEqual(100, AchievementCompletionPercentCalculator.RoundPercentForDisplay(100));
            Assert.AreEqual(100, AchievementCompletionPercentCalculator.RoundPercentForDisplay(120));
        }

        [TestMethod]
        public void RoundPercentForDisplay_OtherwiseRoundsMidpointAwayFromZero()
        {
            Assert.AreEqual(99, AchievementCompletionPercentCalculator.RoundPercentForDisplay(98.5));
            Assert.AreEqual(13, AchievementCompletionPercentCalculator.RoundPercentForDisplay(12.5));
            Assert.AreEqual(0, AchievementCompletionPercentCalculator.RoundPercentForDisplay(0.4));
            Assert.AreEqual(0, AchievementCompletionPercentCalculator.RoundPercentForDisplay(-5));
        }

        [TestMethod]
        public void RoundPercentForDisplay_NonFiniteIsZero()
        {
            Assert.AreEqual(0, AchievementCompletionPercentCalculator.RoundPercentForDisplay(double.NaN));
            Assert.AreEqual(0, AchievementCompletionPercentCalculator.RoundPercentForDisplay(double.PositiveInfinity));
            Assert.AreEqual(0, AchievementCompletionPercentCalculator.RoundPercentForDisplay(double.NegativeInfinity));
        }
    }
}
