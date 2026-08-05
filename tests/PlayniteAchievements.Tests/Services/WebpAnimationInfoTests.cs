using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services.Images;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PlayniteAchievements.Services.Tests
{
    /// <summary>
    /// Covers the RIFF walk that supplies animated WebP frame delays. WIC reports a WebP's frame
    /// count but exposes no metadata for it, so these durations are the only timing source.
    /// </summary>
    [TestClass]
    public class WebpAnimationInfoTests
    {
        // A 13-byte lossless 1x1 VP8L bitstream, reused as the payload of every frame. Its content
        // is irrelevant here; only the container structure around it is under test.
        private static readonly byte[] Vp8LOnePixel =
        {
            0x2F, 0x00, 0x00, 0x00, 0x10, 0x07, 0x10, 0x11, 0x11, 0x88, 0x88, 0xFE, 0x07
        };

        private string _directory;

        [TestInitialize]
        public void Setup()
        {
            _directory = Path.Combine(Path.GetTempPath(), "PlayniteAchievements", "WebpTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
        }

        [TestCleanup]
        public void Cleanup()
        {
            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch
            {
            }
        }

        [TestMethod]
        public void TryReadFrameDurations_ReadsEachAnmfDuration()
        {
            var path = WriteAnimatedWebp("three-frames.webp", 2, 2, 40, 250, 1000);

            Assert.IsTrue(WebpAnimationInfo.TryReadFrameDurations(path, out var durations));
            CollectionAssert.AreEqual(new List<int> { 40, 250, 1000 }, durations);
        }

        [TestMethod]
        public void TryReadFrameDurations_TreatsBelowFloorDurationsAsTheDefault()
        {
            // Encoders write 0 when they leave pacing to the viewer; a near-zero delay would
            // otherwise render as an invisible frame.
            var path = WriteAnimatedWebp("floor.webp", 2, 2, 0, 10, 19, 20);

            Assert.IsTrue(WebpAnimationInfo.TryReadFrameDurations(path, out var durations));
            CollectionAssert.AreEqual(new List<int> { 100, 100, 100, 20 }, durations);
        }

        [TestMethod]
        public void IsAnimated_TrueOnlyWhenTheHeaderDeclaresAnimation()
        {
            var animated = WriteAnimatedWebp("animated.webp", 2, 2, 100, 100);
            var still = WriteStillWebp("still.webp");

            Assert.IsTrue(WebpAnimationInfo.IsAnimated(animated));
            Assert.IsFalse(WebpAnimationInfo.IsAnimated(still));
        }

        [TestMethod]
        public void TryReadFrameDurations_RejectsAStillWebp()
        {
            var path = WriteStillWebp("still.webp");

            Assert.IsFalse(WebpAnimationInfo.TryReadFrameDurations(path, out var durations));
            Assert.IsNull(durations);
        }

        [TestMethod]
        public void TryReadFrameDurations_RejectsTruncatedAndForeignFiles()
        {
            var full = File.ReadAllBytes(WriteAnimatedWebp("full.webp", 2, 2, 100, 100));

            // Cut mid-chunk: the walk must stop rather than read past the end or spin.
            var truncated = Path.Combine(_directory, "truncated.webp");
            File.WriteAllBytes(truncated, Take(full, 30));

            var foreign = Path.Combine(_directory, "foreign.bin");
            File.WriteAllBytes(foreign, Encoding.ASCII.GetBytes("this is not a RIFF container"));

            var empty = Path.Combine(_directory, "empty.webp");
            File.WriteAllBytes(empty, new byte[0]);

            Assert.IsFalse(WebpAnimationInfo.TryReadFrameDurations(truncated, out _));
            Assert.IsFalse(WebpAnimationInfo.TryReadFrameDurations(foreign, out _));
            Assert.IsFalse(WebpAnimationInfo.TryReadFrameDurations(empty, out _));
            Assert.IsFalse(WebpAnimationInfo.IsAnimated(foreign));
            Assert.IsFalse(WebpAnimationInfo.IsAnimated(empty));
        }

        [TestMethod]
        public void Readers_ReturnFalseForAMissingFile()
        {
            var missing = Path.Combine(_directory, "does-not-exist.webp");

            Assert.IsFalse(WebpAnimationInfo.IsAnimated(missing));
            Assert.IsFalse(WebpAnimationInfo.TryReadFrameDurations(missing, out _));
        }

        [TestMethod]
        public void TryReadFrameDurations_SkipsUnrelatedChunksIncludingOddLengthPadding()
        {
            // An odd-sized chunk carries a pad byte that the size field does not count. Mishandling
            // it would desynchronize the walk and lose every following frame.
            var path = WriteAnimatedWebp(
                "padded.webp",
                2,
                2,
                new[] { 120, 240 },
                extraChunk: new KeyValuePair<string, byte[]>("XMP ", new byte[] { 1, 2, 3 }));

            Assert.IsTrue(WebpAnimationInfo.TryReadFrameDurations(path, out var durations));
            CollectionAssert.AreEqual(new List<int> { 120, 240 }, durations);
        }

        private string WriteStillWebp(string name)
        {
            var path = Path.Combine(_directory, name);
            using (var stream = new MemoryStream())
            {
                using (var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true))
                {
                    WriteTag(writer, "RIFF");
                    writer.Write((uint)0);
                    WriteTag(writer, "WEBP");
                    WriteChunk(writer, "VP8L", Vp8LOnePixel);
                }

                File.WriteAllBytes(path, FinalizeRiff(stream.ToArray()));
            }

            return path;
        }

        private string WriteAnimatedWebp(string name, int width, int height, params int[] durations)
        {
            return WriteAnimatedWebp(name, width, height, durations, null);
        }

        private string WriteAnimatedWebp(
            string name,
            int width,
            int height,
            int[] durations,
            KeyValuePair<string, byte[]>? extraChunk)
        {
            var path = Path.Combine(_directory, name);
            using (var stream = new MemoryStream())
            {
                using (var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true))
                {
                    WriteTag(writer, "RIFF");
                    writer.Write((uint)0);
                    WriteTag(writer, "WEBP");

                    // VP8X: flag bit 0x02 marks an animation; canvas dimensions are stored minus one.
                    var vp8x = new List<byte> { 0x02, 0, 0, 0 };
                    vp8x.AddRange(UInt24(width - 1));
                    vp8x.AddRange(UInt24(height - 1));
                    WriteChunk(writer, "VP8X", vp8x.ToArray());

                    var anim = new List<byte> { 0, 0, 0, 0, 0, 0 };
                    WriteChunk(writer, "ANIM", anim.ToArray());

                    if (extraChunk.HasValue)
                    {
                        WriteChunk(writer, extraChunk.Value.Key, extraChunk.Value.Value);
                    }

                    foreach (var duration in durations)
                    {
                        var anmf = new List<byte>();
                        anmf.AddRange(UInt24(0));               // frame x
                        anmf.AddRange(UInt24(0));               // frame y
                        anmf.AddRange(UInt24(0));               // frame width - 1
                        anmf.AddRange(UInt24(0));               // frame height - 1
                        anmf.AddRange(UInt24(duration));
                        anmf.Add(0);                            // blend / dispose flags
                        anmf.AddRange(ChunkBytes("VP8L", Vp8LOnePixel));
                        WriteChunk(writer, "ANMF", anmf.ToArray());
                    }
                }

                File.WriteAllBytes(path, FinalizeRiff(stream.ToArray()));
            }

            return path;
        }

        private static byte[] FinalizeRiff(byte[] bytes)
        {
            Array.Copy(BitConverter.GetBytes((uint)(bytes.Length - 8)), 0, bytes, 4, 4);
            return bytes;
        }

        private static void WriteTag(BinaryWriter writer, string tag)
        {
            writer.Write(Encoding.ASCII.GetBytes(tag));
        }

        private static void WriteChunk(BinaryWriter writer, string tag, byte[] payload)
        {
            writer.Write(ChunkBytes(tag, payload));
        }

        private static byte[] ChunkBytes(string tag, byte[] payload)
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

        private static byte[] UInt24(int value)
        {
            return new[]
            {
                (byte)(value & 0xFF),
                (byte)((value >> 8) & 0xFF),
                (byte)((value >> 16) & 0xFF)
            };
        }

        private static byte[] Take(byte[] source, int count)
        {
            var result = new byte[Math.Min(count, source.Length)];
            Array.Copy(source, result, result.Length);
            return result;
        }
    }
}
