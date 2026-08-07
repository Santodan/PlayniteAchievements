using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services.Images;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace PlayniteAchievements.Services.Tests
{
    /// <summary>
    /// Covers the shared format list, and in particular the split between formats the plugin
    /// recognizes and formats this machine can actually decode.
    /// </summary>
    [TestClass]
    // These tests drive the codec answer through a static override, so they must not run
    // alongside anything else that asks whether WebP is available.
    [DoNotParallelize]
    public class ImageFormatsTests
    {
        private static readonly byte[] Vp8LOnePixel =
        {
            0x2F, 0x00, 0x00, 0x00, 0x10, 0x07, 0x10, 0x11, 0x11, 0x88, 0x88, 0xFE, 0x07
        };

        private string _directory;

        [TestInitialize]
        public void Setup()
        {
            _directory = Path.Combine(Path.GetTempPath(), "PlayniteAchievements", "ImageFormatsTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
        }

        [TestCleanup]
        public void Cleanup()
        {
            // Static probe state would otherwise leak into unrelated tests.
            WebpCodecProbe.SupportOverride = null;

            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch
            {
            }
        }

        [TestMethod]
        public void All_RecognizesWebpRegardlessOfCodecAvailability()
        {
            // A cached .webp must stay visible to path probing, size accounting, and clearing even
            // where it cannot be decoded, or it would become an unreachable file on disk.
            WebpCodecProbe.SupportOverride = false;

            Assert.IsTrue(ImageFormats.All.Contains(".webp", StringComparer.OrdinalIgnoreCase));
            Assert.IsTrue(ImageFormats.IsSupportedExtension(".webp"));
            Assert.IsTrue(ImageFormats.IsSupportedExtension(".WEBP"));
        }

        [TestMethod]
        public void Selectable_OffersWebpOnlyWhenItCanBeDecoded()
        {
            WebpCodecProbe.SupportOverride = true;
            Assert.IsTrue(ImageFormats.IsSelectableExtension(".webp"));
            Assert.IsTrue(ImageFormats.Selectable.Contains(".webp", StringComparer.OrdinalIgnoreCase));

            WebpCodecProbe.SupportOverride = false;
            Assert.IsFalse(ImageFormats.IsSelectableExtension(".webp"));
            Assert.IsFalse(ImageFormats.Selectable.Contains(".webp", StringComparer.OrdinalIgnoreCase));
        }

        [TestMethod]
        public void Selectable_LeavesEveryOtherFormatUnaffected()
        {
            WebpCodecProbe.SupportOverride = false;

            foreach (var extension in ImageFormats.All.Where(e => !ImageFormats.IsWebpExtension(e)))
            {
                Assert.IsTrue(
                    ImageFormats.IsSelectableExtension(extension),
                    $"'{extension}' should not depend on the WebP codec.");
            }
        }

        [TestMethod]
        public void IsSupportedExtension_RejectsUnknownAndEmptyValues()
        {
            Assert.IsFalse(ImageFormats.IsSupportedExtension(".svg"));
            Assert.IsFalse(ImageFormats.IsSupportedExtension(".psd"));
            Assert.IsFalse(ImageFormats.IsSupportedExtension(null));
            Assert.IsFalse(ImageFormats.IsSupportedExtension(string.Empty));
            Assert.IsFalse(ImageFormats.IsSupportedExtension("   "));
        }

        [TestMethod]
        public void BuildOpenFileDialogFilter_TracksCodecAvailabilityAndTheAllFilesEntry()
        {
            WebpCodecProbe.SupportOverride = true;
            var withWebp = ImageFormats.BuildOpenFileDialogFilter(includeAllFiles: false);
            Assert.IsTrue(withWebp.Contains("*.webp"));
            Assert.IsFalse(withWebp.Contains("*.*"));

            WebpCodecProbe.SupportOverride = false;
            var withoutWebp = ImageFormats.BuildOpenFileDialogFilter(includeAllFiles: true);
            Assert.IsFalse(withoutWebp.Contains("*.webp"));
            Assert.IsTrue(withoutWebp.EndsWith("|All Files (*.*)|*.*"));
            Assert.IsTrue(withoutWebp.Contains("*.png"));
        }

        [TestMethod]
        public void GetExtension_ReadsThePathSegmentAndIgnoresQueryStrings()
        {
            Assert.AreEqual(".gif", ImageFormats.GetExtension(@"C:\art\badge.gif"));
            Assert.AreEqual(".webp", ImageFormats.GetExtension("https://example.com/a/b/icon.webp?size=64&v=2"));
            Assert.AreEqual(".png", ImageFormats.GetExtension("images/nested/icon.png"));
            Assert.AreEqual(string.Empty, ImageFormats.GetExtension("https://example.com/no-extension"));
            Assert.AreEqual(string.Empty, ImageFormats.GetExtension(null));
        }

        [TestMethod]
        public void IsAnimationCandidate_CoversOnlyTheFormatsThatCanAnimate()
        {
            // Extension-based by design: this decides whether to preserve the original format on
            // download, before the bytes are available to inspect.
            Assert.IsTrue(ImageFormats.IsAnimationCandidate(@"C:\art\loop.gif"));
            Assert.IsTrue(ImageFormats.IsAnimationCandidate(@"C:\art\loop.webp"));
            Assert.IsTrue(ImageFormats.IsAnimationCandidate("https://example.com/loop.webp?cache=1"));
            Assert.IsFalse(ImageFormats.IsAnimationCandidate(@"C:\art\still.png"));
            Assert.IsFalse(ImageFormats.IsAnimationCandidate(@"C:\art\still.jpg"));
        }

        [TestMethod]
        public void IsAnimatedFile_InspectsWebpContentButTrustsTheGifExtension()
        {
            var animated = WriteWebp("animated.webp", animated: true);
            var still = WriteWebp("still.webp", animated: false);

            Assert.IsTrue(ImageFormats.IsAnimatedFile(animated));
            Assert.IsFalse(ImageFormats.IsAnimatedFile(still));

            // GIF keeps its long-standing extension-only treatment; a single-frame GIF is filtered
            // out later, by the animation frame-count floor.
            Assert.IsTrue(ImageFormats.IsAnimatedFile(@"C:\art\anything.gif"));

            // Nothing to inspect: a remote or missing WebP must not claim to be animated.
            Assert.IsFalse(ImageFormats.IsAnimatedFile("https://example.com/remote.webp"));
            Assert.IsFalse(ImageFormats.IsAnimatedFile(Path.Combine(_directory, "missing.webp")));
        }

        [TestMethod]
        public void IsAnimatedFile_DoesNotDependOnTheCodecBeingInstalled()
        {
            // The check reads the container header directly, so it stays accurate on a machine that
            // cannot decode the pixels.
            var animated = WriteWebp("animated.webp", animated: true);

            WebpCodecProbe.SupportOverride = false;
            Assert.IsTrue(ImageFormats.IsAnimatedFile(animated));
        }

        private string WriteWebp(string name, bool animated)
        {
            var path = Path.Combine(_directory, name);
            var bytes = new List<byte>();
            bytes.AddRange(Encoding.ASCII.GetBytes("RIFF"));
            bytes.AddRange(BitConverter.GetBytes((uint)0));
            bytes.AddRange(Encoding.ASCII.GetBytes("WEBP"));

            if (animated)
            {
                var vp8x = new List<byte> { 0x02, 0, 0, 0 };
                vp8x.AddRange(new byte[] { 1, 0, 0, 1, 0, 0 });
                bytes.AddRange(Chunk("VP8X", vp8x.ToArray()));
                bytes.AddRange(Chunk("ANIM", new byte[6]));

                for (var i = 0; i < 2; i++)
                {
                    var anmf = new List<byte>(new byte[12]);
                    anmf.AddRange(new byte[] { 100, 0, 0 });
                    anmf.Add(0);
                    anmf.AddRange(Chunk("VP8L", Vp8LOnePixel));
                    bytes.AddRange(Chunk("ANMF", anmf.ToArray()));
                }
            }
            else
            {
                bytes.AddRange(Chunk("VP8L", Vp8LOnePixel));
            }

            var buffer = bytes.ToArray();
            Array.Copy(BitConverter.GetBytes((uint)(buffer.Length - 8)), 0, buffer, 4, 4);
            File.WriteAllBytes(path, buffer);
            return path;
        }

        private static byte[] Chunk(string tag, byte[] payload)
        {
            var bytes = new List<byte>();
            bytes.AddRange(Encoding.ASCII.GetBytes(tag));
            bytes.AddRange(BitConverter.GetBytes((uint)payload.Length));
            bytes.AddRange(payload);
            if ((payload.Length & 1) != 0)
            {
                bytes.Add(0);
            }

            return bytes.ToArray();
        }
    }
}
