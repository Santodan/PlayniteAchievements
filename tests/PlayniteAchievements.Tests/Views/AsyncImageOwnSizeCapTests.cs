using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Views.Helpers;
using System;
using System.Reflection;
using System.Threading;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace PlayniteAchievements.Tests.Views
{
    [TestClass]
    public class AsyncImageOwnSizeCapTests
    {
        [TestMethod]
        public void CapAtOwnSize_DecodesNativeAndCapsAtPixelSize()
        {
            RunSta(() =>
            {
                var image = new Image();
                AsyncImage.SetDecodePixel(image, 128);
                Assert.AreEqual(128, ResolveDecodePixel(image));

                AsyncImage.SetCapAtOwnSize(image, true);
                Assert.AreEqual(-1, ResolveDecodePixel(image));

                // 72 DPI, so a DPI-based size would be 53.3 x 26.7 DIP; the cap uses pixels.
                ApplySource(image, Bitmap(40, 20, 72));
                Assert.AreEqual(40d, image.MaxWidth);
                Assert.AreEqual(20d, image.MaxHeight);

                AsyncImage.SetCapAtOwnSize(image, false);
                Assert.AreEqual(128, ResolveDecodePixel(image));
                Assert.AreEqual(double.PositiveInfinity, image.MaxWidth);
                Assert.AreEqual(double.PositiveInfinity, image.MaxHeight);
            });
        }

        [TestMethod]
        public void CapOff_LeavesXamlMaxSizeAlone()
        {
            RunSta(() =>
            {
                var image = new Image { MaxWidth = 12, MaxHeight = 12 };
                ApplySource(image, Bitmap(40, 20, 96));

                Assert.AreEqual(12d, image.MaxWidth);
                Assert.AreEqual(12d, image.MaxHeight);
            });
        }

        private static int ResolveDecodePixel(Image image) =>
            (int)typeof(AsyncImage).GetMethod("ResolveDecodePixel", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, new object[] { image });

        private static void ApplySource(Image image, ImageSource source) =>
            typeof(AsyncImage).GetMethod("ApplySource", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, new object[] { image, source });

        private static BitmapSource Bitmap(int width, int height, double dpi)
        {
            var bitmap = BitmapSource.Create(width, height, dpi, dpi, PixelFormats.Bgra32, null,
                new byte[width * height * 4], width * 4);
            bitmap.Freeze();
            return bitmap;
        }

        private static void RunSta(Action body)
        {
            Exception failure = null;
            var thread = new Thread(() =>
            {
                try { body(); }
                catch (Exception exception) { failure = exception; }
                finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(15)));
            if (failure != null) throw new AssertFailedException(failure.ToString());
        }
    }
}
