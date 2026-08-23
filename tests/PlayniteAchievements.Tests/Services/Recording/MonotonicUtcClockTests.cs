using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Common;

namespace PlayniteAchievements.Tests.Services.Recording
{
    [TestClass]
    public class MonotonicUtcClockTests
    {
        [TestMethod]
        public void UtcNow_UsesCounterElapsedTime_NotWallClock()
        {
            var origin = new DateTime(2026, 8, 12, 12, 0, 0, DateTimeKind.Utc);
            var projected = CaptureTimelineClock.Project(origin, 1_000, 9_125, 1_000);

            Assert.AreEqual(origin.AddMilliseconds(8125), projected);
            Assert.AreEqual(DateTimeKind.Utc, projected.Kind);
        }

        [TestMethod]
        public void FromTimestamp_PreservesFractionalTicksWithoutAccumulationDrift()
        {
            var origin = new DateTime(2026, 8, 12, 12, 0, 0, DateTimeKind.Utc);
            Assert.AreEqual(origin.AddTicks(10_000_000), CaptureTimelineClock.Project(origin, 10, 13, 3));
            Assert.AreEqual(origin.AddTicks(20_000_000), CaptureTimelineClock.Project(origin, 10, 16, 3));
            Assert.AreEqual(origin.AddTicks(30_000_000), CaptureTimelineClock.Project(origin, 10, 19, 3));
        }

        [TestMethod]
        public void Project_RejectsInvalidFrequency()
        {
            Assert.ThrowsException<ArgumentOutOfRangeException>(
                () => CaptureTimelineClock.Project(DateTime.UtcNow, 0, 1, 0));
        }

        [TestMethod]
        public void TimestampTo100ns_UsesTheSameNonAccumulatingQpcGrid()
        {
            Assert.AreEqual(3_333_333L, CaptureTimelineClock.TimestampTo100ns(1, 3));
            Assert.AreEqual(6_666_666L, CaptureTimelineClock.TimestampTo100ns(2, 3));
            Assert.AreEqual(10_000_000L, CaptureTimelineClock.TimestampTo100ns(3, 3));
            Assert.AreEqual(30_000_000L, CaptureTimelineClock.TimestampTo100ns(9, 3));
            Assert.ThrowsException<ArgumentOutOfRangeException>(
                () => CaptureTimelineClock.TimestampTo100ns(1, 0));
        }
    }
}
