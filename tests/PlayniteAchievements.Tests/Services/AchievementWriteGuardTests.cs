using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Services.Refresh;

namespace PlayniteAchievements.Services.Tests
{
    [TestClass]
    public class AchievementWriteGuardTests
    {
        private static GameAchievementData Data(
            int total,
            int unlocked,
            bool hasAchievements = true,
            DateTime? unlockTime = null)
        {
            var achievements = new List<AchievementDetail>();
            for (var i = 0; i < total; i++)
            {
                achievements.Add(new AchievementDetail
                {
                    ApiName = $"ACH_{i}",
                    Unlocked = i < unlocked,
                    UnlockTimeUtc = i < unlocked ? unlockTime : null
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

        [TestMethod]
        public void PreserveCachedUnlocks_PayloadRelocksCachedUnlocks_KeepsThemUnlocked()
        {
            var unlockTime = new DateTime(2026, 7, 4, 12, 0, 0, DateTimeKind.Utc);
            var previous = Data(38, 20, unlockTime: unlockTime);
            var incoming = Data(38, 5);

            Assert.AreEqual(15, AchievementWriteGuard.PreserveCachedUnlocks(previous, incoming));

            for (var i = 0; i < 20; i++)
            {
                Assert.IsTrue(incoming.Achievements[i].Unlocked, $"ACH_{i} should stay unlocked.");
            }

            Assert.AreEqual(
                unlockTime,
                incoming.Achievements[19].UnlockTimeUtc,
                "The cached unlock time comes along, because the payload carries none for an achievement it reports locked.");
            Assert.IsFalse(incoming.Achievements[20].Unlocked);
        }

        [TestMethod]
        public void PreserveCachedUnlocks_PayloadAddsUnlocks_LeavesPayloadAlone()
        {
            var incoming = Data(38, 25, unlockTime: new DateTime(2026, 7, 4, 12, 30, 0, DateTimeKind.Utc));

            Assert.AreEqual(0, AchievementWriteGuard.PreserveCachedUnlocks(Data(38, 20), incoming));
            Assert.AreEqual(25, incoming.Achievements.Count(a => a.Unlocked));
            Assert.AreEqual(
                new DateTime(2026, 7, 4, 12, 30, 0, DateTimeKind.Utc),
                incoming.Achievements[0].UnlockTimeUtc,
                "An unlock the payload already reports keeps the payload's own time.");
        }

        [TestMethod]
        public void PreserveCachedUnlocks_AchievementAbsentFromCache_LeavesItLocked()
        {
            var previous = Data(5, 5);
            var incoming = Data(38, 0);

            Assert.AreEqual(5, AchievementWriteGuard.PreserveCachedUnlocks(previous, incoming));
            Assert.AreEqual(5, incoming.Achievements.Count(a => a.Unlocked));
            Assert.IsFalse(incoming.Achievements[5].Unlocked);
        }

        [TestMethod]
        public void PreserveCachedUnlocks_NoCachedData_DoesNothing()
        {
            var incoming = Data(38, 3);

            Assert.AreEqual(0, AchievementWriteGuard.PreserveCachedUnlocks(null, incoming));
            Assert.AreEqual(0, AchievementWriteGuard.PreserveCachedUnlocks(Data(38, 0), incoming));
            Assert.AreEqual(3, incoming.Achievements.Count(a => a.Unlocked));
        }
    }
}
