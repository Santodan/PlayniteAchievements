using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Common;

namespace PlayniteAchievements.Common.Tests
{
    [TestClass]
    public class DateTimeUtilitiesTests
    {
        private static readonly TimeZoneInfo Pacific = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
        private static readonly TimeZoneInfo India = TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");

        [TestMethod]
        public void ToLocalDay_WestOfUtc_LateEveningIsPreviousDay()
        {
            var utc = new DateTime(2026, 1, 15, 3, 30, 0, DateTimeKind.Utc);

            var day = DateTimeUtilities.ToLocalDay(utc, Pacific);

            Assert.AreEqual(new DateTime(2026, 1, 14), day);
            Assert.AreEqual(DateTimeKind.Unspecified, day.Kind);
            Assert.AreEqual(TimeSpan.Zero, day.TimeOfDay);
        }

        [TestMethod]
        public void ToLocalDay_HalfHourOffset_RollsForward()
        {
            var utc = new DateTime(2026, 1, 14, 19, 0, 0, DateTimeKind.Utc);

            Assert.AreEqual(new DateTime(2026, 1, 15), DateTimeUtilities.ToLocalDay(utc, India));
        }

        [TestMethod]
        public void ToLocalDay_UnspecifiedInput_IsTreatedAsUtc()
        {
            var unspecified = new DateTime(2026, 1, 15, 3, 30, 0, DateTimeKind.Unspecified);

            Assert.AreEqual(new DateTime(2026, 1, 14), DateTimeUtilities.ToLocalDay(unspecified, Pacific));
        }

        [TestMethod]
        public void ToLocalDay_AcrossDstTransition_StaysOnTransitionDay()
        {
            var beforeShift = new DateTime(2026, 3, 8, 9, 59, 0, DateTimeKind.Utc);   // 01:59 PST
            var afterShift = new DateTime(2026, 3, 8, 10, 0, 0, DateTimeKind.Utc);    // 03:00 PDT

            Assert.AreEqual(new DateTime(2026, 3, 8), DateTimeUtilities.ToLocalDay(beforeShift, Pacific));
            Assert.AreEqual(new DateTime(2026, 3, 8), DateTimeUtilities.ToLocalDay(afterShift, Pacific));
        }
    }
}
