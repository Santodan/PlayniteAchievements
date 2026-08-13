using System.Drawing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Capture;

namespace PlayniteAchievements.Services.Tests.Capture
{
    /// <summary>
    /// The height cap shared by the clip encoder (even dimensions, as H.264 requires) and the
    /// screenshot pipeline (exact dimensions).
    /// </summary>
    [TestClass]
    public class ResolutionCapMathTests
    {
        [TestMethod]
        public void CapHeightFor_MapsBothResolutionEnums()
        {
            Assert.AreEqual(0, ResolutionCapMath.CapHeightFor(RecordingResolution.Native));
            Assert.AreEqual(1080, ResolutionCapMath.CapHeightFor(RecordingResolution.P1080));
            Assert.AreEqual(720, ResolutionCapMath.CapHeightFor(RecordingResolution.P720));

            Assert.AreEqual(0, ResolutionCapMath.CapHeightFor(ScreenshotResolution.Native));
            Assert.AreEqual(1080, ResolutionCapMath.CapHeightFor(ScreenshotResolution.P1080));
            Assert.AreEqual(720, ResolutionCapMath.CapHeightFor(ScreenshotResolution.P720));
        }

        [TestMethod]
        public void NoCap_KeepsTheCapturedSize()
        {
            Assert.AreEqual(
                new Size(3840, 2160),
                ResolutionCapMath.Apply(3840, 2160, 0, evenDimensions: false));
        }

        [TestMethod]
        public void SourceUnderTheCap_IsNeverUpscaled()
        {
            Assert.AreEqual(
                new Size(1280, 720),
                ResolutionCapMath.Apply(1280, 720, 1080, evenDimensions: false));
        }

        [TestMethod]
        public void SourceAtTheCap_IsUnchanged()
        {
            Assert.AreEqual(
                new Size(1920, 1080),
                ResolutionCapMath.Apply(1920, 1080, 1080, evenDimensions: false));
        }

        [TestMethod]
        public void OverTheCap_ScalesByHeightPreservingAspect()
        {
            Assert.AreEqual(
                new Size(1920, 1080),
                ResolutionCapMath.Apply(3840, 2160, 1080, evenDimensions: false));
            Assert.AreEqual(
                new Size(1280, 720),
                ResolutionCapMath.Apply(3840, 2160, 720, evenDimensions: false));
        }

        [TestMethod]
        public void Ultrawide_KeepsTheAspectRatio()
        {
            // 3440x1440 capped to 1080 is 2580x1080 exactly; the still keeps the odd-safe width and
            // the encode rounds it down to even.
            Assert.AreEqual(
                new Size(2580, 1080),
                ResolutionCapMath.Apply(3440, 1440, 1080, evenDimensions: false));

            Assert.AreEqual(
                new Size(2580, 1080),
                ResolutionCapMath.Apply(3440, 1440, 1080, evenDimensions: true));
        }

        [TestMethod]
        public void EvenDimensions_RoundsAnOddScaledWidthDown()
        {
            // 2050 wide at 2160 tall scales to exactly 1025, which an H.264 encode cannot take; the
            // still keeps it.
            Assert.AreEqual(
                new Size(1025, 1080),
                ResolutionCapMath.Apply(2050, 2160, 1080, evenDimensions: false));
            Assert.AreEqual(
                new Size(1024, 1080),
                ResolutionCapMath.Apply(2050, 2160, 1080, evenDimensions: true));
        }

        [TestMethod]
        public void EvenDimensions_RoundsAnUncappedOddSizeDown()
        {
            Assert.AreEqual(
                new Size(801, 601),
                ResolutionCapMath.Apply(801, 601, 0, evenDimensions: false));
            Assert.AreEqual(
                new Size(800, 600),
                ResolutionCapMath.Apply(801, 601, 0, evenDimensions: true));
        }

        [TestMethod]
        public void DegenerateSize_ClampsToTheMinimum()
        {
            Assert.AreEqual(
                new Size(1, 1),
                ResolutionCapMath.Apply(1, 1, 0, evenDimensions: false));
            Assert.AreEqual(
                new Size(2, 2),
                ResolutionCapMath.Apply(1, 1, 0, evenDimensions: true));
        }

        [TestMethod]
        public void ExtremeAspect_KeepsAtLeastOnePixelOfWidth()
        {
            // A 40:2160 sliver capped to 1080 scales to 20 wide; a 1:2160 one scales below a pixel.
            Assert.AreEqual(
                new Size(20, 1080),
                ResolutionCapMath.Apply(40, 2160, 1080, evenDimensions: false));
            Assert.AreEqual(
                new Size(1, 1080),
                ResolutionCapMath.Apply(1, 2160, 1080, evenDimensions: false));
            Assert.AreEqual(
                new Size(2, 1080),
                ResolutionCapMath.Apply(1, 2160, 1080, evenDimensions: true));
        }
    }
}
