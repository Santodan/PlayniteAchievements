using System.Drawing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services.Capture;

namespace PlayniteAchievements.Services.Tests.Capture
{
    [TestClass]
    public class LiveBackdropMathTests
    {
        [TestMethod]
        public void RequestFor_GrowsTheCardsByTheReachAndTheMotionPad()
        {
            var request = LiveBackdropMath.RequestFor(new Rectangle(100, 200, 300, 80), 24, 20);

            Assert.AreEqual(new Rectangle(56, 156, 388, 168), request);
            Assert.AreEqual(Rectangle.Empty, LiveBackdropMath.RequestFor(Rectangle.Empty, 24, 20));
        }

        [TestMethod]
        public void Project_MapsScreenToTexture_ThroughOriginAndScale()
        {
            // A window at screen (1000, 500) captured at half size.
            var texture = LiveBackdropMath.Project(
                new Point(1000, 500), 0.5, new Rectangle(1200, 700, 400, 200), 960, 540, out var covered);

            Assert.AreEqual(new Rectangle(100, 100, 200, 100), texture);
            Assert.AreEqual(new Rectangle(1200, 700, 400, 200), covered);
        }

        [TestMethod]
        public void Project_ShiftsARegionRunningOffTheTexture_InsteadOfShrinkingIt()
        {
            // The card is sliding in from below the game's bottom edge.
            var texture = LiveBackdropMath.Project(
                Point.Empty, 1.0, new Rectangle(100, 1000, 300, 200), 1920, 1080, out var covered);

            Assert.AreEqual(new Rectangle(100, 880, 300, 200), texture);
            Assert.AreEqual(new Rectangle(100, 880, 300, 200), covered);
        }

        [TestMethod]
        public void Project_ClampsARegionLargerThanTheTexture()
        {
            var texture = LiveBackdropMath.Project(
                Point.Empty, 1.0, new Rectangle(-50, -50, 400, 300), 320, 240, out var covered);

            Assert.AreEqual(new Rectangle(0, 0, 320, 240), texture);
            Assert.AreEqual(new Rectangle(0, 0, 320, 240), covered);
        }

        [TestMethod]
        public void MipLevelFor_GoesAsDeepAsTheReachHides_WithinTheEdgeFloor()
        {
            Assert.AreEqual(0, LiveBackdropMath.MipLevelFor(3, 400, 200));
            Assert.AreEqual(2, LiveBackdropMath.MipLevelFor(9, 400, 200));
            Assert.AreEqual(LiveBackdropMath.MaxMipLevel, LiveBackdropMath.MipLevelFor(500, 4000, 2000));

            // A short region stops where its height would fall below the floor.
            Assert.AreEqual(1, LiveBackdropMath.MipLevelFor(500, 400, 20));
        }

        [TestMethod]
        public void Viewbox_PlacesTheCardInsideTheFrame()
        {
            var viewbox = LiveBackdropMath.Viewbox(new Rectangle(150, 120, 200, 100), new Rectangle(100, 100, 400, 200));

            Assert.AreEqual(0.125f, viewbox.X, 0.0001f);
            Assert.AreEqual(0.1f, viewbox.Y, 0.0001f);
            Assert.AreEqual(0.5f, viewbox.Width, 0.0001f);
            Assert.AreEqual(0.5f, viewbox.Height, 0.0001f);
        }

        [TestMethod]
        public void Viewbox_ShiftsACardThatOutranTheFrameBackInside()
        {
            // The card moved below the frame it is shown from.
            var viewbox = LiveBackdropMath.Viewbox(new Rectangle(150, 260, 200, 100), new Rectangle(100, 100, 400, 200));

            Assert.AreEqual(0.5f, viewbox.Y, 0.0001f);
            Assert.AreEqual(1f, viewbox.Bottom, 0.0001f);
            Assert.AreEqual(0.5f, viewbox.Height, 0.0001f);
        }
    }
}
