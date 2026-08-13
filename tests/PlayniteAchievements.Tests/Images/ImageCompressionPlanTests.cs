using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services.Images;

namespace PlayniteAchievements.Services.Images.Tests
{
    /// <summary>
    /// Covers the policy behind the "compress cached images" maintenance sweep: which cached files
    /// may be rewritten smaller, what size they become, and what that is projected to save.
    /// </summary>
    [TestClass]
    public class ImageCompressionPlanTests
    {
        [TestMethod]
        public void Decide_CompressesOversizedPng()
        {
            Assert.AreEqual(
                ImageCompressionAction.Compress,
                ImageCompressionPlan.Decide("icon.png", 512, 512, 128));
        }

        [TestMethod]
        public void Decide_CompressesOversizedJpeg()
        {
            Assert.AreEqual(
                ImageCompressionAction.Compress,
                ImageCompressionPlan.Decide("cover.jpg", 600, 900, 256));
            Assert.AreEqual(
                ImageCompressionAction.Compress,
                ImageCompressionPlan.Decide("cover.jpeg", 600, 900, 256));
        }

        [TestMethod]
        public void Decide_SkipsFileAlreadyAtCap()
        {
            // The common case by a wide margin: most providers already serve small icons, and
            // rewriting them would cost quality for no space.
            Assert.AreEqual(
                ImageCompressionAction.SkipUnderCap,
                ImageCompressionPlan.Decide("icon.png", 128, 128, 128));
        }

        [TestMethod]
        public void Decide_SkipsFileUnderCap()
        {
            Assert.AreEqual(
                ImageCompressionAction.SkipUnderCap,
                ImageCompressionPlan.Decide("icon.png", 64, 64, 128));
        }

        [TestMethod]
        public void Decide_JudgesCapAgainstTheLongerEdge()
        {
            // A wide image whose height is under the cap still has to come down.
            Assert.AreEqual(
                ImageCompressionAction.Compress,
                ImageCompressionPlan.Decide("banner.jpg", 460, 100, 128));
        }

        [TestMethod]
        public void Decide_SkipsAnimatedFormats()
        {
            // Re-encoding through WPF would flatten these to a single frame.
            Assert.AreEqual(
                ImageCompressionAction.SkipAnimated,
                ImageCompressionPlan.Decide("badge.gif", 512, 512, 128));
            Assert.AreEqual(
                ImageCompressionAction.SkipAnimated,
                ImageCompressionPlan.Decide("badge.webp", 512, 512, 128));
        }

        [TestMethod]
        public void Decide_SkipsAnimatedFormatsEvenWhenUnderCap()
        {
            // Reported as animated rather than under-cap so the summary states the real reason.
            Assert.AreEqual(
                ImageCompressionAction.SkipAnimated,
                ImageCompressionPlan.Decide("badge.gif", 32, 32, 128));
        }

        [TestMethod]
        public void Decide_SkipsFormatsWithNoEncoder()
        {
            // Rewriting these would have to change the extension, stranding the path persisted in
            // the database.
            Assert.AreEqual(
                ImageCompressionAction.SkipUnsupportedFormat,
                ImageCompressionPlan.Decide("icon.bmp", 512, 512, 128));
            Assert.AreEqual(
                ImageCompressionAction.SkipUnsupportedFormat,
                ImageCompressionPlan.Decide("icon.tiff", 512, 512, 128));
        }

        [TestMethod]
        public void Decide_SkipsUnreadableDimensions()
        {
            Assert.AreEqual(
                ImageCompressionAction.SkipUnsupportedFormat,
                ImageCompressionPlan.Decide("icon.png", 0, 0, 128));
        }

        [TestMethod]
        public void IsRewritableExtension_AcceptsOnlyEncodableStillFormats()
        {
            Assert.IsTrue(ImageCompressionPlan.IsRewritableExtension(".png"));
            Assert.IsTrue(ImageCompressionPlan.IsRewritableExtension(".JPG"));
            Assert.IsTrue(ImageCompressionPlan.IsRewritableExtension(".jpeg"));

            // WebP decodes on machines with the optional OS codec but has no WPF encoder.
            Assert.IsFalse(ImageCompressionPlan.IsRewritableExtension(".webp"));
            Assert.IsFalse(ImageCompressionPlan.IsRewritableExtension(".gif"));
            Assert.IsFalse(ImageCompressionPlan.IsRewritableExtension(string.Empty));
            Assert.IsFalse(ImageCompressionPlan.IsRewritableExtension(null));
        }

        [TestMethod]
        public void ComputeTargetSize_ScalesSquareImageToCap()
        {
            ImageCompressionPlan.ComputeTargetSize(512, 512, 128, out var width, out var height);

            Assert.AreEqual(128, width);
            Assert.AreEqual(128, height);
        }

        [TestMethod]
        public void ComputeTargetSize_PreservesAspectRatio()
        {
            ImageCompressionPlan.ComputeTargetSize(600, 900, 300, out var width, out var height);

            Assert.AreEqual(200, width);
            Assert.AreEqual(300, height);
        }

        [TestMethod]
        public void ComputeTargetSize_PreservesAspectRatioForWideImages()
        {
            ImageCompressionPlan.ComputeTargetSize(460, 215, 230, out var width, out var height);

            Assert.AreEqual(230, width);
            Assert.AreEqual(108, height);
        }

        [TestMethod]
        public void ComputeTargetSize_LeavesImageUnderCapAlone()
        {
            ImageCompressionPlan.ComputeTargetSize(64, 32, 128, out var width, out var height);

            Assert.AreEqual(64, width);
            Assert.AreEqual(32, height);
        }

        [TestMethod]
        public void ComputeTargetSize_NeverCollapsesAnEdgeToZero()
        {
            // An extreme aspect ratio must still round up to a decodable size.
            ImageCompressionPlan.ComputeTargetSize(4000, 4, 128, out var width, out var height);

            Assert.AreEqual(128, width);
            Assert.IsTrue(height >= 1, "Target height collapsed to zero.");
        }

        [TestMethod]
        public void EstimateCompressedBytes_ScalesWithPixelArea()
        {
            // Halving each edge quarters the area, so the projection quarters the bytes.
            Assert.AreEqual(
                25_000L,
                ImageCompressionPlan.EstimateCompressedBytes(100_000L, 256, 256, 128));
        }

        [TestMethod]
        public void EstimateCompressedBytes_LeavesFileUnderCapUnchanged()
        {
            Assert.AreEqual(
                7_000L,
                ImageCompressionPlan.EstimateCompressedBytes(7_000L, 64, 64, 128));
        }

        [TestMethod]
        public void EstimateCompressedBytes_HandlesEmptyFile()
        {
            Assert.AreEqual(
                0L,
                ImageCompressionPlan.EstimateCompressedBytes(0L, 512, 512, 128));
        }

        [TestMethod]
        public void DefaultMaxDimension_IsOneOfTheSelectableCaps()
        {
            CollectionAssert.Contains(
                ImageCompressionPlan.SelectableMaxDimensions,
                ImageCompressionPlan.DefaultMaxDimension);
        }
    }
}
