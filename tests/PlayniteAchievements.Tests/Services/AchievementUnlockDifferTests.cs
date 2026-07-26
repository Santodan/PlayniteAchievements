using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Friends;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.Achievements;

namespace PlayniteAchievements.Tests.Services
{
    [TestClass]
    public class AchievementUnlockDifferTests
    {
        [TestMethod]
        public void DiffUserUnlocks_ReturnsEmpty_WhenNothingChanged()
        {
            var differ = new AchievementUnlockDiffer();
            var unlockTime = new DateTime(2026, 7, 4, 12, 0, 0, DateTimeKind.Utc);
            var before = Data(Achievement("first", "First", true, unlockTime));
            var after = Data(Achievement("first", "First", true, unlockTime));

            var result = differ.DiffUserUnlocks(before, after);

            Assert.AreEqual(0, result.Count);
        }

        [TestMethod]
        public void DiffUserUnlocks_ReturnsAchievementsUnlockedSinceSnapshot()
        {
            var differ = new AchievementUnlockDiffer();
            var before = Data(
                Achievement("first", "First", false),
                Achievement(null, "Display fallback", false));
            var after = Data(
                Achievement("first", "First", true, new DateTime(2026, 7, 4, 12, 1, 0, DateTimeKind.Utc)),
                Achievement(null, "Display fallback", true, new DateTime(2026, 7, 4, 12, 2, 0, DateTimeKind.Utc)));

            var result = differ.DiffUserUnlocks(before, after).ToList();

            Assert.AreEqual(2, result.Count);
            Assert.AreEqual("first", result[0].ApiName);
            Assert.AreEqual("Display fallback", result[1].DisplayName);
        }

        [TestMethod]
        public void DiffUserUnlocks_ReturnsAchievement_WhenUnlockTimeMovesForward()
        {
            var differ = new AchievementUnlockDiffer();
            var before = Data(Achievement("first", "First", true, new DateTime(2026, 7, 4, 12, 0, 0, DateTimeKind.Utc)));
            var after = Data(Achievement("first", "First", true, new DateTime(2026, 7, 4, 12, 5, 0, DateTimeKind.Utc)));

            var result = differ.DiffUserUnlocks(before, after);

            Assert.AreEqual(1, result.Count);
            Assert.AreEqual("first", result[0].ApiName);
        }

        [TestMethod]
        public void DiffFriendSessionUnlocks_FiltersBySessionStartAndDedupeSet()
        {
            var differ = new AchievementUnlockDiffer();
            var sessionStart = new DateTime(2026, 7, 4, 12, 0, 0, DateTimeKind.Utc);
            var toasted = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "already" };
            var rows = new[]
            {
                FriendRow("old", "Old", true, sessionStart.AddSeconds(-1)),
                FriendRow("already", "Already", true, sessionStart.AddMinutes(1)),
                FriendRow("fresh", "Fresh", true, sessionStart),
                FriendRow(null, "Fallback", true, sessionStart.AddMinutes(2)),
                FriendRow("locked", "Locked", false, sessionStart.AddMinutes(3))
            };

            var result = differ.DiffFriendSessionUnlocks(rows, sessionStart, toasted).ToList();

            Assert.AreEqual(2, result.Count);
            Assert.AreEqual("fresh", result[0].ApiName);
            Assert.AreEqual("Fallback", result[1].DisplayName);
            Assert.IsTrue(toasted.Contains("fresh"));
            Assert.IsTrue(toasted.Contains("Fallback"));
        }

        [TestMethod]
        public void DiffFriendBaselineUnlocks_ReturnsUnlocksAbsentFromBaseline()
        {
            var differ = new AchievementUnlockDiffer();
            var toasted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var baseline = new[]
            {
                FriendRow("existing", "Existing", true, null),
                FriendRow("locked", "Locked", false, null)
            };
            var current = new[]
            {
                FriendRow("existing", "Existing", true, null),
                FriendRow("locked", "Locked", true, null),
                FriendRow(null, "Fallback", true, null)
            };

            var result = differ.DiffFriendBaselineUnlocks(baseline, current, toasted).ToList();

            Assert.AreEqual(2, result.Count);
            CollectionAssert.AreEquivalent(
                new[] { "locked", "Fallback" },
                result.Select(row => row.ApiName ?? row.DisplayName).ToArray());
            Assert.IsTrue(toasted.Contains("locked"));
            Assert.IsTrue(toasted.Contains("Fallback"));
        }

        private static GameAchievementData Data(params AchievementDetail[] achievements)
        {
            return new GameAchievementData
            {
                Achievements = achievements.ToList()
            };
        }

        private static AchievementDetail Achievement(string apiName, string displayName, bool unlocked, DateTime? unlockTime = null)
        {
            return new AchievementDetail
            {
                ApiName = apiName,
                DisplayName = displayName,
                Unlocked = unlocked,
                UnlockTimeUtc = unlockTime
            };
        }

        private static FriendAchievementRow FriendRow(string apiName, string displayName, bool unlocked, DateTime? unlockTime)
        {
            return new FriendAchievementRow
            {
                ApiName = apiName,
                DisplayName = displayName,
                Unlocked = unlocked,
                UnlockTimeUtc = unlockTime
            };
        }
    }
}
