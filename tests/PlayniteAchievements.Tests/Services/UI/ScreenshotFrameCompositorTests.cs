using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services.UI;

namespace PlayniteAchievements.Tests.Services.UI
{
    [TestClass]
    public class ScreenshotFrameCompositorTests
    {
        [TestMethod]
        public void ComputeCanvas_ScalesTo1080DipHeight()
        {
            var (width, height, scale) = ScreenshotFrameCompositor.ComputeCanvas(3840, 2160);

            Assert.AreEqual(1920.0, width, 0.001);
            Assert.AreEqual(1080.0, height, 0.001);
            Assert.AreEqual(2.0, scale, 0.001);
        }

        [TestMethod]
        public void ComputeCanvas_PreservesUltrawideAspect()
        {
            var (width, height, scale) = ScreenshotFrameCompositor.ComputeCanvas(2560, 1080);

            Assert.AreEqual(2560.0, width, 0.001);
            Assert.AreEqual(1080.0, height, 0.001);
            Assert.AreEqual(1.0, scale, 0.001);
        }

        [TestMethod]
        public void ComposeFramed_MatchesInputDimensionsAndOverlaysFrame()
        {
            RunOnSta(() =>
            {
                var screenshot = CreateSolidBitmap(320, 180, blue: 255, green: 0, red: 0);
                var template = CreateFullBleedBorderTemplate();
                var compositor = new ScreenshotFrameCompositor(null);

                var framed = compositor.ComposeFramed(screenshot, template, new object());

                Assert.IsNotNull(framed);
                Assert.AreEqual(320, framed.PixelWidth);
                Assert.AreEqual(180, framed.PixelHeight);
                Assert.IsTrue(framed.IsFrozen);

                // The 108-DIP border maps to 180/1080 * 108 = 18 pixels: an edge pixel is frame
                // red, the center keeps the screenshot blue.
                var edge = GetPixel(framed, 5, 5);
                Assert.IsTrue(edge.R > 200 && edge.B < 60, $"edge pixel was {edge}");

                var center = GetPixel(framed, 160, 90);
                Assert.IsTrue(center.B > 200 && center.R < 60, $"center pixel was {center}");
            });
        }

        [TestMethod]
        public void ComposeFramed_NullInputsReturnNull()
        {
            RunOnSta(() =>
            {
                var compositor = new ScreenshotFrameCompositor(null);
                var screenshot = CreateSolidBitmap(16, 16, 0, 0, 0);
                var template = CreateFullBleedBorderTemplate();

                Assert.IsNull(compositor.ComposeFramed(null, template, new object()));
                Assert.IsNull(compositor.ComposeFramed(screenshot, null, new object()));
                Assert.IsNull(compositor.ComposeFramed(screenshot, template, null));
            });
        }

        [TestMethod]
        public void ToBitmapSource_ConvertsAndFreezes()
        {
            using (var bitmap = new System.Drawing.Bitmap(10, 8, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            {
                using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
                {
                    graphics.Clear(System.Drawing.Color.Lime);
                }

                var source = ScreenshotFrameCompositor.ToBitmapSource(bitmap);

                Assert.IsNotNull(source);
                Assert.AreEqual(10, source.PixelWidth);
                Assert.AreEqual(8, source.PixelHeight);
                Assert.IsTrue(source.IsFrozen);

                var pixel = GetPixel(source, 5, 4);
                Assert.IsTrue(pixel.G > 200 && pixel.R < 60 && pixel.B < 60, $"pixel was {pixel}");
            }
        }

        private static DataTemplate CreateFullBleedBorderTemplate()
        {
            var factory = new FrameworkElementFactory(typeof(Border));
            factory.SetValue(Border.BorderBrushProperty, Brushes.Red);
            factory.SetValue(Border.BorderThicknessProperty, new Thickness(108));
            factory.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            return new DataTemplate { VisualTree = factory };
        }

        private static BitmapSource CreateSolidBitmap(int width, int height, byte blue, byte green, byte red)
        {
            var stride = width * 4;
            var pixels = new byte[stride * height];
            for (var i = 0; i < pixels.Length; i += 4)
            {
                pixels[i] = blue;
                pixels[i + 1] = green;
                pixels[i + 2] = red;
                pixels[i + 3] = 255;
            }

            var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
            source.Freeze();
            return source;
        }

        private static (byte B, byte G, byte R, byte A) GetPixel(BitmapSource source, int x, int y)
        {
            var pixel = new byte[4];
            source.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
            return (pixel[0], pixel[1], pixel[2], pixel[3]);
        }

        private static void RunOnSta(Action action)
        {
            Exception exception = null;
            var thread = new Thread(() =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    exception = ex;
                }
            });

            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();

            if (exception != null)
            {
                ExceptionDispatchInfo.Capture(exception).Throw();
            }
        }
    }
}
