using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services.Sound;

namespace PlayniteAchievements.Tests.Services.Sound
{
    [TestClass]
    public class SoundHostRestartPolicyTests
    {
        [TestMethod]
        public void NextDelay_BacksOffThenGivesUp()
        {
            Assert.AreEqual(TimeSpan.FromMilliseconds(500), SoundHostRestartPolicy.NextDelay(1));
            Assert.AreEqual(TimeSpan.FromSeconds(2), SoundHostRestartPolicy.NextDelay(2));
            Assert.AreEqual(TimeSpan.FromSeconds(8), SoundHostRestartPolicy.NextDelay(3));
            Assert.IsNull(SoundHostRestartPolicy.NextDelay(4));
            Assert.IsNull(SoundHostRestartPolicy.NextDelay(10));
        }

        [TestMethod]
        public void NextFailureCount_ResetsAfterAHealthyRun()
        {
            Assert.AreEqual(3, SoundHostRestartPolicy.NextFailureCount(2, TimeSpan.FromSeconds(1)));
            Assert.AreEqual(1, SoundHostRestartPolicy.NextFailureCount(2, SoundHostRestartPolicy.HealthyRunThreshold));
            Assert.AreEqual(1, SoundHostRestartPolicy.NextFailureCount(0, TimeSpan.FromMinutes(5)));
        }
    }
}
