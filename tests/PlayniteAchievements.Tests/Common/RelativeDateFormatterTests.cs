using System;
using System.Globalization;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Common;

namespace PlayniteAchievements.Tests.Common
{
    [TestClass]
    public class RelativeDateFormatterTests
    {
        // Wednesday. The chosen sample dates resolve identically under Sunday- and Monday-start weeks,
        // but the culture is pinned for determinism regardless of the host machine.
        private static readonly DateTime Now = new DateTime(2026, 6, 24, 12, 0, 0, DateTimeKind.Local);

        private CultureInfo _originalCulture;

        [TestInitialize]
        public void Setup()
        {
            _originalCulture = Thread.CurrentThread.CurrentCulture;
            Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
        }

        [TestCleanup]
        public void Cleanup()
        {
            Thread.CurrentThread.CurrentCulture = _originalCulture;
        }

        [TestMethod]
        public void GetBucket_SameDay_ReturnsToday()
        {
            Assert.AreEqual(RelativeDateBucket.Today, RelativeDateFormatter.GetBucket(Now.AddHours(-3), Now));
        }

        [TestMethod]
        public void GetBucket_FutureValue_ReturnsToday()
        {
            Assert.AreEqual(RelativeDateBucket.Today, RelativeDateFormatter.GetBucket(Now.AddDays(1), Now));
        }

        [TestMethod]
        public void GetBucket_PreviousCalendarDay_ReturnsYesterday()
        {
            Assert.AreEqual(RelativeDateBucket.Yesterday, RelativeDateFormatter.GetBucket(new DateTime(2026, 6, 23), Now));
        }

        [TestMethod]
        public void GetBucket_EarlierThisWeek_ReturnsThisWeek()
        {
            // Monday of the current week; within the week but older than yesterday.
            Assert.AreEqual(RelativeDateBucket.ThisWeek, RelativeDateFormatter.GetBucket(new DateTime(2026, 6, 22), Now));
        }

        [TestMethod]
        public void GetBucket_EarlierThisMonthPriorWeek_ReturnsThisMonth()
        {
            Assert.AreEqual(RelativeDateBucket.ThisMonth, RelativeDateFormatter.GetBucket(new DateTime(2026, 6, 10), Now));
        }

        [TestMethod]
        public void GetBucket_PreviousCalendarWeek_ReturnsLastWeek()
        {
            Assert.AreEqual(RelativeDateBucket.LastWeek, RelativeDateFormatter.GetBucket(new DateTime(2026, 6, 15), Now));
        }

        [TestMethod]
        public void GetBucket_PreviousCalendarMonth_ReturnsLastMonth()
        {
            Assert.AreEqual(RelativeDateBucket.LastMonth, RelativeDateFormatter.GetBucket(new DateTime(2026, 5, 15), Now));
        }

        [TestMethod]
        public void GetBucket_EarlierThisYearPriorMonth_ReturnsThisYear()
        {
            Assert.AreEqual(RelativeDateBucket.ThisYear, RelativeDateFormatter.GetBucket(new DateTime(2026, 4, 15), Now));
        }

        [TestMethod]
        public void GetBucket_LastMonthAcrossYearBoundary_ReturnsLastMonth()
        {
            Assert.AreEqual(
                RelativeDateBucket.LastMonth,
                RelativeDateFormatter.GetBucket(new DateTime(2025, 12, 15), new DateTime(2026, 1, 15)));
        }

        [TestMethod]
        public void GetBucket_PriorYear_ReturnsLongAgo()
        {
            Assert.AreEqual(RelativeDateBucket.LongAgo, RelativeDateFormatter.GetBucket(new DateTime(2025, 6, 15), Now));
        }

        [TestMethod]
        public void GetYearsAgo_PriorCalendarYear_ReturnsOne()
        {
            // Calendar-year difference, consistent with the bucketing: late December of the
            // prior year is still "1 year ago" in early January.
            Assert.AreEqual(1, RelativeDateFormatter.GetYearsAgo(new DateTime(2025, 12, 31), new DateTime(2026, 1, 1)));
        }

        [TestMethod]
        public void GetYearsAgo_SeveralYearsBack_ReturnsCalendarYearDifference()
        {
            Assert.AreEqual(8, RelativeDateFormatter.GetYearsAgo(new DateTime(2018, 6, 15), Now));
        }
    }
}
