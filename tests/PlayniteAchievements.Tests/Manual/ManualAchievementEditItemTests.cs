using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.ViewModels;
using System;

namespace PlayniteAchievements.Manual.Tests
{
    [TestClass]
    public class ManualAchievementEditItemTests
    {
        [TestMethod]
        public void HasUnlockTime_InitialEnable_SeedsCurrentLocalTime()
        {
            var item = new ManualAchievementEditItem(CreateDetail("seed"), isUnlocked: true, unlockTime: null);
            var before = DateTime.Now.AddMinutes(-2);

            item.HasUnlockTime = true;

            Assert.IsTrue(item.HasUnlockTime);
            Assert.IsTrue(item.UnlockTimeLocal.HasValue);
            Assert.IsTrue(item.UnlockTimeLocal.Value >= before);
            Assert.IsTrue(item.UnlockTimeLocal.Value <= DateTime.Now.AddMinutes(2));
        }

        [TestMethod]
        public void SwitchingFrom24HourToPm_PreservesExplicitPmSelection()
        {
            var localNineAm = DateTime.SpecifyKind(DateTime.Now.Date.AddHours(9), DateTimeKind.Local);
            var item = new ManualAchievementEditItem(
                CreateDetail("mode"),
                isUnlocked: true,
                unlockTime: localNineAm.ToUniversalTime());

            item.SelectedTimeModeText = "24hr";
            item.TimeText = "09:30";
            item.SelectedTimeModeText = "PM";

            Assert.AreEqual("PM", item.SelectedTimeModeText);
            Assert.AreEqual("9:30", item.TimeText);
            Assert.IsTrue(item.UnlockTimeLocal.HasValue);
            Assert.AreEqual(21, item.UnlockTimeLocal.Value.Hour);
            Assert.AreEqual(30, item.UnlockTimeLocal.Value.Minute);
        }

        [TestMethod]
        public void TwentyFourHourMode_Accepts2300()
        {
            var localTenAm = DateTime.SpecifyKind(DateTime.Now.Date.AddHours(10), DateTimeKind.Local);
            var item = new ManualAchievementEditItem(
                CreateDetail("twentyfour"),
                isUnlocked: true,
                unlockTime: localTenAm.ToUniversalTime());

            item.SelectedTimeModeText = "24hr";
            item.TimeText = "23:00";

            Assert.AreEqual("24hr", item.SelectedTimeModeText);
            Assert.IsTrue(item.IsValidTime);
            Assert.AreEqual("23:00", item.TimeText);
            Assert.IsTrue(item.UnlockTimeLocal.HasValue);
            Assert.AreEqual(23, item.UnlockTimeLocal.Value.Hour);
            Assert.AreEqual(0, item.UnlockTimeLocal.Value.Minute);
        }

        [TestMethod]
        public void TwentyFourHourMode_ProgressiveTyping_EndsValidAt2300()
        {
            var localTenAm = DateTime.SpecifyKind(DateTime.Now.Date.AddHours(10), DateTimeKind.Local);
            var item = new ManualAchievementEditItem(
                CreateDetail("typing"),
                isUnlocked: true,
                unlockTime: localTenAm.ToUniversalTime());

            item.SelectedTimeModeText = "24hr";

            item.TimeText = "2";
            Assert.IsFalse(item.IsValidTime);

            item.TimeText = "23";
            Assert.IsFalse(item.IsValidTime);

            item.TimeText = "23:";
            Assert.IsFalse(item.IsValidTime);

            item.TimeText = "23:0";
            Assert.IsTrue(item.IsValidTime);

            item.TimeText = "23:00";
            Assert.IsTrue(item.IsValidTime);
            Assert.AreEqual("23:00", item.TimeText);
            Assert.IsTrue(item.UnlockTimeLocal.HasValue);
            Assert.AreEqual(23, item.UnlockTimeLocal.Value.Hour);
            Assert.AreEqual(0, item.UnlockTimeLocal.Value.Minute);
        }

        private static AchievementDetail CreateDetail(string apiName)
        {
            return new AchievementDetail
            {
                ApiName = apiName,
                DisplayName = apiName,
                Description = string.Empty,
                UnlockedIconPath = string.Empty,
                LockedIconPath = string.Empty
            };
        }
    }
}
