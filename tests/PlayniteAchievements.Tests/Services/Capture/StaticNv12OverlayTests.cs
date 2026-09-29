using System;
using System.Drawing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services.Capture;

namespace PlayniteAchievements.Services.Tests.Capture
{
    /// <summary>
    /// The precomputed still overlay must land the same pixels as the per-frame
    /// <see cref="OverlayBlitMath.BlendOntoNv12"/> it replaces for the frame chrome, within the
    /// fixed-point rounding, skip transparent margins, and scale with opacity.
    /// </summary>
    [TestClass]
    public class StaticNv12OverlayTests
    {
        private const int W = 16;
        private const int H = 12;

        [TestMethod]
        public void Blend_AtFullOpacity_MatchesThePerFrameBlend()
        {
            var overlay = MakeOverlay(W, H);
            var yExpected = MakeLuma(W, H);
            var uvExpected = MakeChroma(W, H);
            OverlayBlitMath.BlendOntoNv12(yExpected, uvExpected, W, H, overlay, W, H, new Rectangle(0, 0, W, H));

            var still = StaticNv12Overlay.Create(overlay, W, H, W, H);
            Assert.AreEqual(new Rectangle(0, 0, W, H), still.Region);
            var y = MakeLuma(W, H);
            var uv = MakeChroma(W, H);
            still.Blend(y, uv, 1.0);

            AssertClose(yExpected, y, 1);
            AssertClose(uvExpected, uv, 1);
        }

        [TestMethod]
        public void Blend_AtHalfOpacity_MatchesAHalfScaledOverlay()
        {
            var overlay = MakeOverlay(W, H);
            var scaled = (byte[])overlay.Clone();
            OverlayBlitMath.ScaleAll(scaled, 0.5);
            var yExpected = MakeLuma(W, H);
            var uvExpected = MakeChroma(W, H);
            OverlayBlitMath.BlendOntoNv12(yExpected, uvExpected, W, H, scaled, W, H, new Rectangle(0, 0, W, H));

            var still = StaticNv12Overlay.Create(overlay, W, H, W, H);
            var y = MakeLuma(W, H);
            var uv = MakeChroma(W, H);
            still.Blend(y, uv, 0.5);

            AssertClose(yExpected, y, 2);
            AssertClose(uvExpected, uv, 2);
        }

        [TestMethod]
        public void Blend_AtZeroOpacity_LeavesTheFrameAlone()
        {
            var still = StaticNv12Overlay.Create(MakeOverlay(W, H), W, H, W, H);
            var y = MakeLuma(W, H);
            var uv = MakeChroma(W, H);
            still.Blend(y, uv, 0);

            CollectionAssert.AreEqual(MakeLuma(W, H), y);
            CollectionAssert.AreEqual(MakeChroma(W, H), uv);
        }

        [TestMethod]
        public void Create_TrimsTheRegionToCoveredChromaBlocks()
        {
            // Only a 3x3 patch at (5,3) is opaque; the region widens to whole 2x2 blocks.
            var overlay = new byte[W * H * 4];
            for (var y = 3; y < 6; y++)
            {
                for (var x = 5; x < 8; x++)
                {
                    var i = ((y * W) + x) * 4;
                    overlay[i] = 200;
                    overlay[i + 1] = 100;
                    overlay[i + 2] = 50;
                    overlay[i + 3] = 255;
                }
            }

            var still = StaticNv12Overlay.Create(overlay, W, H, W, H);
            Assert.AreEqual(new Rectangle(4, 2, 4, 4), still.Region);
        }

        [TestMethod]
        public void Create_FullyTransparent_HasAnEmptyRegion()
        {
            var still = StaticNv12Overlay.Create(new byte[W * H * 4], W, H, W, H);
            Assert.IsTrue(still.Region.IsEmpty);
        }

        [TestMethod]
        public void Create_ScalesTheOverlayToTheFrame()
        {
            // A 2x2 overlay whose right half is opaque covers the right half of an 8x4 frame.
            var overlay = new byte[2 * 2 * 4];
            foreach (var pixel in new[] { 1, 3 })
            {
                overlay[(pixel * 4) + 3] = 255;
            }

            var still = StaticNv12Overlay.Create(overlay, 2, 2, 8, 4);
            Assert.AreEqual(new Rectangle(4, 0, 4, 4), still.Region);
        }

        /// <summary>A premultiplied gradient with varied coverage, including transparent pixels.</summary>
        private static byte[] MakeOverlay(int w, int h)
        {
            var pixels = new byte[w * h * 4];
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    var i = ((y * w) + x) * 4;
                    var alpha = (x + y) % 5 == 0 ? 0 : ((x * 37) + (y * 11)) % 256;
                    pixels[i] = (byte)(alpha * ((x * 13) % 256) / 255);
                    pixels[i + 1] = (byte)(alpha * ((y * 29) % 256) / 255);
                    pixels[i + 2] = (byte)(alpha * (((x + y) * 7) % 256) / 255);
                    pixels[i + 3] = (byte)alpha;
                }
            }

            return pixels;
        }

        private static byte[] MakeLuma(int w, int h)
        {
            var luma = new byte[w * h];
            for (var i = 0; i < luma.Length; i++)
            {
                luma[i] = (byte)(16 + ((i * 7) % 220));
            }

            return luma;
        }

        private static byte[] MakeChroma(int w, int h)
        {
            var chroma = new byte[w * h / 2];
            for (var i = 0; i < chroma.Length; i++)
            {
                chroma[i] = (byte)(16 + ((i * 13) % 225));
            }

            return chroma;
        }

        private static void AssertClose(byte[] expected, byte[] actual, int tolerance)
        {
            Assert.AreEqual(expected.Length, actual.Length);
            for (var i = 0; i < expected.Length; i++)
            {
                Assert.IsTrue(
                    Math.Abs(expected[i] - actual[i]) <= tolerance,
                    $"index {i}: expected {expected[i]}, actual {actual[i]}");
            }
        }
    }
}
