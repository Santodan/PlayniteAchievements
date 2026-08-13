using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services.Images;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PlayniteAchievements.Services.Images.Tests
{
    /// <summary>
    /// Covers the header-only dimension reader the compression scan uses instead of spinning up a
    /// WIC decoder per file. Each case round-trips through a real encoder so the parser is checked
    /// against genuine PNG and JPEG bytes rather than a hand-built approximation.
    /// </summary>
    [TestClass]
    public class ImageHeaderDimensionsTests
    {
        [TestMethod]
        public void TryRead_ReadsPngDimensions()
        {
            using (var stream = EncodePng(320, 200))
            {
                Assert.IsTrue(ImageHeaderDimensions.TryRead(stream, out var width, out var height));
                Assert.AreEqual(320, width);
                Assert.AreEqual(200, height);
            }
        }

        [TestMethod]
        public void TryRead_ReadsSquarePngDimensions()
        {
            using (var stream = EncodePng(64, 64))
            {
                Assert.IsTrue(ImageHeaderDimensions.TryRead(stream, out var width, out var height));
                Assert.AreEqual(64, width);
                Assert.AreEqual(64, height);
            }
        }

        [TestMethod]
        public void TryRead_ReadsJpegDimensions()
        {
            // JPEG has no fixed dimension offset, so this exercises the marker walk.
            using (var stream = EncodeJpeg(600, 900))
            {
                Assert.IsTrue(ImageHeaderDimensions.TryRead(stream, out var width, out var height));
                Assert.AreEqual(600, width);
                Assert.AreEqual(900, height);
            }
        }

        [TestMethod]
        public void TryRead_DoesNotConfuseJpegWidthAndHeight()
        {
            // The SOF segment stores height before width; transposing them would pass a square test.
            using (var stream = EncodeJpeg(160, 40))
            {
                Assert.IsTrue(ImageHeaderDimensions.TryRead(stream, out var width, out var height));
                Assert.AreEqual(160, width);
                Assert.AreEqual(40, height);
            }
        }

        [TestMethod]
        public void TryRead_AgreesWithTheDecoderItReplaces()
        {
            using (var stream = EncodePng(123, 45))
            {
                var frame = BitmapFrame.Create(
                    stream,
                    BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile,
                    BitmapCacheOption.None);
                var decoderWidth = frame.PixelWidth;
                var decoderHeight = frame.PixelHeight;

                stream.Position = 0;
                Assert.IsTrue(ImageHeaderDimensions.TryRead(stream, out var width, out var height));
                Assert.AreEqual(decoderWidth, width);
                Assert.AreEqual(decoderHeight, height);
            }
        }

        [TestMethod]
        public void TryRead_RejectsUnsupportedFormat()
        {
            using (var stream = EncodeGif(32, 32))
            {
                Assert.IsFalse(ImageHeaderDimensions.TryRead(stream, out _, out _));
            }
        }

        [TestMethod]
        public void TryRead_RejectsTruncatedAndEmptyStreams()
        {
            using (var empty = new MemoryStream())
            {
                Assert.IsFalse(ImageHeaderDimensions.TryRead(empty, out _, out _));
            }

            // A valid PNG signature with the IHDR chunk cut off must not report garbage dimensions.
            using (var truncated = new MemoryStream(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A }))
            {
                Assert.IsFalse(ImageHeaderDimensions.TryRead(truncated, out _, out _));
            }
        }

        [TestMethod]
        public void TryRead_RejectsNullStream()
        {
            Assert.IsFalse(ImageHeaderDimensions.TryRead(null, out _, out _));
        }

        private static MemoryStream EncodePng(int width, int height) =>
            Encode(new PngBitmapEncoder(), width, height);

        private static MemoryStream EncodeJpeg(int width, int height) =>
            Encode(new JpegBitmapEncoder(), width, height);

        private static MemoryStream EncodeGif(int width, int height) =>
            Encode(new GifBitmapEncoder(), width, height);

        private static MemoryStream Encode(BitmapEncoder encoder, int width, int height)
        {
            var stride = width * 4;
            var source = BitmapSource.Create(
                width,
                height,
                96,
                96,
                PixelFormats.Bgra32,
                null,
                new byte[stride * height],
                stride);

            encoder.Frames.Add(BitmapFrame.Create(source));

            var stream = new MemoryStream();
            encoder.Save(stream);
            stream.Position = 0;
            return stream;
        }
    }
}
