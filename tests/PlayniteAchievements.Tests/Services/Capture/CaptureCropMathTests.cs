using System.Drawing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Common;
using PlayniteAchievements.Services.Capture;

namespace PlayniteAchievements.Services.Tests.Capture
{
    /// <summary>
    /// Each case here is a window shape that produced a real report, expressed as the rects the
    /// recorder measures. All rects are physical pixels in one space, as the caller guarantees.
    /// </summary>
    [TestClass]
    public class CaptureCropMathTests
    {
        [TestMethod]
        public void BorderlessFullscreen_KeepsTheWholeFrame()
        {
            // Client, frame and window all coincide: nothing to crop away.
            var rect = new Rectangle(0, 0, 1920, 1080);

            var crop = CaptureCropMath.ClientCrop(1920, 1080, new WindowRects(rect, rect, rect), evenDimensions: true);

            Assert.AreEqual(new Rectangle(0, 0, 1920, 1080), crop);
        }

        [TestMethod]
        public void CaptionedWindow_TextureIsTheFrameBounds_ExcludesChrome()
        {
            // A 100px caption and 8px side/bottom borders; the texture is the frame bounds.
            var frame = new Rectangle(100, 200, 1920, 1080);
            var window = new Rectangle(92, 200, 1936, 1088);
            var client = new Rectangle(108, 300, 1904, 972);

            var crop = CaptureCropMath.ClientCrop(1920, 1080, new WindowRects(client, frame, window), evenDimensions: true);

            Assert.AreEqual(new Rectangle(8, 100, 1904, 972), crop);
        }

        [TestMethod]
        public void CaptionedWindow_TextureIsTheWindowRect_ExcludesChrome()
        {
            // Same window, but the texture spans the outer rect (including the invisible border).
            // Measuring against the frame bounds here is what left bottom chrome in frame and cut
            // the top of the picture off.
            var frame = new Rectangle(100, 200, 1920, 1080);
            var window = new Rectangle(92, 200, 1936, 1088);
            var client = new Rectangle(108, 300, 1904, 972);

            var crop = CaptureCropMath.ClientCrop(1936, 1088, new WindowRects(client, frame, window), evenDimensions: true);

            Assert.AreEqual(new Rectangle(16, 100, 1904, 972), crop);
        }

        [TestMethod]
        public void DpiUnawareWindowOnAScaledDisplay_ScalesTheCropIntoTheSmallerTexture()
        {
            // The application renders a 1280x720 surface that DWM stretches to a 1920x1080 window;
            // WGC captures the surface. Ignoring the 2/3 scale kept a magnified corner.
            var rect = new Rectangle(0, 0, 1920, 1080);

            var crop = CaptureCropMath.ClientCrop(1280, 720, new WindowRects(rect, rect, rect), evenDimensions: true);

            Assert.AreEqual(new Rectangle(0, 0, 1280, 720), crop);
        }

        [TestMethod]
        public void DpiUnawareCaptionedWindow_ScalesOffsetAndSizeAlike()
        {
            // 1.5x scaled display: a 1280x720 surface behind a 1920x1080 window, with chrome.
            var window = new Rectangle(0, 0, 1920, 1080);
            var client = new Rectangle(30, 150, 1860, 900);

            var crop = CaptureCropMath.ClientCrop(1280, 720, new WindowRects(client, Rectangle.Empty, window), evenDimensions: true);

            // Every term multiplied by 2/3, widths rounded down to even.
            Assert.AreEqual(new Rectangle(20, 100, 1240, 600), crop);
        }

        [TestMethod]
        public void NoCandidateMatchesTheTexture_KeepsTheWholeFrame()
        {
            // Aspect ratios disagree: the window resized out from under the measurement. Better a
            // little chrome in frame than a crop from a relationship that was never established.
            var frame = new Rectangle(0, 0, 1920, 1080);
            var window = new Rectangle(0, 0, 1920, 1080);
            var client = new Rectangle(0, 0, 1920, 1080);

            var crop = CaptureCropMath.ClientCrop(1024, 1024, new WindowRects(client, frame, window), evenDimensions: true);

            Assert.AreEqual(new Rectangle(0, 0, 1024, 1024), crop);
        }

        [TestMethod]
        public void UnreadableRects_KeepTheWholeFrame()
        {
            var crop = CaptureCropMath.ClientCrop(1920, 1080, default, evenDimensions: true);

            Assert.AreEqual(new Rectangle(0, 0, 1920, 1080), crop);
        }

        [TestMethod]
        public void StillCapture_KeepsOddDimensions()
        {
            // Only an H.264 encode needs even dimensions; a screenshot must not lose a row or
            // column to the video path's rounding.
            var window = new Rectangle(0, 0, 1921, 1081);
            var client = new Rectangle(0, 0, 1921, 1081);

            var crop = CaptureCropMath.ClientCrop(
                1921, 1081, new WindowRects(client, Rectangle.Empty, window), evenDimensions: false);

            Assert.AreEqual(new Rectangle(0, 0, 1921, 1081), crop);
        }

        [TestMethod]
        public void UnreadableClientRect_KeepsTheWholeFrame()
        {
            var rect = new Rectangle(0, 0, 1920, 1080);

            var crop = CaptureCropMath.ClientCrop(1920, 1080, new WindowRects(Rectangle.Empty, rect, rect), evenDimensions: true);

            Assert.AreEqual(new Rectangle(0, 0, 1920, 1080), crop);
        }

        [TestMethod]
        public void CropNeverLeavesTheTexture()
        {
            // A client rect reported larger than the window it belongs to must still stay inside.
            var window = new Rectangle(0, 0, 1920, 1080);
            var client = new Rectangle(100, 100, 4000, 4000);

            var crop = CaptureCropMath.ClientCrop(1920, 1080, new WindowRects(client, Rectangle.Empty, window), evenDimensions: true);

            Assert.IsTrue(crop.Right <= 1920, $"right={crop.Right}");
            Assert.IsTrue(crop.Bottom <= 1080, $"bottom={crop.Bottom}");
        }

        [TestMethod]
        public void CropDimensionsAreAlwaysEven()
        {
            // H.264 needs even dimensions; an odd client area must round down, not up.
            var window = new Rectangle(0, 0, 1921, 1081);
            var client = new Rectangle(0, 0, 1921, 1081);

            var crop = CaptureCropMath.ClientCrop(1921, 1081, new WindowRects(client, Rectangle.Empty, window), evenDimensions: true);

            Assert.AreEqual(0, crop.Width % 2);
            Assert.AreEqual(0, crop.Height % 2);
        }

        [TestMethod]
        public void TheMoreUniformCandidateWins()
        {
            // The window rect maps exactly; the frame bounds only approximately. The exact one
            // must be chosen even though both are within tolerance.
            var frame = new Rectangle(0, 0, 1900, 1080);
            var window = new Rectangle(0, 0, 1920, 1080);
            var client = new Rectangle(0, 0, 1920, 1080);

            var mapping = CaptureCropMath.ResolveMapping(1920, 1080, frame, window);

            Assert.IsTrue(mapping.IsValid);
            Assert.AreEqual(CaptureAnchor.WindowRect, mapping.Anchor);
            Assert.AreEqual(1.0, mapping.Scale, 0.0001);
            Assert.AreEqual(Point.Empty, mapping.Origin);
        }
    }
}
