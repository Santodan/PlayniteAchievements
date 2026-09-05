using System;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services.Capture;

namespace PlayniteAchievements.Services.Tests.Capture
{
    [TestClass]
    public class PcmAudioTests
    {
        private static byte[] Samples(params short[] values)
        {
            var bytes = new byte[values.Length * 2];
            Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
            return bytes;
        }

        private static short[] ToShorts(byte[] bytes)
        {
            var values = new short[bytes.Length / 2];
            Buffer.BlockCopy(bytes, 0, values, 0, values.Length * 2);
            return values;
        }

        /// <summary>Deterministic band-limited stereo noise (8-tap moving average of white noise).</summary>
        private static short[] BandLimitedNoise(int frames, int seed, int amplitude)
        {
            var random = new Random(seed);
            var raw = new double[frames + 16];
            for (var i = 0; i < raw.Length; i++)
            {
                raw[i] = random.Next(-amplitude, amplitude + 1);
            }

            var samples = new short[frames * 2];
            for (var frame = 0; frame < frames; frame++)
            {
                double left = 0;
                double right = 0;
                for (var k = 0; k < 8; k++)
                {
                    left += raw[frame + k];
                    right += raw[frame + k + 4];
                }

                samples[frame * 2] = (short)(left / 8);
                samples[frame * 2 + 1] = (short)(right / 8);
            }

            return samples;
        }

        [TestMethod]
        public void MixInto_AddsSamples()
        {
            var dest = Samples(100, -200, 300, 0);
            var source = Samples(50, -50, -300, 1000);

            PcmAudio.MixInto(dest, 0, source, 0, dest.Length);

            CollectionAssert.AreEqual(new short[] { 150, -250, 0, 1000 }, ToShorts(dest));
        }

        [TestMethod]
        public void MixInto_SaturatesInsteadOfWrapping()
        {
            var dest = Samples(short.MaxValue, short.MinValue);
            var source = Samples(1000, -1000);

            PcmAudio.MixInto(dest, 0, source, 0, dest.Length);

            CollectionAssert.AreEqual(new[] { short.MaxValue, short.MinValue }, ToShorts(dest));
        }

        [TestMethod]
        public void MixInto_RespectsOffsetsAndClampsToBothBuffers()
        {
            var dest = Samples(1, 2, 3, 4);
            var source = Samples(10, 20);

            // Source offset skips its first sample; dest offset starts at sample index 2; only
            // one source sample remains, so sample 3 of dest is untouched.
            PcmAudio.MixInto(dest, 4, source, 2, long.MaxValue);

            CollectionAssert.AreEqual(new short[] { 1, 2, 23, 4 }, ToShorts(dest));
        }

        [TestMethod]
        public void MixInto_InvalidInputs_NoOp()
        {
            var dest = Samples(5);

            PcmAudio.MixInto(dest, -1, Samples(1), 0, 2);
            PcmAudio.MixInto(dest, 0, null, 0, 2);
            PcmAudio.MixInto(dest, 0, Samples(1), 99, 2);

            CollectionAssert.AreEqual(new short[] { 5 }, ToShorts(dest));
        }

        [TestMethod]
        public void FadeOutTail_RampsToSilenceWithoutTouchingTheHead()
        {
            // 1 second of constant full-scale samples; fade the last half second.
            var pcm = new byte[PcmAudio.BytesPerSecond];
            for (var i = 0; i < pcm.Length; i += 2)
            {
                pcm[i] = 0xff;
                pcm[i + 1] = 0x3f; // 16383
            }

            PcmAudio.FadeOutTail(pcm, 0.5);

            short At(int byteOffset) => (short)(pcm[byteOffset] | (pcm[byteOffset + 1] << 8));
            // Head untouched.
            Assert.AreEqual(16383, At(0));
            Assert.AreEqual(16383, At(PcmAudio.BytesPerSecond / 2 - 4));
            // Mid-fade roughly half amplitude; final sample near silence.
            var mid = At(PcmAudio.BytesPerSecond * 3 / 4);
            Assert.IsTrue(mid > 6000 && mid < 10500, $"mid-fade was {mid}");
            Assert.IsTrue(Math.Abs(At(pcm.Length - 2)) < 50);
        }

        [TestMethod]
        public void FadeOutTail_ShortBufferOrInvalid_NoThrow()
        {
            PcmAudio.FadeOutTail(null, 1);
            PcmAudio.FadeOutTail(new byte[2], 1);
            var pcm = Samples(1000, 1000);
            PcmAudio.FadeOutTail(pcm, 0);
            CollectionAssert.AreEqual(new short[] { 1000, 1000 }, ToShorts(pcm));
        }

        [TestMethod]
        public void TicksToAlignedBytes_AlignsToBlockBoundary()
        {
            // 1 second = 192000 bytes; already aligned.
            Assert.AreEqual(192000L, PcmAudio.TicksToAlignedBytes(10_000_000));
            // One frame is 208.333 ticks. The nearest representable tick must still map back to
            // frame one rather than being truncated to three bytes and aligned onto frame zero.
            Assert.AreEqual(PcmAudio.BlockAlign, PcmAudio.TicksToAlignedBytes(208));
            Assert.AreEqual(PcmAudio.BlockAlign, PcmAudio.TicksToAlignedBytes(209));
            Assert.AreEqual(0, PcmAudio.TicksToAlignedBytes(104));
            // Every result is a whole stereo sample frame.
            var bytes = PcmAudio.TicksToAlignedBytes(12_345);
            Assert.AreEqual(0, bytes % PcmAudio.BlockAlign);
        }

        [TestMethod]
        public void WriteWav_WritesAReadableHeaderForTheExportFormat()
        {
            // A fallback clip window goes back to the exporter as an ordinary chunk file, so the
            // header has to describe exactly the format the rest of the pipeline assumes.
            var path = Path.Combine(Path.GetTempPath(), $"pa_wav_{Guid.NewGuid():N}.wav");
            var pcm = Samples(BandLimitedNoise(1200, 3, 4000));
            try
            {
                PcmAudio.WriteWav(path, pcm);
                var written = File.ReadAllBytes(path);

                Assert.AreEqual(44 + pcm.Length, written.Length);
                Assert.AreEqual("RIFF", Encoding.ASCII.GetString(written, 0, 4));
                Assert.AreEqual("WAVE", Encoding.ASCII.GetString(written, 8, 4));
                Assert.AreEqual("fmt ", Encoding.ASCII.GetString(written, 12, 4));
                Assert.AreEqual(1, BitConverter.ToInt16(written, 20));                       // PCM
                Assert.AreEqual(PcmAudio.Channels, BitConverter.ToInt16(written, 22));
                Assert.AreEqual(PcmAudio.SampleRate, BitConverter.ToInt32(written, 24));
                Assert.AreEqual(PcmAudio.BytesPerSecond, BitConverter.ToInt32(written, 28));
                Assert.AreEqual(PcmAudio.BlockAlign, BitConverter.ToInt16(written, 32));
                Assert.AreEqual(PcmAudio.BitsPerSample, BitConverter.ToInt16(written, 34));
                Assert.AreEqual("data", Encoding.ASCII.GetString(written, 36, 4));
                Assert.AreEqual(pcm.Length, BitConverter.ToInt32(written, 40));
                CollectionAssert.AreEqual(pcm, written.Skip(44).ToArray());
            }
            finally
            {
                try { File.Delete(path); } catch { }
            }
        }

        [TestMethod]
        public void IsSilent_TellsDigitalSilenceAndFaintNoiseFromSignal()
        {
            Assert.IsTrue(PcmAudio.IsSilent(null));
            Assert.IsTrue(PcmAudio.IsSilent(new byte[0]));
            Assert.IsTrue(PcmAudio.IsSilent(new byte[PcmAudio.BytesPerSecond]), "zero-filled is silent");
            // About -70 dBFS of noise: below the -60 dBFS default, so still "no game here".
            Assert.IsTrue(PcmAudio.IsSilent(Samples(BandLimitedNoise(4800, 7, 12))));
            // A quiet but real signal, about -40 dBFS, is not silent.
            Assert.IsFalse(PcmAudio.IsSilent(Samples(BandLimitedNoise(4800, 7, 900))));
            // The threshold is a parameter.
            Assert.IsFalse(PcmAudio.IsSilent(Samples(BandLimitedNoise(4800, 7, 12)), thresholdDbfs: -90));
        }
    }
}
