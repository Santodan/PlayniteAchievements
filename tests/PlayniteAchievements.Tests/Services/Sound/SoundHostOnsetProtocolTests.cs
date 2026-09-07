using System.Globalization;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services.Sound;

namespace PlayniteAchievements.Tests.Services.Sound
{
    [TestClass]
    public class SoundHostOnsetProtocolTests
    {
        [TestMethod]
        public void Started_CarriesTheMeasuredAudibleDelayWhenTheHostReportsOne()
        {
            var line = SoundHostProtocol.EncodeStarted(7, 123456789012345L, 41.257);
            Assert.AreEqual("started\t7\t123456789012345\t41.257", line);

            Assert.IsTrue(SoundHostProtocol.TryParse(line, out var message));
            Assert.AreEqual(7, message.Id);
            Assert.AreEqual(123456789012345L, message.Qpc);
            Assert.AreEqual(41.257, message.AudibleDelayMs.Value, 1e-9);
        }

        [TestMethod]
        public void Started_WithoutADelayStillParsesAndReportsNone()
        {
            // An older host, or one whose audio client could not be reached, omits the field; the
            // plugin then falls back to its modelled alignment.
            var line = SoundHostProtocol.EncodeStarted(7, 99L);
            Assert.AreEqual("started\t7\t99", line);

            Assert.IsTrue(SoundHostProtocol.TryParse(line, out var message));
            Assert.IsNull(message.AudibleDelayMs);
        }

        [TestMethod]
        public void Started_DelayIsInvariantAndBounded()
        {
            var previous = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                Assert.IsTrue(SoundHostProtocol.TryParse(SoundHostProtocol.EncodeStarted(1, 5L, 30.5), out var message));
                Assert.AreEqual(30.5, message.AudibleDelayMs.Value, 1e-9);
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = previous;
            }

            // Garbage or implausible delays are dropped rather than trusted.
            Assert.IsTrue(SoundHostProtocol.TryParse("started\t1\t5\tabc", out var garbage));
            Assert.IsNull(garbage.AudibleDelayMs);
            Assert.IsTrue(SoundHostProtocol.TryParse("started\t1\t5\t-4", out var negative));
            Assert.IsNull(negative.AudibleDelayMs);
            Assert.IsTrue(SoundHostProtocol.TryParse("started\t1\t5\t99999", out var huge));
            Assert.IsNull(huge.AudibleDelayMs);
        }
    }
}
