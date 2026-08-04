using System;
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
            // A fraction that lands mid-frame rounds down to a 4-byte boundary.
            var bytes = PcmAudio.TicksToAlignedBytes(12_345);
            Assert.AreEqual(0, bytes % PcmAudio.BlockAlign);
        }
    }
}
