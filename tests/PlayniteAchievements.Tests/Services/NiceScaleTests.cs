using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services.Achievements;

namespace PlayniteAchievements.Services.Tests
{
    [TestClass]
    public class NiceScaleTests
    {
        [DataTestMethod]
        [DataRow(0, 1.0, 1.0)]
        [DataRow(1, 1.0, 1.0)]
        [DataRow(3, 3.0, 1.0)]
        [DataRow(7, 8.0, 2.0)]
        [DataRow(11, 12.0, 2.0)]
        [DataRow(47, 50.0, 10.0)]
        [DataRow(130, 150.0, 50.0)]
        [DataRow(999, 1000.0, 200.0)]
        public void ForMax_PicksNiceCeilingAndStep(int max, double expectedMax, double expectedStep)
        {
            var scale = NiceScale.ForMax(max);

            Assert.AreEqual(expectedMax, scale.Max);
            Assert.AreEqual(expectedStep, scale.Step);
        }

        [TestMethod]
        public void ForMax_HoldsInvariantsAcrossRange()
        {
            for (var max = -5; max <= 5000; max++)
            {
                var scale = NiceScale.ForMax(max);

                Assert.IsTrue(scale.Step >= 1, $"step for {max}");
                Assert.IsTrue(scale.Max >= 1, $"max for {max}");
                Assert.IsTrue(scale.Max >= max, $"ceiling below max for {max}");
                Assert.AreEqual(0, scale.Max % scale.Step, $"ceiling not on step for {max}");
                var intervals = scale.Max / scale.Step;
                Assert.IsTrue(intervals >= 1 && intervals <= 8, $"{intervals} intervals for {max}");
            }
        }
    }
}
