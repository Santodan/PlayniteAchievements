using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Capture;

namespace PlayniteAchievements.Services.Tests.Capture
{
    [TestClass]
    public class BitrateMathTests
    {
        private const int Mbps = 1_000_000;

        [TestMethod]
        public void Native_MatchesTheRatesTheEncoderHasAlwaysUsed()
        {
            // 0.12 bits per pixel per frame, the reference every other tier scales from.
            Assert.AreEqual(15 * Mbps, Bitrate(1920, 1080, 60, RecordingQuality.Native), 100_000);
            Assert.AreEqual(27 * Mbps, Bitrate(2560, 1440, 60, RecordingQuality.Native), 500_000);
            Assert.AreEqual(60 * Mbps, Bitrate(3840, 2160, 60, RecordingQuality.Native), 500_000);
        }

        [TestMethod]
        public void LowerTiersReduceTheRate()
        {
            var native = Bitrate(3840, 2160, 60, RecordingQuality.Native);
            var high = Bitrate(3840, 2160, 60, RecordingQuality.High);
            var medium = Bitrate(3840, 2160, 60, RecordingQuality.Medium);
            var low = Bitrate(3840, 2160, 60, RecordingQuality.Low);

            Assert.IsTrue(low < medium, $"low={low} medium={medium}");
            Assert.IsTrue(medium < high, $"medium={medium} high={high}");
            Assert.IsTrue(high < native, $"high={high} native={native}");
        }

        [TestMethod]
        public void TiersStayDistinctBelowTheFloor()
        {
            // The whole point of scaling the floor with the tier. At 1080p30 the computed rate is
            // under the Native floor, so a fixed floor would give every tier the same 8 Mbps --
            // a setting that does nothing for the captures most likely to want smaller files.
            var native = Bitrate(1920, 1080, 30, RecordingQuality.Native);
            var high = Bitrate(1920, 1080, 30, RecordingQuality.High);
            var medium = Bitrate(1920, 1080, 30, RecordingQuality.Medium);
            var low = Bitrate(1920, 1080, 30, RecordingQuality.Low);

            Assert.AreEqual(8 * Mbps, native);
            Assert.IsTrue(low < medium && medium < high && high < native,
                $"low={low} medium={medium} high={high} native={native}");
        }

        [TestMethod]
        public void TiersStayDistinctAtTheCeiling()
        {
            // 8K60 exceeds every tier's ceiling; the ceilings scale too, so they do not converge.
            var native = Bitrate(7680, 4320, 60, RecordingQuality.Native);
            var low = Bitrate(7680, 4320, 60, RecordingQuality.Low);

            Assert.AreEqual(120 * Mbps, native);
            Assert.AreEqual(50 * Mbps, low);
        }

        [TestMethod]
        public void RateRisesWithFrameRateAndPixels()
        {
            Assert.IsTrue(
                Bitrate(1920, 1080, 60, RecordingQuality.Native) >
                Bitrate(1920, 1080, 30, RecordingQuality.Native));
            Assert.IsTrue(
                Bitrate(3840, 2160, 60, RecordingQuality.Native) >
                Bitrate(1920, 1080, 60, RecordingQuality.Native));
        }

        [TestMethod]
        public void DegenerateSizesFallBackToTheFloorRatherThanZero()
        {
            Assert.AreEqual(8 * Mbps, Bitrate(0, 1080, 30, RecordingQuality.Native));
            Assert.AreEqual(8 * Mbps, Bitrate(1920, 0, 30, RecordingQuality.Low));
            Assert.AreEqual(8 * Mbps, Bitrate(1920, 1080, 0, RecordingQuality.Medium));
        }

        private static int Bitrate(int width, int height, int fps, RecordingQuality quality)
        {
            return BitrateMath.Compute(width, height, fps, quality);
        }
    }
}
