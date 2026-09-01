using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Achievements;

namespace PlayniteAchievements.Tests.Models
{
    [TestClass]
    public class RarityBorderHaloBitmapTests
    {
        private static byte[] RenderPixels(out int width, out int height, out int stride)
        {
            var bitmap = RarityAppearanceHelper.GetBorderHaloBitmap(
                Color.FromRgb(0x86, 0xC8, 0xFF),
                cardWidth: 200, cardHeight: 80, cornerRadius: 8, glowRadius: 36, margin: 42);
            Assert.IsNotNull(bitmap);
            Assert.IsTrue(bitmap.IsFrozen);
            Assert.AreEqual(PixelFormats.Pbgra32, bitmap.Format);

            width = bitmap.PixelWidth;
            height = bitmap.PixelHeight;
            stride = width * 4;
            var pixels = new byte[stride * height];
            bitmap.CopyPixels(pixels, stride, 0);
            return pixels;
        }

        [TestMethod]
        public void Bitmap_SizesToCardPlusMarginAtRenderScale()
        {
            RenderPixels(out var width, out var height, out _);
            var scale = RarityAppearanceHelper.HaloRenderScale;
            Assert.AreEqual((int)((200 + 84) * scale), width);
            Assert.AreEqual((int)((80 + 84) * scale), height);
        }

        [TestMethod]
        public void Pixels_StayPremultipliedAndFadeOutward()
        {
            var pixels = RenderPixels(out var width, out var height, out var stride);

            // Premultiplied invariant: no channel may exceed its pixel's alpha.
            for (var i = 0; i < pixels.Length; i += 4)
            {
                var alpha = pixels[i + 3];
                Assert.IsTrue(pixels[i + 0] <= alpha && pixels[i + 1] <= alpha && pixels[i + 2] <= alpha);
            }

            // The falloff runs out before the corner of the margin: fully transparent there.
            Assert.AreEqual(0, pixels[3]);

            // Mid-height horizontal run from the outside in: alpha never decreases by more than
            // the dither's amplitude, so the profile is the falloff plus grain, not noise.
            var y = height / 2;
            byte previous = 0;
            for (var x = 0; x < width / 2; x++)
            {
                var alpha = pixels[(y * stride) + (x * 4) + 3];
                Assert.IsTrue(alpha + 2 >= previous, $"alpha fell more than dither allows at x={x}");
                if (alpha > previous)
                {
                    previous = alpha;
                }
            }

            // And it actually reaches glow strength at the card edge.
            Assert.IsTrue(previous > 100);
        }

        [TestMethod]
        public void Bitmap_IsCachedPerKeyAndInvalidatedOnClear()
        {
            var first = RarityAppearanceHelper.GetBorderHaloBitmap(
                Colors.Red, 100, 50, 8, 36, 42);
            var second = RarityAppearanceHelper.GetBorderHaloBitmap(
                Colors.Red, 100, 50, 8, 36, 42);
            Assert.AreSame(first, second);

            RarityAppearanceHelper.ClearBorderHaloCache();
            var third = RarityAppearanceHelper.GetBorderHaloBitmap(
                Colors.Red, 100, 50, 8, 36, 42);
            Assert.AreNotSame(first, third);
        }

        [TestMethod]
        public void Bitmap_NullForDegenerateInputs()
        {
            Assert.IsNull(RarityAppearanceHelper.GetBorderHaloBitmap(Colors.Red, 0, 50, 8, 36, 42));
            Assert.IsNull(RarityAppearanceHelper.GetBorderHaloBitmap(Colors.Red, 100, 0, 8, 36, 42));
            Assert.IsNull(RarityAppearanceHelper.GetBorderHaloBitmap(Colors.Red, 100, 50, 8, 0, 42));
        }
    }
}
