using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services.Capture;

namespace PlayniteAchievements.Services.Tests.Capture
{
    /// <summary>
    /// Where a decoded NV12 frame's chroma plane begins. The padded case is the one that shipped
    /// broken: a capture width that is even but not 16-aligned makes the decoder pad the pitch, and
    /// reading the contiguous length at the frame width then divides unevenly and fails the whole
    /// overlay pass.
    /// </summary>
    [TestClass]
    public class Nv12LayoutMathTests
    {
        [TestMethod]
        public void PaddedPitch_ReadsTheLengthAtThePitch()
        {
            // From a user's log: a 4K window capped to 1972x1080, which the decoder pads to pitch
            // 1984 over 1088 allocated rows (1080 rounded up to 16). 1972 x 3/2 = 2958 does not
            // divide 3237888 at all, which is what used to throw.
            Assert.AreEqual(
                1088,
                Nv12LayoutMath.AlignedHeight(1972, 1080, 1984, 3237888L, 3237888L));
        }

        [TestMethod]
        public void UnpaddedPitch_KeepsTheFrameWidthReading()
        {
            // 1920 is already 16-aligned, so pitch == frameW and both divisors agree. This is the
            // common case and the behaviour that already worked.
            Assert.AreEqual(
                1080,
                Nv12LayoutMath.AlignedHeight(1920, 1080, 1920, 3110400L, 3110400L));
        }

        [TestMethod]
        public void PaddedPitch_StillAllowsAnAllocatedHeightAboveTheFrame()
        {
            // 1280x720 padded to pitch 1280 (no padding) but allocated over 736 rows.
            Assert.AreEqual(
                736,
                Nv12LayoutMath.AlignedHeight(1280, 720, 1280, 1413120L, 1413120L));
        }

        [TestMethod]
        public void LengthFittingNoLayout_IsRefused()
        {
            // Divides by neither 1972 x 3/2 nor 1984 x 3/2.
            Assert.AreEqual(0, Nv12LayoutMath.AlignedHeight(1972, 1080, 1984, 3237889L, 4000000L));
        }

        [TestMethod]
        public void SurfaceLargerThanTheBuffer_IsRefused()
        {
            // The arithmetic works out to 1088 rows, but the buffer cannot hold that surface.
            Assert.AreEqual(0, Nv12LayoutMath.AlignedHeight(1972, 1080, 1984, 3237888L, 3000000L));
        }

        [TestMethod]
        public void FewerRowsThanTheFrame_IsRefused()
        {
            // 1984 x 1.5 x 1080 = 3214080 describes only 1080 rows at 1088-row frame height.
            Assert.AreEqual(0, Nv12LayoutMath.AlignedHeight(1972, 1088, 1984, 3214080L, 3237888L));
        }

        [TestMethod]
        public void CodedWidthRows_ReadTheLengthAtTheMacroblockWidth()
        {
            // 2032 (2020 rounded to 16) x 1.5 x 1088 = 3316224; 2048 x 1.5 x 1088 = 3342336 allocated.
            Assert.AreEqual(1088, Nv12LayoutMath.AlignedHeight(2020, 1080, 2048, 3316224L, 3342336L));
            // 416 (406 rounded to 16) x 1.5 x 192 = 119808; 448 x 1.5 x 192 = 129024 allocated.
            Assert.AreEqual(192, Nv12LayoutMath.AlignedHeight(406, 192, 448, 119808L, 129024L));
        }

        [TestMethod]
        public void CodedWidthRows_AcceptAnyPaddingNarrowerThanThePitch()
        {
            // 1920 (1900 padded to 64) x 1.5 x 1088 = 3133440; 2048 x 1.5 x 1088 = 3342336 allocated.
            Assert.AreEqual(1088, Nv12LayoutMath.AlignedHeight(1900, 1080, 2048, 3133440L, 3342336L));
        }

        [TestMethod]
        public void CodedWidthRows_ThatOverflowTheBuffer_AreRefused()
        {
            Assert.AreEqual(0, Nv12LayoutMath.AlignedHeight(2020, 1080, 2048, 3316224L, 3342335L));
        }

        [TestMethod]
        public void DegenerateInputs_AreRefused()
        {
            Assert.AreEqual(0, Nv12LayoutMath.AlignedHeight(0, 1080, 1984, 3237888L, 3237888L));
            Assert.AreEqual(0, Nv12LayoutMath.AlignedHeight(1972, 0, 1984, 3237888L, 3237888L));
            Assert.AreEqual(0, Nv12LayoutMath.AlignedHeight(1972, 1080, 0, 3237888L, 3237888L));
            Assert.AreEqual(0, Nv12LayoutMath.AlignedHeight(1972, 1080, 1984, 0L, 3237888L));
        }
    }
}
