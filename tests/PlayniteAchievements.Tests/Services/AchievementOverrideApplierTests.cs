using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Achievements;
using System;

namespace PlayniteAchievements.Services.Tests
{
    [TestClass]
    public class AchievementOverrideApplierTests
    {
        /// <summary>
        /// Stands in for the summary row, which is internal to the plugin assembly. What matters is
        /// that a second implementation of the interface gets identical treatment.
        /// </summary>
        private sealed class FakeRow : IAchievementOverrideTarget
        {
            public string DisplayName { get; set; }

            public string Description { get; set; }

            public int? Points { get; set; }

            public string TrophyType { get; set; }

            public bool Hidden { get; set; }

            public DateTime? UnlockTimeUtc { get; set; }

            public bool Unlocked { get; set; }
        }

        private static AchievementOverride FullOverride()
        {
            return new AchievementOverride
            {
                DisplayName = "Renamed",
                Description = "Rewritten",
                Points = 42,
                TrophyType = "gold",
                Hidden = true,
                UnlockTimeUtc = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc)
            };
        }

        [TestMethod]
        public void Apply_EveryTargetTypeGetsTheSameResult()
        {
            var detail = new FakeRow { Unlocked = true };
            var row = new FakeRow { Unlocked = true };
            var userOverride = FullOverride();

            AchievementOverrideApplier.Apply(detail, userOverride, hasManualLink: false);
            AchievementOverrideApplier.Apply(row, userOverride, hasManualLink: false);

            // The whole point of the shared applier: the hydrated row and the summary row cannot
            // disagree about what the user asked for.
            Assert.AreEqual(detail.DisplayName, row.DisplayName);
            Assert.AreEqual(detail.Description, row.Description);
            Assert.AreEqual(detail.Points, row.Points);
            Assert.AreEqual(detail.TrophyType, row.TrophyType);
            Assert.AreEqual(detail.Hidden, row.Hidden);
            Assert.AreEqual(detail.UnlockTimeUtc, row.UnlockTimeUtc);
        }

        [TestMethod]
        public void Apply_HiddenOverride_LandsOnBothTargets()
        {
            var detail = new FakeRow { Hidden = false };
            var row = new FakeRow { Hidden = false };
            var userOverride = new AchievementOverride { Hidden = true };

            AchievementOverrideApplier.Apply(detail, userOverride, hasManualLink: false);
            AchievementOverrideApplier.Apply(row, userOverride, hasManualLink: false);

            Assert.IsTrue(detail.Hidden);
            Assert.IsTrue(row.Hidden);
        }

        [TestMethod]
        public void Apply_NullHidden_LeavesTheProviderValue()
        {
            var detail = new FakeRow { Hidden = true };

            AchievementOverrideApplier.Apply(detail, new AchievementOverride(), hasManualLink: false);

            // Null is "no opinion", not "not hidden".
            Assert.IsTrue(detail.Hidden);
        }

        [TestMethod]
        public void Apply_ManuallyTrackedGame_KeepsTheLinksUnlockTime()
        {
            var linkTime = new DateTime(2025, 5, 5, 0, 0, 0, DateTimeKind.Utc);
            var detail = new FakeRow { Unlocked = true, UnlockTimeUtc = linkTime };
            var row = new FakeRow { Unlocked = true, UnlockTimeUtc = linkTime };
            var userOverride = FullOverride();

            AchievementOverrideApplier.Apply(detail, userOverride, hasManualLink: true);
            AchievementOverrideApplier.Apply(row, userOverride, hasManualLink: true);

            Assert.AreEqual(linkTime, detail.UnlockTimeUtc);
            Assert.AreEqual(linkTime, row.UnlockTimeUtc);
            // Everything else still applies; only the timestamp is withheld.
            Assert.AreEqual("Renamed", detail.DisplayName);
            Assert.AreEqual("Renamed", row.DisplayName);
        }

        [TestMethod]
        public void Apply_ClearUnlockTime_ClearsOnBothTargets()
        {
            var stamped = new DateTime(2025, 5, 5, 0, 0, 0, DateTimeKind.Utc);
            var detail = new FakeRow { Unlocked = true, UnlockTimeUtc = stamped };
            var row = new FakeRow { Unlocked = true, UnlockTimeUtc = stamped };
            var userOverride = new AchievementOverride { ClearUnlockTime = true };

            AchievementOverrideApplier.Apply(detail, userOverride, hasManualLink: false);
            AchievementOverrideApplier.Apply(row, userOverride, hasManualLink: false);

            Assert.IsNull(detail.UnlockTimeUtc);
            Assert.IsNull(row.UnlockTimeUtc);
        }

        [TestMethod]
        public void Apply_LockedRow_DoesNotTakeAnUnlockTime()
        {
            var row = new FakeRow { Unlocked = false };

            AchievementOverrideApplier.Apply(row, FullOverride(), hasManualLink: false);

            // Unlock status stays provider-owned, so a locked row has no timestamp to correct.
            Assert.IsNull(row.UnlockTimeUtc);
        }

        [TestMethod]
        public void Apply_BlankOverride_LeavesProviderValuesAlone()
        {
            var detail = new FakeRow
            {
                DisplayName = "Provider name",
                Description = "Provider text",
                Points = 7,
                TrophyType = "bronze"
            };

            AchievementOverrideApplier.Apply(
                detail,
                new AchievementOverride { DisplayName = "  ", Description = string.Empty, TrophyType = null },
                hasManualLink: false);

            Assert.AreEqual("Provider name", detail.DisplayName);
            Assert.AreEqual("Provider text", detail.Description);
            Assert.AreEqual(7, detail.Points);
            Assert.AreEqual("bronze", detail.TrophyType);
        }
    }
}
