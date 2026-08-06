using System.Drawing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services.UI;

namespace PlayniteAchievements.Tests.Services.UI
{
    /// <summary>
    /// Covers the pure corner and clamp math behind toast placement. Both are static and take only
    /// rectangles and scalars, so no window, presentation source, or DPI context is involved.
    /// </summary>
    [TestClass]
    public class ToastWindowPlacerTests
    {
        // The visible-body gap (CornerGapDip 24) less the card's glow margin: +8 DIP with the glow
        // off, -18 DIP with the border glow on (the window then overhangs the edge on purpose).
        private const double GapNoGlow = 8d;
        private const double GapBorderGlow = -18d;

        [TestMethod]
        public void ComputeCorner_PlacesEachCornerInsetByTheScaledGap()
        {
            var anchor = Rectangle.FromLTRB(0, 0, 1920, 1040);

            ToastWindowPlacer.ComputeCorner(anchor, 442, 138, 1.0, false, false, GapNoGlow, out var x, out var y);
            Assert.AreEqual(8, x);
            Assert.AreEqual(8, y);

            ToastWindowPlacer.ComputeCorner(anchor, 442, 138, 1.0, true, false, GapNoGlow, out x, out y);
            Assert.AreEqual(1920 - 442 - 8, x);
            Assert.AreEqual(8, y);

            ToastWindowPlacer.ComputeCorner(anchor, 442, 138, 1.0, false, true, GapNoGlow, out x, out y);
            Assert.AreEqual(8, x);
            Assert.AreEqual(1040 - 138 - 8, y);

            ToastWindowPlacer.ComputeCorner(anchor, 442, 138, 1.0, true, true, GapNoGlow, out x, out y);
            Assert.AreEqual(1920 - 442 - 8, x);
            Assert.AreEqual(1040 - 138 - 8, y);
        }

        [TestMethod]
        public void ComputeCorner_ScalesTheGapToTheMonitor()
        {
            var anchor = Rectangle.FromLTRB(0, 0, 3840, 2120);

            ToastWindowPlacer.ComputeCorner(anchor, 884, 276, 2.0, true, true, GapNoGlow, out var x, out var y);

            Assert.AreEqual(3840 - 884 - 16, x);
            Assert.AreEqual(2120 - 276 - 16, y);
        }

        [TestMethod]
        public void ComputeCorner_HonoursASecondaryMonitorOrigin()
        {
            // A 4K monitor to the right of a 1080p primary: physical origin is not zero.
            var anchor = Rectangle.FromLTRB(1920, 0, 5760, 2120);

            ToastWindowPlacer.ComputeCorner(anchor, 884, 276, 2.0, true, true, GapNoGlow, out var x, out var y);

            Assert.AreEqual(5760 - 884 - 16, x);
            Assert.AreEqual(2120 - 276 - 16, y);
        }

        [TestMethod]
        public void ClampToBounds_LeavesAnOnScreenCornerAlone()
        {
            var anchor = Rectangle.FromLTRB(0, 0, 3840, 2120);

            var clamped = ToastWindowPlacer.ClampToBounds(
                2940, 1828, 884, 276, anchor, 0, out var x, out var y);

            Assert.IsFalse(clamped);
            Assert.AreEqual(2940, x);
            Assert.AreEqual(1828, y);
        }

        [TestMethod]
        public void ClampToBounds_KeepsTheDeliberateGlowOverhang()
        {
            // With the border glow on the gap is negative, so the window hangs past the edge by
            // |gap| * monitorScale and the visible card body still sits a constant distance in.
            var anchor = Rectangle.FromLTRB(0, 0, 3840, 2120);
            var overhang = (int)(-GapBorderGlow * 2.0);

            var clamped = ToastWindowPlacer.ClampToBounds(
                -36, -36, 884, 276, anchor, overhang, out var x, out var y);

            Assert.IsFalse(clamped);
            Assert.AreEqual(-36, x);
            Assert.AreEqual(-36, y);
        }

        [TestMethod]
        public void ClampToBounds_PullsAnOffscreenCornerBack()
        {
            var anchor = Rectangle.FromLTRB(0, 0, 3840, 2120);

            var clamped = ToastWindowPlacer.ClampToBounds(
                6000, 4000, 884, 276, anchor, 0, out var x, out var y);

            Assert.IsTrue(clamped);
            Assert.AreEqual(3840 - 884, x);
            Assert.AreEqual(2120 - 276, y);
        }

        [TestMethod]
        public void ClampToBounds_PinsACardLargerThanItsAnchorToTheNearEdge()
        {
            // An over-applied DPI compensation can measure the card larger than the anchor. The
            // right/bottom corners then subtract that size from the far edge and go negative; the
            // near edge must stay visible rather than the card being pushed off the opposite side.
            var anchor = Rectangle.FromLTRB(0, 0, 800, 600);

            var clamped = ToastWindowPlacer.ClampToBounds(
                -108, -108, 900, 700, anchor, 0, out var x, out var y);

            Assert.IsTrue(clamped);
            Assert.AreEqual(0, x);
            Assert.AreEqual(0, y);
        }

        [TestMethod]
        public void ClampToBounds_IgnoresAnEmptyAnchor()
        {
            var clamped = ToastWindowPlacer.ClampToBounds(
                5000, 5000, 884, 276, Rectangle.Empty, 0, out var x, out var y);

            Assert.IsFalse(clamped);
            Assert.AreEqual(5000, x);
            Assert.AreEqual(5000, y);
        }
    }
}
