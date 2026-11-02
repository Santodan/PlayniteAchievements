using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Common;

namespace PlayniteAchievements.Tests.Common
{
    [TestClass]
    public class RelativeDateFormatterTests
    {
        private static readonly DateTime Now = new DateTime(2026, 6, 24, 12, 0, 0, DateTimeKind.Local);

        private static void AssertRelative(RelativeDateUnit unit, int count, DateTime value, DateTime now)
        {
            var actual = RelativeDateFormatter.GetRelative(value, now);
            Assert.AreEqual(unit, actual.Unit);
            Assert.AreEqual(count, actual.Count);
        }

        [TestMethod]
        public void GetRelative_SameDay_ReturnsToday()
        {
            AssertRelative(RelativeDateUnit.Today, 0, Now.AddHours(-3), Now);
        }

        [TestMethod]
        public void GetRelative_FutureValue_ReturnsToday()
        {
            AssertRelative(RelativeDateUnit.Today, 0, Now.AddDays(1), Now);
        }

        [TestMethod]
        public void GetRelative_PreviousCalendarDay_ReturnsYesterday()
        {
            AssertRelative(RelativeDateUnit.Yesterday, 1, new DateTime(2026, 6, 23, 23, 59, 0), Now);
        }

        [TestMethod]
        public void GetRelative_AcrossYearBoundary_ReturnsYesterday()
        {
            AssertRelative(RelativeDateUnit.Yesterday, 1, new DateTime(2025, 12, 31), new DateTime(2026, 1, 1));
        }

        [TestMethod]
        public void GetRelative_TwoToSixDays_ReturnsDays()
        {
            AssertRelative(RelativeDateUnit.Days, 2, Now.AddDays(-2), Now);
            AssertRelative(RelativeDateUnit.Days, 6, Now.AddDays(-6), Now);
        }

        [TestMethod]
        public void GetRelative_SevenToTwentySevenDays_ReturnsWeeks()
        {
            AssertRelative(RelativeDateUnit.Weeks, 1, Now.AddDays(-7), Now);
            AssertRelative(RelativeDateUnit.Weeks, 1, Now.AddDays(-13), Now);
            AssertRelative(RelativeDateUnit.Weeks, 2, Now.AddDays(-14), Now);
            AssertRelative(RelativeDateUnit.Weeks, 3, Now.AddDays(-27), Now);
        }

        [TestMethod]
        public void GetRelative_TwentyEightDays_ReturnsOneMonth()
        {
            AssertRelative(RelativeDateUnit.Months, 1, Now.AddDays(-28), Now);
        }

        [TestMethod]
        public void GetRelative_ThirtyDaysShortOfCalendarMonth_ReturnsOneMonth()
        {
            // Jan 31 -> Mar 2 is 30 days but not yet a whole calendar month by day-of-month.
            AssertRelative(RelativeDateUnit.Months, 1, new DateTime(2026, 1, 31), new DateTime(2026, 3, 2));
        }

        [TestMethod]
        public void GetRelative_ElevenMonths_ReturnsMonths()
        {
            AssertRelative(RelativeDateUnit.Months, 11, new DateTime(2025, 7, 24), Now);
            AssertRelative(RelativeDateUnit.Months, 11, new DateTime(2025, 6, 25), Now);
        }

        [TestMethod]
        public void GetRelative_TwelveMonths_ReturnsOneYear()
        {
            AssertRelative(RelativeDateUnit.Years, 1, new DateTime(2025, 6, 24), Now);
        }

        [TestMethod]
        public void GetRelative_TwentyThreeMonths_ReturnsOneYear()
        {
            AssertRelative(RelativeDateUnit.Years, 1, new DateTime(2024, 7, 24), Now);
        }

        [TestMethod]
        public void GetRelative_SeveralYears_ReturnsWholeYears()
        {
            AssertRelative(RelativeDateUnit.Years, 8, new DateTime(2018, 6, 15), Now);
            AssertRelative(RelativeDateUnit.Years, 7, new DateTime(2018, 6, 25), Now);
        }
    }
}
