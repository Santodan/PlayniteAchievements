using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services.Recording;

namespace PlayniteAchievements.Services.Tests.Recording
{
    [TestClass]
    public class SurroundDownmixTests
    {
        private const float Fold = 0.70710678f;

        private static byte[] Frame(params float[] samples)
        {
            var bytes = new byte[samples.Length * 4];
            Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
            return bytes;
        }

        private static float[] Stereo(byte[] output)
        {
            var samples = new float[output.Length / 4];
            Buffer.BlockCopy(output, 0, samples, 0, output.Length);
            return samples;
        }

        [TestMethod]
        public void EightChannels_FoldsEverythingWhenNoControllerIsPresent()
        {
            // FL FR C LFE BL BR SL SR
            var input = Frame(0.1f, 0.2f, 0.1f, 0.05f, 0.2f, 0.3f, 0.3f, 0.4f);
            var output = Stereo(SurroundDownmix.ToStereo(input, input.Length, 8, dropBackChannels: false));

            Assert.AreEqual(2, output.Length);
            Assert.AreEqual(0.1f + Fold * (0.1f + 0.05f + 0.2f + 0.3f), output[0], 1e-6f);
            Assert.AreEqual(0.2f + Fold * (0.1f + 0.05f + 0.3f + 0.4f), output[1], 1e-6f);
        }

        [TestMethod]
        public void EightChannels_DropsOnlyTheBackPairWhenAControllerIsPresent()
        {
            // The actuators land on BL/BR by speaker position; center, LFE and the sides survive.
            var input = Frame(0.1f, 0.2f, 0.3f, 0.05f, 0.9f, 0.9f, 0.6f, 0.7f);
            var output = Stereo(SurroundDownmix.ToStereo(input, input.Length, 8, dropBackChannels: true));

            Assert.AreEqual(0.1f + Fold * (0.3f + 0.05f + 0.6f), output[0], 1e-6f);
            Assert.AreEqual(0.2f + Fold * (0.3f + 0.05f + 0.7f), output[1], 1e-6f);
        }

        [TestMethod]
        public void FourChannels_BackPairIsChannelsTwoAndThree()
        {
            var input = Frame(0.1f, 0.2f, 0.9f, 0.8f);
            var folded = Stereo(SurroundDownmix.ToStereo(input, input.Length, 4, dropBackChannels: false));
            var dropped = Stereo(SurroundDownmix.ToStereo(input, input.Length, 4, dropBackChannels: true));

            Assert.AreEqual(0.1f + Fold * 0.9f, folded[0], 1e-6f);
            Assert.AreEqual(0.2f + Fold * 0.8f, folded[1], 1e-6f);
            Assert.AreEqual(0.1f, dropped[0], 1e-6f);
            Assert.AreEqual(0.2f, dropped[1], 1e-6f);
        }

        [TestMethod]
        public void SixChannels_CenterAndLfeGoToBothSides()
        {
            // FL FR C LFE BL BR
            var input = Frame(0f, 0f, 0.4f, 0.2f, 0.5f, 0.6f);
            var dropped = Stereo(SurroundDownmix.ToStereo(input, input.Length, 6, dropBackChannels: true));

            Assert.AreEqual(Fold * 0.6f, dropped[0], 1e-6f);
            Assert.AreEqual(Fold * 0.6f, dropped[1], 1e-6f);
        }

        [TestMethod]
        public void Output_IsClampedAndSizedByWholeFrames()
        {
            var input = Frame(0.9f, -0.9f, 0f, 0f, 0.9f, -0.9f, 0.9f, -0.9f, /* half a frame */ 1f, 1f);
            var output = SurroundDownmix.ToStereo(input, input.Length, 8, dropBackChannels: false);

            Assert.AreEqual(8, output.Length, "one whole frame in, one stereo frame out");
            var samples = Stereo(output);
            Assert.AreEqual(1f, samples[0]);
            Assert.AreEqual(-1f, samples[1]);
        }

        [TestMethod]
        public void UnsupportedChannelCounts_ReturnNull()
        {
            Assert.IsNull(SurroundDownmix.ToStereo(new byte[16], 16, 2, false));
            Assert.IsNull(SurroundDownmix.ToStereo(new byte[20], 20, 5, false));
            Assert.IsNull(SurroundDownmix.ToStereo(null, 0, 8, false));
        }
    }
}
