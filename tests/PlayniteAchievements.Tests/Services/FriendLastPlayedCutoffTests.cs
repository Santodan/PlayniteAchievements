using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Refresh;
using System;
using System.Globalization;

namespace PlayniteAchievements.Services.Tests
{
    [TestClass]
    public class FriendLastPlayedCutoffTests
    {
        private static readonly DateTime LocalNow = new DateTime(2026, 8, 14, 15, 30, 0);

        [TestMethod]
        public void GetLastPlayedCutoffUtc_AllTime_ReturnsNull()
        {
            Assert.IsNull(FriendRefreshWorkPolicy.GetLastPlayedCutoffUtc(FriendLastPlayedThreshold.AllTime, LocalNow));
        }

        [TestMethod]
        public void GetLastPlayedCutoffUtc_ThisYear_ReturnsJanuaryFirstUtc()
        {
            var cutoff = FriendRefreshWorkPolicy.GetLastPlayedCutoffUtc(FriendLastPlayedThreshold.ThisYear, LocalNow);

            Assert.AreEqual(new DateTime(2026, 1, 1).ToUniversalTime(), cutoff);
        }

        [TestMethod]
        public void GetLastPlayedCutoffUtc_ThisMonth_ReturnsFirstOfMonthUtc()
        {
            var cutoff = FriendRefreshWorkPolicy.GetLastPlayedCutoffUtc(FriendLastPlayedThreshold.ThisMonth, LocalNow);

            Assert.AreEqual(new DateTime(2026, 8, 1).ToUniversalTime(), cutoff);
        }

        [TestMethod]
        public void GetLastPlayedCutoffUtc_ThisWeek_ReturnsCultureWeekStartUtc()
        {
            var cutoff = FriendRefreshWorkPolicy.GetLastPlayedCutoffUtc(FriendLastPlayedThreshold.ThisWeek, LocalNow);
            var expectedLocal = RelativeDateFormatter.StartOfCurrentWeek(LocalNow);

            Assert.AreEqual(expectedLocal.ToUniversalTime(), cutoff);
            Assert.AreEqual(CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek, expectedLocal.DayOfWeek);
            Assert.IsTrue(expectedLocal <= LocalNow.Date);
            Assert.IsTrue((LocalNow.Date - expectedLocal).TotalDays < 7);
        }

        [TestMethod]
        public void IsWithinLastPlayedCutoff_NullCutoff_AlwaysPasses()
        {
            Assert.IsTrue(FriendRefreshWorkPolicy.IsWithinLastPlayedCutoff(new DateTime(2001, 1, 1), null));
            Assert.IsTrue(FriendRefreshWorkPolicy.IsWithinLastPlayedCutoff(null, null));
        }

        [TestMethod]
        public void IsWithinLastPlayedCutoff_NullLastPlayed_FailsOpen()
        {
            Assert.IsTrue(FriendRefreshWorkPolicy.IsWithinLastPlayedCutoff(null, new DateTime(2026, 1, 1)));
        }

        [TestMethod]
        public void IsWithinLastPlayedCutoff_OlderThanCutoff_Fails()
        {
            var cutoff = new DateTime(2026, 1, 1);

            Assert.IsFalse(FriendRefreshWorkPolicy.IsWithinLastPlayedCutoff(cutoff.AddSeconds(-1), cutoff));
        }

        [TestMethod]
        public void IsWithinLastPlayedCutoff_AtOrAfterCutoff_Passes()
        {
            var cutoff = new DateTime(2026, 1, 1);

            Assert.IsTrue(FriendRefreshWorkPolicy.IsWithinLastPlayedCutoff(cutoff, cutoff));
            Assert.IsTrue(FriendRefreshWorkPolicy.IsWithinLastPlayedCutoff(cutoff.AddDays(3), cutoff));
        }
    }
}
