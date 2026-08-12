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
                Start.AddSeconds(20),
                hasProgressSource: true,
                isRemote: false,
                equivalent: false);

            Assert.AreEqual(DateTime.MaxValue, schedule.NextDueUtc);
            Assert.IsFalse(schedule.ShouldEmitUnlocks());

            schedule.SourceAttached(Start.AddMilliseconds(10));

            Assert.AreEqual(Start.AddMilliseconds(10), schedule.NextDueUtc);
            schedule.BeginRead(Start.AddMilliseconds(10));
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

            schedule.BeginRead(Start.AddMilliseconds(1750));
            Assert.AreEqual(Start.AddMilliseconds(1250), schedule.ActiveReadObservedUtc);
            schedule.SignalFile(Start.AddMilliseconds(1800), watcherError: false, TimeSpan.FromMilliseconds(500));
            schedule.Succeeded(Start.AddMilliseconds(1850), TimeSpan.FromSeconds(60));

            Assert.IsTrue(schedule.Dirty);
            Assert.AreEqual(Start.AddMilliseconds(2300), schedule.NextDueUtc);

            // The event that arrived during the previous read belongs to the next read, rather
            // than being consumed or replaced with the scheduler's start time.
            schedule.BeginRead(Start.AddMilliseconds(2300));
            Assert.AreEqual(Start.AddMilliseconds(1800), schedule.ActiveReadObservedUtc);
        }

        [TestMethod]
        public void FailedRead_RetainsFileObservation_AndSuccessDoesNotReuseIt()
        {
            var schedule = PrimedLocalSchedule();
            var fileEvent = Start.AddSeconds(5);
            schedule.SignalFile(fileEvent, watcherError: false, TimeSpan.Zero);

            schedule.BeginRead(Start.AddSeconds(5.1));
            Assert.AreEqual(fileEvent, schedule.ActiveReadObservedUtc);
            schedule.Failed(Start.AddSeconds(5.2), new[] { 100 }, TimeSpan.FromSeconds(15));

            schedule.BeginRead(Start.AddSeconds(5.3));
            Assert.AreEqual(fileEvent, schedule.ActiveReadObservedUtc);
            schedule.Succeeded(Start.AddSeconds(5.4), TimeSpan.FromSeconds(60));

            schedule.BeginRead(Start.AddSeconds(6));
            Assert.AreEqual(
                Start.AddSeconds(6),
                schedule.ActiveReadObservedUtc,
                "A consumed watcher event must not become every later read's observation time.");
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
        public void RemoteSource_PrimesSilently_WhileFallbackKeepsStartupGrace()
        {
            var remote = new InGameReadSchedule();
            remote.Configure(
                Start,
                Start.AddSeconds(20),
                hasProgressSource: true,
                isRemote: true,
                equivalent: false);

            // A remote source is scheduled to read immediately, but like every other source it
            // establishes its baseline silently on that first read and only emits thereafter.
            Assert.AreEqual(Start, remote.NextDueUtc);
            Assert.IsFalse(remote.ShouldEmitUnlocks());
            remote.BeginRead(Start);
            remote.Succeeded(Start.AddSeconds(1), TimeSpan.FromSeconds(15));
            Assert.IsTrue(remote.ShouldEmitUnlocks());

            var fallback = new InGameReadSchedule();
            fallback.Configure(
                Start,
                Start.AddSeconds(20),
                hasProgressSource: false,
                isRemote: false,
                equivalent: false);

            Assert.AreEqual(Start.AddSeconds(20), fallback.NextDueUtc);
            fallback.MarkFallbackSuccess(Start.AddSeconds(21), TimeSpan.FromSeconds(15));
            fallback.Configure(
                Start.AddSeconds(22),
                Start.AddSeconds(20),
                hasProgressSource: false,
                isRemote: false,
                equivalent: true);
            Assert.AreEqual(
                Start.AddSeconds(36),
                fallback.NextDueUtc,
                "Equivalent reconfiguration must not reset an existing fallback deadline.");
        }

        private static InGameReadSchedule PrimedLocalSchedule()
        {
            var schedule = new InGameReadSchedule();
            schedule.Configure(
                Start,
                Start.AddSeconds(20),
                hasProgressSource: true,
                isRemote: false,
                equivalent: false);
            schedule.SourceAttached(Start);
            schedule.BeginRead(Start);
            schedule.Succeeded(Start, TimeSpan.FromSeconds(60));
            return schedule;
        }
    }
}
