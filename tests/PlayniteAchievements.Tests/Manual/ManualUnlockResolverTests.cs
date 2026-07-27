using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Providers.Exophase;
using PlayniteAchievements.Providers.Manual;
using System;
using System.Collections.Generic;

namespace PlayniteAchievements.Manual.Tests
{
    [TestClass]
    public class ManualUnlockResolverTests
    {
        [TestMethod]
        public void Exophase_LegacyDisplayKey_ResolvesUnlockedAgainstStableApiName()
        {
            // A link stored before the stable ApiName scheme keys unlocks by "exophase_<display>".
            var legacyKey = ExophaseApiClient.GenerateApiName("First Blood");
            StringAssert.StartsWith(legacyKey, "exophase_");

            var unlockTime = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            var link = new ManualAchievementLink
            {
                SourceKey = "Exophase",
                SourceGameId = "shogun-showdown-steam",
                UnlockStates = new Dictionary<string, bool> { [legacyKey] = true },
                UnlockTimes = new Dictionary<string, DateTime?> { [legacyKey] = unlockTime }
            };

            // A freshly fetched achievement now carries a stable "exophase:<id>" ApiName.
            var detail = new AchievementDetail
            {
                ApiName = "exophase:12345",
                DisplayName = "First Blood"
            };

            var resolved = new ManualUnlockResolver(link)
                .TryResolveUnlock(detail, out var isUnlocked, out var resolvedTime);

            Assert.IsTrue(resolved, "Legacy display-derived key must match the stable ApiName via DisplayName fallback.");
            Assert.IsTrue(isUnlocked);
            Assert.AreEqual(unlockTime, resolvedTime);
        }

        [TestMethod]
        public void Steam_ExactApiName_ResolvesUnlocked()
        {
            var link = new ManualAchievementLink
            {
                SourceKey = "Steam",
                SourceGameId = "2084000",
                UnlockStates = new Dictionary<string, bool> { ["q_first_island_cleared"] = true },
                UnlockTimes = new Dictionary<string, DateTime?>()
            };

            var detail = new AchievementDetail { ApiName = "q_first_island_cleared", DisplayName = "First Island" };

            var resolved = new ManualUnlockResolver(link)
                .TryResolveUnlock(detail, out var isUnlocked, out var resolvedTime);

            Assert.IsTrue(resolved);
            Assert.IsTrue(isUnlocked);
            Assert.IsNull(resolvedTime, "No stored time means unlocked with a null unlock time.");
        }

        [TestMethod]
        public void UnknownAchievement_ResolvesLocked()
        {
            var link = new ManualAchievementLink
            {
                SourceKey = "Steam",
                SourceGameId = "2084000",
                UnlockStates = new Dictionary<string, bool> { ["a"] = true },
                UnlockTimes = new Dictionary<string, DateTime?>()
            };

            var detail = new AchievementDetail { ApiName = "b", DisplayName = "B" };

            var resolved = new ManualUnlockResolver(link)
                .TryResolveUnlock(detail, out var isUnlocked, out var resolvedTime);

            Assert.IsFalse(resolved);
            Assert.IsFalse(isUnlocked);
            Assert.IsNull(resolvedTime);
        }

        [TestMethod]
        public void StoredStateFalse_TakesPrecedenceOverStoredTime()
        {
            var link = new ManualAchievementLink
            {
                SourceKey = "Steam",
                SourceGameId = "2084000",
                UnlockStates = new Dictionary<string, bool> { ["a"] = false },
                UnlockTimes = new Dictionary<string, DateTime?> { ["a"] = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) }
            };

            var detail = new AchievementDetail { ApiName = "a", DisplayName = "A" };

            var resolved = new ManualUnlockResolver(link)
                .TryResolveUnlock(detail, out var isUnlocked, out var resolvedTime);

            Assert.IsFalse(resolved, "An explicit false state must win over a stored unlock time.");
            Assert.IsFalse(isUnlocked);
            Assert.IsNull(resolvedTime);
        }

        [TestMethod]
        public void ApplyUnlockState_MarksUnlockedInPlace()
        {
            var legacyKey = ExophaseApiClient.GenerateApiName("First Blood");
            var link = new ManualAchievementLink
            {
                SourceKey = "Exophase",
                SourceGameId = "shogun-showdown-steam",
                UnlockStates = new Dictionary<string, bool> { [legacyKey] = true },
                UnlockTimes = new Dictionary<string, DateTime?>()
            };

            var achievements = new List<AchievementDetail>
            {
                new AchievementDetail { ApiName = "exophase:12345", DisplayName = "First Blood" },
                new AchievementDetail { ApiName = "exophase:99999", DisplayName = "Never Earned" }
            };

            new ManualUnlockResolver(link).ApplyUnlockState(achievements);

            Assert.IsTrue(achievements[0].Unlocked);
            Assert.IsFalse(achievements[1].Unlocked);
        }
    }
}
