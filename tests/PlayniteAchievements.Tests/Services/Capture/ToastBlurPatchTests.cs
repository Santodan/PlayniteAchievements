using System.Drawing;
using System.Drawing.Imaging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services.Capture;

namespace PlayniteAchievements.Services.Tests.Capture
{
    [TestClass]
    public class ToastBlurPatchTests
    {
        // A card inset 10% on every side of its container, with corners of 5% of its width.
        private static ToastBlurPatch InsetPatch(double reachPhys = 24) =>
            new ToastBlurPatch(new RectangleF(0.1f, 0.1f, 0.8f, 0.8f), 0.05f, reachPhys);

        [TestMethod]
        public void MapCard_ScalesTheFractionsOntoTheOverlayRect()
        {
            var card = InsetPatch().MapCard(new RectangleF(100, 50, 400, 200));

            Assert.AreEqual(140f, card.X, 0.01f);
            Assert.AreEqual(70f, card.Y, 0.01f);
            Assert.AreEqual(320f, card.Width, 0.01f);
            Assert.AreEqual(160f, card.Height, 0.01f);
            Assert.AreEqual(20f, InsetPatch().MapCornerRadius(new RectangleF(100, 50, 400, 200)), 0.01f);
        }

        [TestMethod]
        public void ReachForStrength_IsLinearFromSharpToFull_WithTheDefaultInTheMiddle()
        {
            Assert.AreEqual(0, ToastBlurPatch.ReachForStrength(0));
            Assert.AreEqual(ToastBlurPatch.MaxReachPhys, ToastBlurPatch.ReachForStrength(100));
            Assert.AreEqual(ToastBlurPatch.MaxReachPhys / 2, ToastBlurPatch.ReachForStrength(null));
            Assert.AreEqual(ToastBlurPatch.MaxReachPhys, ToastBlurPatch.ReachForStrength(250));
        }

        [TestMethod]
        public void Apply_SmoothsTheShotUnderTheCardAndLeavesTheRestAlone()
        {
            using (var shot = Stripes(600, 300))
            {
                var overlay = new Rectangle(100, 50, 400, 200);
                InsetPatch().Apply(shot, overlay, 1.0);

                // Inside the card (140..460 x 70..230) the 2 px stripes average toward mid grey.
                var inside = shot.GetPixel(300, 150);
                Assert.IsTrue(inside.R > 60 && inside.R < 195, "inside the card was not blurred: " + inside.R);

                // Outside the card, including the container's glow room, the stripes are untouched.
                Assert.AreEqual(Stripe(10), shot.GetPixel(10, 10).R);
                Assert.AreEqual(Stripe(110), shot.GetPixel(110, 150).R);
                Assert.AreEqual(Stripe(300), shot.GetPixel(300, 60).R);
            }
        }

        [TestMethod]
        public void Apply_AtZeroStrength_LeavesTheShotSharp()
        {
            using (var shot = Stripes(600, 300))
            {
                InsetPatch(reachPhys: 0).Apply(shot, new Rectangle(100, 50, 400, 200), 1.0);

                Assert.AreEqual(Stripe(300), shot.GetPixel(300, 150).R);
                Assert.AreEqual(Stripe(301), shot.GetPixel(301, 150).R);
            }
        }

        private static byte Stripe(int x) => (x / 2) % 2 == 0 ? (byte)0 : (byte)255;

        private static Bitmap Stripes(int width, int height)
        {
            var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            for (var x = 0; x < width; x++)
            {
                var value = Stripe(x);
                for (var y = 0; y < height; y++)
                {
                    bitmap.SetPixel(x, y, Color.FromArgb(255, value, value, value));
                }
            }

            return bitmap;
        }
    }
}
