using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services.InGameMonitoring;
using System;

namespace PlayniteAchievements.Tests.Services
{
    [TestClass]
    public class InGameReadScheduleTests
    {
        private static readonly DateTime Start =
            new DateTime(2026, 7, 28, 12, 0, 0, DateTimeKind.Utc);

        [TestMethod]
        public void LocalSource_AttachesBeforeImmediateBaseline_AndSuppressesStaleUnlocks()
        {
            var schedule = new InGameReadSchedule();

            schedule.Configure(
                Start,
                hasProgressSource: true,
                isRemote: false,
                equivalent: false);

            Assert.AreEqual(DateTime.MaxValue, schedule.NextDueUtc);
            Assert.IsFalse(schedule.ShouldEmitUnlocks());

            schedule.SourceAttached(Start.AddMilliseconds(10));

            Assert.AreEqual(Start.AddMilliseconds(10), schedule.NextDueUtc);
            schedule.BeginRead();
            schedule.Succeeded(Start.AddMilliseconds(20), TimeSpan.FromSeconds(60));
            Assert.IsTrue(schedule.ShouldEmitUnlocks());
            Assert.AreEqual(Start.AddMilliseconds(20).AddSeconds(60), schedule.NextDueUtc);
        }

        [TestMethod]
        public void FileSignals_UseTrailingDebounce_AndEventsDuringReadWinOverSafetyCadence()
        {
            var schedule = PrimedLocalSchedule();

            schedule.SignalFile(Start.AddSeconds(1), watcherError: false, TimeSpan.FromMilliseconds(500));
            schedule.SignalFile(Start.AddMilliseconds(1250), watcherError: false, TimeSpan.FromMilliseconds(500));
            Assert.AreEqual(Start.AddMilliseconds(1750), schedule.NextDueUtc);

            schedule.BeginRead();
            schedule.SignalFile(Start.AddMilliseconds(1800), watcherError: false, TimeSpan.FromMilliseconds(500));
            schedule.Succeeded(Start.AddMilliseconds(1850), TimeSpan.FromSeconds(60));

            Assert.IsTrue(schedule.Dirty);
            Assert.AreEqual(Start.AddMilliseconds(2300), schedule.NextDueUtc);
        }

        [TestMethod]
        public void FailedReads_RetryWithoutAdvancingBaseline_ThenUseDegradedCadence()
        {
            var schedule = PrimedLocalSchedule();
            var retries = new[] { 100, 250, 500, 1000 };
            var now = Start.AddSeconds(1);

            foreach (var retry in retries)
            {
                schedule.Failed(now, retries, TimeSpan.FromSeconds(15));
                Assert.AreEqual(now.AddMilliseconds(retry), schedule.NextDueUtc);
                Assert.IsTrue(schedule.Primed, "A failed read must not replace the last good baseline.");
                Assert.IsFalse(schedule.Degraded);
                now = now.AddSeconds(1);
            }

            schedule.Failed(now, retries, TimeSpan.FromSeconds(15));
            Assert.IsTrue(schedule.Degraded);
            Assert.AreEqual(now.AddSeconds(15), schedule.NextDueUtc);
            Assert.AreEqual(0, schedule.RetryAttempt);

            schedule.Succeeded(now.AddSeconds(2), TimeSpan.FromSeconds(60));
            Assert.IsFalse(schedule.Degraded);
            Assert.AreEqual(now.AddSeconds(62), schedule.NextDueUtc);
        }

        [TestMethod]
        public void WatcherError_ReconcilesImmediately_AndEquivalentReconfigurePreservesBaseline()
        {
            var schedule = PrimedLocalSchedule();
            schedule.SignalFile(Start.AddSeconds(3), watcherError: true, TimeSpan.FromMilliseconds(500));

            Assert.IsTrue(schedule.Degraded);
            Assert.AreEqual(Start.AddSeconds(3), schedule.NextDueUtc);

            schedule.Configure(
                Start.AddSeconds(4),
                Start.AddSeconds(20),
                hasProgressSource: true,
                isRemote: false,
                equivalent: true);

            Assert.IsTrue(schedule.Primed);
            Assert.IsTrue(schedule.Dirty);
            Assert.AreEqual(Start.AddSeconds(4), schedule.NextDueUtc);
        }

        [TestMethod]
        public void RemoteSource_PrimesSilently_AndNoFastSourceParksTheSchedule()
        {
            var remote = new InGameReadSchedule();
            remote.Configure(
                Start,
                hasProgressSource: true,
                isRemote: true,
                equivalent: false);

            // A remote source is scheduled to read immediately, but like every other source it
            // establishes its baseline silently on that first read and only emits thereafter.
            Assert.AreEqual(Start, remote.NextDueUtc);
            Assert.IsFalse(remote.ShouldEmitUnlocks());
            remote.BeginRead();
            remote.Succeeded(Start.AddSeconds(1), TimeSpan.FromSeconds(15));
            Assert.IsTrue(remote.ShouldEmitUnlocks());

            var noFastSource = new InGameReadSchedule();
            noFastSource.Configure(
                Start,
                hasProgressSource: false,
                isRemote: false,
                equivalent: false);

            Assert.AreEqual(
                DateTime.MaxValue,
                noFastSource.NextDueUtc,
                "With no fast source this schedule drives nothing; the universal refresh prong " +
                "keeps its own deadline outside this type.");
            Assert.IsFalse(noFastSource.Dirty);
        }

        [TestMethod]
        public void MarkPrimed_EstablishesBaseline_WithoutDisturbingTheFastProng()
        {
            var schedule = new InGameReadSchedule();
            schedule.Configure(
                Start,
                hasProgressSource: true,
                isRemote: false,
                equivalent: false);
            schedule.SignalFile(Start.AddSeconds(1), watcherError: false, TimeSpan.FromMilliseconds(500));
            var pendingDueUtc = schedule.NextDueUtc;
            Assert.IsFalse(schedule.ShouldEmitUnlocks());

            schedule.MarkPrimed();

            Assert.IsTrue(
                schedule.ShouldEmitUnlocks(),
                "The refresh prong reading first must establish the shared session baseline.");
            Assert.IsTrue(
                schedule.Dirty,
                "A pending file event must survive the refresh prong priming.");
            Assert.AreEqual(
                pendingDueUtc,
                schedule.NextDueUtc,
                "The refresh prong must not move the fast prong's deadline.");

            var degraded = new InGameReadSchedule();
            degraded.Configure(
                Start,
                hasProgressSource: true,
                isRemote: false,
                equivalent: false);
            degraded.SignalFile(Start, watcherError: true, TimeSpan.FromMilliseconds(500));
            degraded.MarkPrimed();
            Assert.IsTrue(
                degraded.Degraded,
                "A refresh prong success says nothing about the fast source's health.");
        }

        private static InGameReadSchedule PrimedLocalSchedule()
        {
            var schedule = new InGameReadSchedule();
            schedule.Configure(
                Start,
                hasProgressSource: true,
                isRemote: false,
                equivalent: false);
            schedule.SourceAttached(Start);
            schedule.BeginRead();
            schedule.Succeeded(Start, TimeSpan.FromSeconds(60));
            return schedule;
        }
    }
}
