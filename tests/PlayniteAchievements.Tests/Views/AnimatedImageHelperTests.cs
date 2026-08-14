using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Tests.TestInfrastructure;
using PlayniteAchievements.Views.Helpers;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Tests.Views
{
    [TestClass]
    public class AnimatedImageHelperTests
    {
        [TestMethod]
        public void TryEnsureCachedFrames_RefusesGifsSoTheyReachTheNativeDecoder()
        {
            // This helper retains a full-canvas bitmap per frame, which is what forced large GIFs
            // to be downscaled and temporally sampled. GIFs now stream through NativeGifAnimation
            // instead, so a GIF reaching here at all would mean something silently reintroduced
            // that budget -- and the two paths would both be decoding the same file.
            var path = GifFixture.WriteTempGif(GifFixture.BuildSparseGif(64, 64, 8));
            try
            {
                Assert.IsFalse(
                    AnimatedImageHelper.TryEnsureCachedFrames(path, applyGray: false, decodePixel: 64));
            }
            finally
            {
                GifFixture.DeleteTempPayload(path);
            }
        }

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

        // The retained-pixel budget these cases pin now governs animated WebP alone. The
        // dimensions are kept as they are because they came from real sources that exercised the
        // interesting corners of the budget, not because the format is what is under test.
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
