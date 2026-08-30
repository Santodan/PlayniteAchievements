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
        public void DiffUserUnlocks_ReturnsEmpty_WhenUnlockTimeMovesForward()
        {
            var differ = new AchievementUnlockDiffer();
            var before = Data(Achievement("first", "First", true, new DateTime(2026, 7, 4, 12, 0, 0, DateTimeKind.Utc)));
            var after = Data(Achievement("first", "First", true, new DateTime(2026, 7, 4, 12, 5, 0, DateTimeKind.Utc)));

            var result = differ.DiffUserUnlocks(before, after);

            Assert.AreEqual(
                0,
                result.Count,
                "An achievement already unlocked in the baseline is not new just because a later source reported a different unlock time.");
        }

        [TestMethod]
        public void DiffUserUnlocks_ReturnsRepeatUnlock_WhenTheProviderOptsIn()
        {
            // A provider whose achievements are earned again (League challenges climb tiers) has a
            // single authoritative source, so a newer timestamp is a new earn.
            var differ = new AchievementUnlockDiffer(providerKey => providerKey == "Riot");
            var before = Data("Riot", Achievement("101000", "Globetrotter", true, new DateTime(2026, 7, 4, 12, 0, 0, DateTimeKind.Utc)));
            var after = Data("Riot", Achievement("101000", "Globetrotter", true, new DateTime(2026, 7, 4, 12, 5, 0, DateTimeKind.Utc)));

            var result = differ.DiffUserUnlocks(before, after);

            Assert.AreEqual(1, result.Count);
            Assert.AreEqual("101000", result[0].ApiName);
        }

        [TestMethod]
        public void DiffUserUnlocks_IgnoresRepeatUnlock_ForProvidersThatDidNotOptIn()
        {
            var differ = new AchievementUnlockDiffer(providerKey => providerKey == "Riot");
            var before = Data("Steam", Achievement("first", "First", true, new DateTime(2026, 7, 4, 12, 0, 0, DateTimeKind.Utc)));
            var after = Data("Steam", Achievement("first", "First", true, new DateTime(2026, 7, 4, 12, 5, 0, DateTimeKind.Utc)));

            Assert.AreEqual(
                0,
                differ.DiffUserUnlocks(before, after).Count,
                "Opting one provider in must not change what every other provider announces.");
        }

        [TestMethod]
        public void DiffUserUnlocks_IgnoresRepeatUnlock_WhenTheTimestampMovesBackwards()
        {
            var differ = new AchievementUnlockDiffer(providerKey => true);
            var before = Data("Riot", Achievement("101000", "Globetrotter", true, new DateTime(2026, 7, 4, 12, 5, 0, DateTimeKind.Utc)));
            var after = Data("Riot", Achievement("101000", "Globetrotter", true, new DateTime(2026, 7, 4, 12, 0, 0, DateTimeKind.Utc)));

            Assert.AreEqual(
                0,
                differ.DiffUserUnlocks(before, after).Count,
                "Only a forward move is an earn; an earlier time is a correction.");
        }

        [TestMethod]
        public void DiffUserUnlocks_IgnoresRepeatUnlock_WhenEitherTimestampIsMissing()
        {
            var differ = new AchievementUnlockDiffer(providerKey => true);
            var before = Data("Riot", Achievement("101000", "Globetrotter", true, null));
            var after = Data("Riot", Achievement("101000", "Globetrotter", true, new DateTime(2026, 7, 4, 12, 0, 0, DateTimeKind.Utc)));

            Assert.AreEqual(
                0,
                differ.DiffUserUnlocks(before, after).Count,
                "An appearing timestamp is a source filling a gap, not a new earn.");
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

        [TestMethod]
        public void DiffUserUnlocks_OrdersByTimeThenInputOrder()
        {
            var differ = new AchievementUnlockDiffer();
            var tied = new DateTime(2026, 7, 4, 12, 5, 0, DateTimeKind.Utc);
            var before = Data(
                Achievement("zeta", "Zeta", false),
                Achievement("alpha", "Alpha", false),
                Achievement("late-input-early-time", "Late Input Early Time", false));
            var after = Data(
                Achievement("zeta", "Zeta", true, tied),
                Achievement("alpha", "Alpha", true, tied),
                Achievement("late-input-early-time", "Late Input Early Time", true, tied.AddMinutes(-1)));

            var result = differ.DiffUserUnlocks(before, after).ToList();

            CollectionAssert.AreEqual(
                new[] { "late-input-early-time", "zeta", "alpha" },
                result.Select(a => a.ApiName).ToArray(),
                "Time ascending dominates; same-timestamp unlocks keep the provider-ordered input order, not name or rarity order.");
        }

        [TestMethod]
        public void DiffFriendSessionUnlocks_SameTimestampKeepsProviderOrder()
        {
            var differ = new AchievementUnlockDiffer();
            var sessionStart = new DateTime(2026, 7, 4, 12, 0, 0, DateTimeKind.Utc);
            var tied = sessionStart.AddMinutes(1);
            var rows = new[]
            {
                FriendRow("zeta", "Zeta", true, tied, RarityTier.Common),
                FriendRow("alpha", "Alpha", true, tied, RarityTier.UltraRare)
            };

            var result = differ.DiffFriendSessionUnlocks(rows, sessionStart, new HashSet<string>(StringComparer.OrdinalIgnoreCase)).ToList();

            CollectionAssert.AreEqual(
                new[] { "zeta", "alpha" },
                result.Select(row => row.ApiName).ToArray(),
                "Same-timestamp friend unlocks keep the provider-ordered input order, not rarity or name order.");
        }

        [TestMethod]
        public void DiffFriendSessionUnlocks_TimeDominatesProviderOrder()
        {
            var differ = new AchievementUnlockDiffer();
            var sessionStart = new DateTime(2026, 7, 4, 12, 0, 0, DateTimeKind.Utc);
            var rows = new[]
            {
                FriendRow("later-time", "Later Time", true, sessionStart.AddMinutes(5)),
                FriendRow("earlier-time", "Earlier Time", true, sessionStart.AddMinutes(1))
            };

            var result = differ.DiffFriendSessionUnlocks(rows, sessionStart, new HashSet<string>(StringComparer.OrdinalIgnoreCase)).ToList();

            CollectionAssert.AreEqual(
                new[] { "earlier-time", "later-time" },
                result.Select(row => row.ApiName).ToArray());
        }

        [TestMethod]
        public void DiffFriendBaselineUnlocks_KeepsProviderOrder()
        {
            var differ = new AchievementUnlockDiffer();
            var baseline = new[] { FriendRow("existing", "Existing", true, null) };
            var current = new[]
            {
                FriendRow("existing", "Existing", true, null),
                FriendRow("zeta", "Zeta", true, null, RarityTier.Common),
                FriendRow("alpha", "Alpha", true, null, RarityTier.UltraRare)
            };

            var result = differ.DiffFriendBaselineUnlocks(baseline, current, new HashSet<string>(StringComparer.OrdinalIgnoreCase)).ToList();

            CollectionAssert.AreEqual(
                new[] { "zeta", "alpha" },
                result.Select(row => row.ApiName).ToArray(),
                "Baseline diffs carry no timestamps; the provider-ordered input order is the emission order.");
        }

        private static GameAchievementData Data(params AchievementDetail[] achievements)
        {
            return new GameAchievementData
            {
                Achievements = achievements.ToList()
            };
        }

        private static GameAchievementData Data(string providerKey, params AchievementDetail[] achievements)
        {
            return new GameAchievementData
            {
                ProviderKey = providerKey,
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

        private static FriendAchievementRow FriendRow(
            string apiName,
            string displayName,
            bool unlocked,
            DateTime? unlockTime,
            RarityTier? rarity = null)
        {
            return new FriendAchievementRow
            {
                ApiName = apiName,
                DisplayName = displayName,
                Unlocked = unlocked,
                UnlockTimeUtc = unlockTime,
                Rarity = rarity
            };
        }
    }
}
