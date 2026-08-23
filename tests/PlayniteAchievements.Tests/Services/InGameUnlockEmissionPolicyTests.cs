using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services.InGameMonitoring;
using System;

namespace PlayniteAchievements.Tests.Services
{
    [TestClass]
    public class InGameUnlockEmissionPolicyTests
    {
        private static readonly DateTime SessionStart =
            new DateTime(2026, 8, 11, 18, 0, 0, DateTimeKind.Utc);

        [TestMethod]
        public void PrimedRead_EmitsRegardlessOfUnlockTime()
        {
            Assert.IsTrue(InGameUnlockEmissionPolicy.ShouldEmit(true, SessionStart, null));
            Assert.IsTrue(InGameUnlockEmissionPolicy.ShouldEmit(
                true,
                SessionStart,
                SessionStart.AddYears(-3)));
            Assert.IsTrue(InGameUnlockEmissionPolicy.ShouldEmit(
                true,
                SessionStart,
                SessionStart.AddMinutes(5)));
        }

        [TestMethod]
        public void BaselineRead_SuppressesEarnedBacklog()
        {
            Assert.IsFalse(
                InGameUnlockEmissionPolicy.ShouldEmit(false, SessionStart, SessionStart.AddYears(-3)),
                "A pre-session unlock is backlog and must never notify on the baseline read.");
            Assert.IsFalse(
                InGameUnlockEmissionPolicy.ShouldEmit(false, SessionStart, SessionStart.AddSeconds(-1)),
                "One second before the session start is still backlog.");
        }

        [TestMethod]
        public void BaselineRead_SuppressesUnlockWithNoTimestamp()
        {
            // A provider that supplies no unlock time gives no evidence the unlock happened in this
            // session, so the baseline read stays silent rather than guessing.
            Assert.IsFalse(InGameUnlockEmissionPolicy.ShouldEmit(false, SessionStart, null));
        }

        [TestMethod]
        public void BaselineRead_EmitsUnlockStampedInsideSession()
        {
            Assert.IsTrue(
                InGameUnlockEmissionPolicy.ShouldEmit(false, SessionStart, SessionStart),
                "An unlock stamped exactly at session start counts as in-session.");
            Assert.IsTrue(
                InGameUnlockEmissionPolicy.ShouldEmit(false, SessionStart, SessionStart.AddSeconds(30)));
        }

        [TestMethod]
        public void BaselineRead_NormalizesNonUtcUnlockTimeBeforeComparing()
        {
            // Same instant expressed as local time must decide identically, whatever the machine
            // timezone: a provider handing back a local DateTime cannot flip the gate.
            var inSessionUtc = SessionStart.AddMinutes(10);
            var inSessionLocal = inSessionUtc.ToLocalTime();
            Assert.AreEqual(DateTimeKind.Local, inSessionLocal.Kind);
            Assert.IsTrue(InGameUnlockEmissionPolicy.ShouldEmit(false, SessionStart, inSessionLocal));

            var backlogLocal = SessionStart.AddHours(-10).ToLocalTime();
            Assert.IsFalse(InGameUnlockEmissionPolicy.ShouldEmit(false, SessionStart, backlogLocal));
        }
    }
}
