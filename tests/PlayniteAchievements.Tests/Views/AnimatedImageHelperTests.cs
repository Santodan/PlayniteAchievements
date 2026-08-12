using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Views.Helpers;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Tests.Views
{
    [TestClass]
    public class AnimatedImageHelperTests
    {
        [TestMethod]
        public void BuildFrameRetentionPlan_PreservesCompleteDurationWhenFramesAreLimited()
        {
            var sourceDelays = Enumerable.Repeat(40, 300).ToList();

            AnimatedImageHelper.BuildFrameRetentionPlan(
                sourceDelays.Count,
                8,
                sourceDelays,
                out var retainedIndices,
                out var retainedDelays);

            CollectionAssert.AreEqual(
                new List<int> { 0, 37, 75, 112, 150, 187, 225, 262 },
                retainedIndices);
            Assert.AreEqual(sourceDelays.Sum(), retainedDelays.Sum());
            Assert.AreEqual(8, retainedDelays.Count);
        }

        [TestMethod]
        public void BuildFrameRetentionPlan_KeepsEveryFrameAndDelayWhenUnderBudget()
        {
            var sourceDelays = new List<int> { 20, 40, 60, 80 };

            AnimatedImageHelper.BuildFrameRetentionPlan(
                sourceDelays.Count,
                sourceDelays.Count,
                sourceDelays,
                out var retainedIndices,
                out var retainedDelays);

            CollectionAssert.AreEqual(new List<int> { 0, 1, 2, 3 }, retainedIndices);
            CollectionAssert.AreEqual(sourceDelays, retainedDelays);
        }

        [TestMethod]
        public void ResolveAnimationDimensions_PrioritizesAllFramesForToastSizedDecode()
        {
            AnimatedImageHelper.ResolveAnimationDimensions(
                1920,
                1080,
                512,
                300,
                out var width,
                out var height);

            Assert.AreEqual(445, width);
            Assert.AreEqual(250, height);
        }

        [TestMethod]
        public void ResolveAnimationDimensions_DoesNotUpscaleSmallAnimation()
        {
            AnimatedImageHelper.ResolveAnimationDimensions(
                320,
                180,
                512,
                60,
                out var width,
                out var height);

            Assert.AreEqual(320, width);
            Assert.AreEqual(180, height);
        }

        [TestMethod]
        public void ResolveAnimationDimensions_KeepsSteamStationIconAtNativeResolution()
        {
            AnimatedImageHelper.ResolveAnimationDimensions(
                348,
                480,
                160,
                40,
                out var width,
                out var height);

            Assert.AreEqual(348, width);
            Assert.AreEqual(480, height);
        }

        [TestMethod]
        public void ResolveAnimationDimensions_ScalesLargeToastBackgroundWithoutDroppingFrames()
        {
            AnimatedImageHelper.ResolveAnimationDimensions(
                1727,
                289,
                768,
                315,
                out var width,
                out var height);

            Assert.AreEqual(768, width);
            Assert.AreEqual(129, height);
        }
    }
}
