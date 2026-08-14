using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Tests.TestInfrastructure;
using PlayniteAchievements.Views.Helpers;
using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using XamlAnimatedGif;

namespace PlayniteAchievements.Tests.Views
{
    [TestClass]
    public class NativeGifAnimationTests
    {
        [TestMethod]
        public async Task PayloadCache_SharesCompressedBytesOnlyWhileSourcesAreActive()
        {
            var path = CreateTempGifPayload(new byte[] { 1, 2, 3, 4 });
            try
            {
                var first = await NativeGifPayloadCache.AcquireAsync(path, CancellationToken.None);
                var second = await NativeGifPayloadCache.AcquireAsync(path, CancellationToken.None);
                try
                {
                    Assert.AreSame(first.PayloadReference, second.PayloadReference);
                    Assert.AreEqual(1, NativeGifPayloadCache.ActiveEntryCount);

                    first.Dispose();
                    Assert.AreEqual(1, NativeGifPayloadCache.ActiveEntryCount);
                    second.Dispose();
                    Assert.AreEqual(0, NativeGifPayloadCache.ActiveEntryCount);
                }
                finally
                {
                    first.Dispose();
                    second.Dispose();
                }
            }
            finally
            {
                DeleteTempPayload(path);
            }
        }

        [TestMethod]
        public async Task PayloadCache_ReplacedFileGetsANewPayloadWhileOldVisualIsAlive()
        {
            var path = CreateTempGifPayload(new byte[] { 1, 2, 3, 4 });
            NativeGifPayloadCache.Lease first = null;
            NativeGifPayloadCache.Lease replacement = null;
            try
            {
                first = await NativeGifPayloadCache.AcquireAsync(path, CancellationToken.None);
                File.WriteAllBytes(path, new byte[] { 9, 8, 7, 6, 5 });
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(2));

                replacement = await NativeGifPayloadCache.AcquireAsync(path, CancellationToken.None);

                Assert.AreNotSame(first.PayloadReference, replacement.PayloadReference);
                Assert.AreEqual(2, NativeGifPayloadCache.ActiveEntryCount);
            }
            finally
            {
                first?.Dispose();
                replacement?.Dispose();
                DeleteTempPayload(path);
            }

            Assert.AreEqual(0, NativeGifPayloadCache.ActiveEntryCount);
        }

        [TestMethod]
        public void GrayscaleView_TracksMutablePixelsAndPreservesAlpha()
        {
            LocalizationAssemblyInitializer.RunOnSta(() =>
            {
                var source = new WriteableBitmap(1, 1, 96, 96, PixelFormats.Bgra32, null);
                WritePixel(source, blue: 255, green: 0, red: 0, alpha: 128);
                var view = NativeGifAnimation.CreateGrayscaleView(source);

                var first = RenderPixel(view);
                Assert.AreEqual(first[0], first[1]);
                Assert.AreEqual(first[1], first[2]);
                Assert.IsTrue(first[3] >= 126 && first[3] <= 129, $"Alpha was {first[3]}.");

                WritePixel(source, blue: 255, green: 255, red: 255, alpha: 255);
                var second = RenderPixel(view);
                Assert.IsTrue(second[0] > first[0]);
                Assert.AreEqual(second[0], second[1]);
                Assert.AreEqual(second[1], second[2]);
                Assert.AreEqual(255, second[3]);
            });
        }

        [TestMethod]
        public void StreamingDecoder_KeepsReportedGifDimensionsAndEveryFrame()
        {
            // Regression for the user-reported 1727x289, 315-frame background. Each encoded frame
            // is only 1x1, so this fixture proves logical-canvas resolution and temporal frame count
            // without allocating hundreds of full-canvas source bitmaps in the test itself.
            LocalizationAssemblyInitializer.RunOnSta(() =>
            {
                using (var stream = new MemoryStream(GifFixture.BuildSparseGif(1727, 289, 315), writable: false))
                {
                    var imageAnimatorType = typeof(AnimationBehavior).Assembly
                        .GetType("XamlAnimatedGif.ImageAnimator", throwOnError: true);
                    var create = imageAnimatorType.GetMethod(
                        "CreateAsync",
                        BindingFlags.Public | BindingFlags.Static,
                        binder: null,
                        types: new[]
                        {
                            typeof(Stream),
                            typeof(System.Windows.Media.Animation.RepeatBehavior),
                            typeof(System.Windows.Controls.Image),
                            typeof(bool)
                        },
                        modifiers: null);
                    Assert.IsNotNull(create);

                    var task = (Task)create.Invoke(null, new object[]
                    {
                        stream,
                        System.Windows.Media.Animation.RepeatBehavior.Forever,
                        new System.Windows.Controls.Image(),
                        false
                    });
                    task.GetAwaiter().GetResult();
                    var animator = (Animator)task.GetType().GetProperty("Result").GetValue(task);
                    try
                    {
                        Assert.AreEqual(315, animator.FrameCount);
                        var bitmapProperty = typeof(Animator).GetProperty(
                            "Bitmap",
                            BindingFlags.Instance | BindingFlags.NonPublic);
                        var bitmap = (BitmapSource)bitmapProperty.GetValue(animator);
                        Assert.AreEqual(1727, bitmap.PixelWidth);
                        Assert.AreEqual(289, bitmap.PixelHeight);
                    }
                    finally
                    {
                        animator.Dispose();
                    }
                }
            });
        }

        private static string CreateTempGifPayload(byte[] bytes) => GifFixture.WriteTempGif(bytes);

        private static void DeleteTempPayload(string path) => GifFixture.DeleteTempPayload(path);

        private static void WritePixel(WriteableBitmap bitmap, byte blue, byte green, byte red, byte alpha)
        {
            bitmap.WritePixels(
                new Int32Rect(0, 0, 1, 1),
                new[] { blue, green, red, alpha },
                4,
                0);
        }

        private static byte[] RenderPixel(ImageSource source)
        {
            var visual = new DrawingVisual();
            using (var drawing = visual.RenderOpen())
            {
                drawing.DrawImage(source, new Rect(0, 0, 1, 1));
            }

            var rendered = new RenderTargetBitmap(1, 1, 96, 96, PixelFormats.Pbgra32);
            rendered.Render(visual);
            var pixel = new byte[4];
            rendered.CopyPixels(pixel, 4, 0);
            return pixel;
        }
    }
}
