using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services.Capture;

namespace PlayniteAchievements.Services.Tests.Capture
{
    [TestClass]
    public class BoxBlurTests
    {
        [TestMethod]
        public void RadiusFor_SpreadsTheReachOverThePasses_AndNeverDropsBelowOne()
        {
            Assert.AreEqual(8, BoxBlur.RadiusFor(24));
            Assert.AreEqual(1, BoxBlur.RadiusFor(0.5));
        }

        [TestMethod]
        public void Apply_LeavesAFlatPlaneFlat()
        {
            var plane = Filled(16, 16, 1, 120);

            BoxBlur.Apply(plane, 0, 16, 16, 16, 1, 3);

            foreach (var value in plane)
            {
                Assert.AreEqual(120, value);
            }
        }

        [TestMethod]
        public void Apply_SpreadsAPointWithoutMovingIt()
        {
            var plane = new byte[21 * 21];
            plane[10 * 21 + 10] = 255;

            BoxBlur.Apply(plane, 0, 21, 21, 21, 1, 1);

            var center = plane[10 * 21 + 10];
            Assert.IsTrue(center > 0 && center < 255, "the point did not spread: " + center);
            Assert.AreEqual(plane[10 * 21 + 9], plane[10 * 21 + 11], "the spread is not symmetric");
            Assert.AreEqual(plane[9 * 21 + 10], plane[11 * 21 + 10], "the spread is not symmetric");
            Assert.AreEqual(0, plane[0], "the spread reached past three passes of radius one");
        }

        [TestMethod]
        public void Apply_KeepsInterleavedChannelsApart()
        {
            // BGRA: blue everywhere, nothing in the other channels.
            var plane = new byte[8 * 8 * 4];
            for (var i = 0; i < plane.Length; i += 4)
            {
                plane[i] = 200;
            }

            BoxBlur.Apply(plane, 0, 8, 8, 8 * 4, 4, 2);

            for (var i = 0; i < plane.Length; i += 4)
            {
                Assert.AreEqual(200, plane[i]);
                Assert.AreEqual(0, plane[i + 1]);
                Assert.AreEqual(0, plane[i + 2]);
            }
        }

        [TestMethod]
        public void Apply_StaysInsideAStridedSubregion()
        {
            // A 4x4 region at offset 2 inside rows of 10 bytes; the bytes around it are sentinels.
            var plane = Filled(10, 6, 1, 77);
            for (var row = 1; row < 5; row++)
            {
                for (var col = 2; col < 6; col++)
                {
                    plane[row * 10 + col] = (byte)(col % 2 == 0 ? 0 : 255);
                }
            }

            BoxBlur.Apply(plane, 10 + 2, 4, 4, 10, 1, 1);

            Assert.AreEqual(77, plane[10 + 1]);
            Assert.AreEqual(77, plane[10 + 6]);
            Assert.AreEqual(77, plane[2]);
            Assert.AreEqual(77, plane[5 * 10 + 2]);
        }

        private static byte[] Filled(int width, int height, int channels, byte value)
        {
            var plane = new byte[width * height * channels];
            for (var i = 0; i < plane.Length; i++)
            {
                plane[i] = value;
            }

            return plane;
        }
    }
}
