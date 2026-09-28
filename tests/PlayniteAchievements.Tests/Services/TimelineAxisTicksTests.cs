using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models;
using PlayniteAchievements.Services.Achievements;

namespace PlayniteAchievements.Services.Tests
{
    [TestClass]
    public class TimelineAxisTicksTests
    {
        private static readonly CultureInfo EnUs = CultureInfo.GetCultureInfo("en-US");
        private static readonly IReadOnlyDictionary<DateTime, int> NoCounts = new Dictionary<DateTime, int>();

        private static TimelineBucketPlan Plan(DateTime start, DateTime end, TimelineGranularity granularity) =>
            TimelineBucketing.Build(start, end, NoCounts, granularity);

        private static TimelineAxisLabels Ticks(TimelineBucketPlan plan, int maxTicks = TimelineAxisTicks.DefaultMaxTicks) =>
            TimelineAxisTicks.Plan(plan.Buckets, plan.Unit, maxTicks, EnUs);

        [TestMethod]
        public void SevenDays_LabelsEveryBar()
        {
            var labels = Ticks(Plan(new DateTime(2026, 9, 22), new DateTime(2026, 9, 28), TimelineGranularity.Day));

            Assert.AreEqual(7, labels.TickCount);
            CollectionAssert.AreEqual(new[] { "9/22", "9/23", "9/24", "9/25", "9/26", "9/27", "9/28" }, labels.AxisLabels.ToArray());
        }

        [TestMethod]
        public void FourteenDays_LabelsOddDays()
        {
            var labels = Ticks(Plan(new DateTime(2026, 1, 1), new DateTime(2026, 1, 14), TimelineGranularity.Day));

            Assert.AreEqual(7, labels.TickCount);
            Assert.AreEqual("1/1", labels.AxisLabels[0]);
            Assert.AreEqual(string.Empty, labels.AxisLabels[1]);
            Assert.AreEqual("1/13", labels.AxisLabels[12]);
        }

        [TestMethod]
        public void Month_OfDailyBars_LabelsMondays()
        {
            var labels = Ticks(Plan(new DateTime(2026, 1, 1), new DateTime(2026, 1, 31), TimelineGranularity.Day));

            Assert.AreEqual(4, labels.TickCount);
            CollectionAssert.AreEqual(
                new[] { "1/5", "1/12", "1/19", "1/26" },
                labels.AxisLabels.Where(l => l.Length > 0).ToArray());
        }

        [TestMethod]
        public void YearOfWeeks_LabelsQuarterStarts()
        {
            var labels = Ticks(Plan(new DateTime(2026, 1, 1), new DateTime(2026, 12, 31), TimelineGranularity.Week));

            Assert.AreEqual(4, labels.TickCount);
            CollectionAssert.AreEqual(
                new[] { "Jan 26", "Apr 26", "Jul 26", "Oct 26" },
                labels.AxisLabels.Where(l => l.Length > 0).ToArray());
        }

        [TestMethod]
        public void ThreeMonthsOfWeeks_LabelsMonthStarts()
        {
            var labels = Ticks(Plan(new DateTime(2026, 7, 1), new DateTime(2026, 9, 30), TimelineGranularity.Week));

            CollectionAssert.AreEqual(
                new[] { "Jul", "Aug", "Sep" },
                labels.AxisLabels.Where(l => l.Length > 0).ToArray());
        }

        [TestMethod]
        public void YearOfMonths_LabelsQuarterStarts()
        {
            var labels = Ticks(Plan(new DateTime(2026, 1, 1), new DateTime(2026, 12, 31), TimelineGranularity.Month));

            CollectionAssert.AreEqual(new[] { "Jan 26", "Apr 26", "Jul 26", "Oct 26" }, labels.AxisLabels.Where(l => l.Length > 0).ToArray());
        }

        [TestMethod]
        public void ThreeYearsOfMonths_LabelsJanuaries()
        {
            var labels = Ticks(Plan(new DateTime(2026, 1, 1), new DateTime(2028, 12, 31), TimelineGranularity.Month));

            CollectionAssert.AreEqual(new[] { "2026", "2027", "2028" }, labels.AxisLabels.Where(l => l.Length > 0).ToArray());
        }

        [TestMethod]
        public void SmallerMaxTicks_Coarsens()
        {
            var labels = Ticks(Plan(new DateTime(2026, 1, 1), new DateTime(2026, 12, 31), TimelineGranularity.Month), maxTicks: 3);

            Assert.AreEqual(1, labels.TickCount);
            Assert.AreEqual("2026", labels.AxisLabels[0]);
        }

        [TestMethod]
        public void HugeMaxTicks_LabelsEveryBucket()
        {
            var labels = Ticks(Plan(new DateTime(2026, 1, 1), new DateTime(2026, 12, 31), TimelineGranularity.Month), maxTicks: 1000);

            Assert.AreEqual(12, labels.TickCount);
            Assert.IsTrue(labels.AxisLabels.All(l => l.Length > 0));
        }

        [TestMethod]
        public void WindowShorterThanAnyBoundary_StillLabelsFirstBucket()
        {
            var labels = Ticks(Plan(new DateTime(2026, 1, 6), new DateTime(2026, 1, 8), TimelineGranularity.Day), maxTicks: 1);

            Assert.IsTrue(labels.TickCount >= 1);
            Assert.IsTrue(labels.AxisLabels.Any(l => l.Length > 0));
        }

        [TestMethod]
        public void TooltipLabels_AreNeverBlankAndShowClippedRanges()
        {
            var plan = Plan(new DateTime(2025, 9, 29), new DateTime(2026, 9, 28), TimelineGranularity.Month);
            var labels = Ticks(plan);

            Assert.IsTrue(labels.TooltipLabels.All(l => !string.IsNullOrEmpty(l)));
            Assert.AreEqual("9/29/2025 – 9/30/2025", labels.TooltipLabels[0]);
            Assert.AreEqual("October 2025", labels.TooltipLabels[1]);
            Assert.AreEqual("9/1/2026 – 9/28/2026", labels.TooltipLabels[12]);

            var weeks = Ticks(Plan(new DateTime(2026, 1, 5), new DateTime(2026, 1, 18), TimelineGranularity.Week));
            Assert.AreEqual("1/5/2026 – 1/11/2026", weeks.TooltipLabels[0]);

            var days = Ticks(Plan(new DateTime(2026, 1, 5), new DateTime(2026, 1, 6), TimelineGranularity.Day));
            Assert.AreEqual("1/5/2026", days.TooltipLabels[0]);
        }

        [DataTestMethod]
        [DataRow("en-US", "M/d")]
        [DataRow("de-DE", "dd.MM")]
        [DataRow("fr-FR", "dd/MM")]
        [DataRow("ja-JP", "MM/dd")]
        [DataRow("sv-SE", "MM-dd")]
        public void ShortMonthDayPattern_DropsTheYear(string culture, string expected)
        {
            Assert.AreEqual(expected, TimelineAxisTicks.ShortMonthDayPattern(CultureInfo.GetCultureInfo(culture)));
        }
    }
}
