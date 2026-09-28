using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models;
using PlayniteAchievements.Services.Achievements;

namespace PlayniteAchievements.Services.Tests
{
    [TestClass]
    public class TimelineBucketingTests
    {
        private static readonly DateTime Jan1 = new DateTime(2026, 1, 1);
        private static readonly IReadOnlyDictionary<DateTime, int> NoCounts = new Dictionary<DateTime, int>();

        [DataTestMethod]
        [DataRow(7, TimelineBucketUnit.Day)]
        [DataRow(14, TimelineBucketUnit.Day)]
        [DataRow(31, TimelineBucketUnit.Day)]
        [DataRow(92, TimelineBucketUnit.Week)]
        [DataRow(200, TimelineBucketUnit.Week)]
        [DataRow(365, TimelineBucketUnit.Month)]
        [DataRow(730, TimelineBucketUnit.Month)]
        [DataRow(1095, TimelineBucketUnit.Month)]
        [DataRow(1460, TimelineBucketUnit.Quarter)]
        [DataRow(2920, TimelineBucketUnit.Quarter)]
        [DataRow(5475, TimelineBucketUnit.Year)]
        public void Auto_PicksUnitNearestTargetBarCount(int spanDays, TimelineBucketUnit expected)
        {
            var plan = TimelineBucketing.Build(Jan1, Jan1.AddDays(spanDays - 1), NoCounts, TimelineGranularity.Auto);

            Assert.AreEqual(expected, plan.Unit, $"{spanDays} days gave {plan.Buckets.Count} buckets");
            Assert.IsTrue(plan.Buckets.Count >= 7 && plan.Buckets.Count <= 63, $"{plan.Buckets.Count} buckets for {spanDays} days");
        }

        [TestMethod]
        public void Override_Day_IsHonoredUpToTheCap()
        {
            var plan = TimelineBucketing.Build(Jan1, Jan1.AddDays(91), NoCounts, TimelineGranularity.Day);

            Assert.AreEqual(TimelineBucketUnit.Day, plan.Unit);
            Assert.AreEqual(92, plan.Buckets.Count);
        }

        [TestMethod]
        public void Override_Day_EscalatesPastTheCap()
        {
            var plan = TimelineBucketing.Build(Jan1, Jan1.AddDays(799), NoCounts, TimelineGranularity.Day);

            Assert.AreEqual(TimelineBucketUnit.Week, plan.Unit);
            Assert.IsTrue(plan.Buckets.Count <= TimelineBucketing.MaxOverrideBarCount);
        }

        [TestMethod]
        public void Override_Month_OnShortWindow_IsOneBucket()
        {
            var plan = TimelineBucketing.Build(new DateTime(2026, 1, 5), new DateTime(2026, 1, 14), NoCounts, TimelineGranularity.Month);

            Assert.AreEqual(TimelineBucketUnit.Month, plan.Unit);
            Assert.AreEqual(1, plan.Buckets.Count);
            Assert.AreEqual(new DateTime(2026, 1, 5), plan.Buckets[0].Start);
            Assert.AreEqual(new DateTime(2026, 1, 14), plan.Buckets[0].End);
            Assert.IsTrue(plan.Buckets[0].IsPartial);
        }

        [TestMethod]
        public void MonthlyYear_HasThirteenBucketsWithClippedEnds()
        {
            var plan = TimelineBucketing.Build(new DateTime(2025, 9, 29), new DateTime(2026, 9, 28), NoCounts, TimelineGranularity.Month);

            Assert.AreEqual(13, plan.Buckets.Count);
            var first = plan.Buckets[0];
            Assert.AreEqual(new DateTime(2025, 9, 1), first.PeriodStart);
            Assert.AreEqual(new DateTime(2025, 9, 29), first.Start);
            Assert.AreEqual(new DateTime(2025, 9, 30), first.End);
            Assert.IsTrue(first.IsPartial);
            var middle = plan.Buckets[1];
            Assert.AreEqual(new DateTime(2025, 10, 1), middle.Start);
            Assert.AreEqual(new DateTime(2025, 10, 31), middle.End);
            Assert.IsFalse(middle.IsPartial);
            var last = plan.Buckets[12];
            Assert.AreEqual(new DateTime(2026, 9, 1), last.Start);
            Assert.AreEqual(new DateTime(2026, 9, 28), last.End);
            Assert.IsTrue(last.IsPartial);
        }

        [TestMethod]
        public void Weeks_StartOnMonday()
        {
            var wednesday = new DateTime(2026, 9, 30);
            var plan = TimelineBucketing.Build(wednesday, wednesday.AddDays(20), NoCounts, TimelineGranularity.Week);

            Assert.AreEqual(new DateTime(2026, 9, 28), plan.Buckets[0].PeriodStart);
            Assert.AreEqual(DayOfWeek.Monday, plan.Buckets[0].PeriodStart.DayOfWeek);
            Assert.AreEqual(wednesday, plan.Buckets[0].Start);
            Assert.AreEqual(new DateTime(2026, 10, 4), plan.Buckets[0].End);
            Assert.AreEqual(4, plan.Buckets.Count);
        }

        [TestMethod]
        public void MonthStepping_FromJanuaryThirtyFirst_DoesNotSkipFebruary()
        {
            var plan = TimelineBucketing.Build(new DateTime(2026, 1, 31), new DateTime(2026, 3, 15), NoCounts, TimelineGranularity.Month);

            CollectionAssert.AreEqual(
                new[] { new DateTime(2026, 1, 1), new DateTime(2026, 2, 1), new DateTime(2026, 3, 1) },
                plan.Buckets.Select(b => b.PeriodStart).ToArray());
        }

        [TestMethod]
        public void Counts_AreClippedToTheWindowAndNormalized()
        {
            var start = new DateTime(2026, 2, 10);
            var end = new DateTime(2026, 2, 14);
            var counts = new Dictionary<DateTime, int>
            {
                { start.AddDays(-1), 100 },
                { start, 1 },
                { new DateTime(2026, 2, 12, 13, 45, 0, DateTimeKind.Utc), 2 },
                { end, 3 },
                { end.AddDays(1), 100 },
                { new DateTime(2026, 2, 11), 0 },
                { new DateTime(2026, 2, 13), -4 }
            };

            var plan = TimelineBucketing.Build(start, end, counts, TimelineGranularity.Day);

            CollectionAssert.AreEqual(new[] { 1, 0, 2, 0, 3 }, plan.Buckets.Select(b => b.Count).ToArray());
            Assert.AreEqual(6, plan.Total);
            Assert.AreEqual(3, plan.Max);
        }

        [TestMethod]
        public void DailyBuckets_AcrossDst_AreConsecutiveDays()
        {
            var plan = TimelineBucketing.Build(new DateTime(2026, 3, 2), new DateTime(2026, 3, 15), NoCounts, TimelineGranularity.Day);

            Assert.AreEqual(14, plan.Buckets.Count);
            for (var i = 1; i < plan.Buckets.Count; i++)
            {
                Assert.AreEqual(plan.Buckets[i - 1].Start.AddDays(1), plan.Buckets[i].Start);
                Assert.AreEqual(TimeSpan.Zero, plan.Buckets[i].Start.TimeOfDay);
            }
        }

        [TestMethod]
        public void EmptyWindow_YieldsOneBucket()
        {
            var plan = TimelineBucketing.Build(Jan1, Jan1, null, TimelineGranularity.Auto);

            Assert.AreEqual(1, plan.Buckets.Count);
            Assert.AreEqual(0, plan.Total);
            Assert.AreEqual(0, plan.Max);
        }

        [TestMethod]
        public void ReversedWindow_CollapsesToStart()
        {
            var plan = TimelineBucketing.Build(Jan1, Jan1.AddDays(-10), NoCounts, TimelineGranularity.Auto);

            Assert.AreEqual(1, plan.Buckets.Count);
            Assert.AreEqual(Jan1, plan.Buckets[0].Start);
        }

        [TestMethod]
        public void MonthlyCounts_AggregateByCalendarMonth()
        {
            var counts = new Dictionary<DateTime, int>
            {
                { new DateTime(2026, 1, 3), 1 },
                { new DateTime(2026, 1, 30), 2 },
                { new DateTime(2026, 2, 1), 5 },
                { new DateTime(2026, 3, 31), 7 }
            };

            var plan = TimelineBucketing.Build(new DateTime(2026, 1, 1), new DateTime(2026, 3, 31), counts, TimelineGranularity.Month);

            CollectionAssert.AreEqual(new[] { 3, 5, 7 }, plan.Buckets.Select(b => b.Count).ToArray());
        }
    }
}
