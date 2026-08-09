using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Services.Refresh;

namespace PlayniteAchievements.Services.Tests
{
    [TestClass]
    public class AchievementWriteGuardTests
    {
        private static GameAchievementData Data(int total, int unlocked, bool hasAchievements = true)
        {
            var achievements = new List<AchievementDetail>();
            for (var i = 0; i < total; i++)
            {
                achievements.Add(new AchievementDetail
                {
                    ApiName = $"ACH_{i}",
                    Unlocked = i < unlocked
                });
            }

            return new GameAchievementData
            {
                HasAchievements = hasAchievements,
                Achievements = achievements
            };
        }

        [TestMethod]
        public void ShouldRejectWrite_NoCachedData_Allows()
        {
            Assert.IsFalse(AchievementWriteGuard.ShouldRejectWrite(null, Data(10, 3), out _));
            Assert.IsFalse(AchievementWriteGuard.ShouldRejectWrite(Data(0, 0), Data(10, 3), out _));
        }

        [TestMethod]
        public void ShouldRejectWrite_FirstScanFindsNoAchievements_Allows()
        {
            Assert.IsFalse(AchievementWriteGuard.ShouldRejectWrite(
                null,
                Data(0, 0, hasAchievements: false),
                out _));
        }

        [TestMethod]
        public void ShouldRejectWrite_EmptyPayloadOverCachedAchievements_Rejects()
        {
            Assert.IsTrue(AchievementWriteGuard.ShouldRejectWrite(
                Data(38, 12),
                Data(0, 0, hasAchievements: false),
                out var reason));
            StringAssert.Contains(reason, "empty");
        }

        [TestMethod]
        public void ShouldRejectWrite_AllLockedOverCachedUnlocks_Rejects()
        {
            Assert.IsTrue(AchievementWriteGuard.ShouldRejectWrite(
                Data(38, 12),
                Data(38, 0),
                out var reason));
            StringAssert.Contains(reason, "no unlocks");
        }

        [TestMethod]
        public void ShouldRejectWrite_CachedHasNoUnlocks_Allows()
        {
            Assert.IsFalse(AchievementWriteGuard.ShouldRejectWrite(Data(38, 0), Data(38, 0), out _));
        }

        [TestMethod]
        public void ShouldRejectWrite_UnlocksGainedOrUnchanged_Allows()
        {
            Assert.IsFalse(AchievementWriteGuard.ShouldRejectWrite(Data(38, 12), Data(38, 12), out _));
            Assert.IsFalse(AchievementWriteGuard.ShouldRejectWrite(Data(38, 12), Data(38, 20), out _));
        }

        [TestMethod]
        public void ShouldRejectWrite_PartialUnlockDrop_Allows()
        {
            Assert.IsFalse(AchievementWriteGuard.ShouldRejectWrite(Data(38, 20), Data(30, 5), out _));
        }

        [TestMethod]
        public void IsPartialUnlockRegression_FewerButNonZeroUnlocks_ReportsRegression()
        {
            Assert.IsTrue(AchievementWriteGuard.IsPartialUnlockRegression(
                Data(38, 20),
                Data(38, 5),
                out var previousUnlocked,
                out var incomingUnlocked));
            Assert.AreEqual(20, previousUnlocked);
            Assert.AreEqual(5, incomingUnlocked);
        }

        [TestMethod]
        public void IsPartialUnlockRegression_ZeroOrGrowingUnlocks_ReportsNoRegression()
        {
            Assert.IsFalse(AchievementWriteGuard.IsPartialUnlockRegression(Data(38, 20), Data(38, 0), out _, out _));
            Assert.IsFalse(AchievementWriteGuard.IsPartialUnlockRegression(Data(38, 20), Data(38, 25), out _, out _));
        }
    }
}
