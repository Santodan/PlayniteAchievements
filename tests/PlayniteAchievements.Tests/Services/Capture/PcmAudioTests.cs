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
