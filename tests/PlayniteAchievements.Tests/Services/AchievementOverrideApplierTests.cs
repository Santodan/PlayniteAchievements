using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Achievements;
using System;

namespace PlayniteAchievements.Services.Tests
{
    /// <summary>
    /// An override's fields reach a row through its custom layer; its unlock time is applied
    /// apart from the layers.
    /// </summary>
    [TestClass]
    public class AchievementOverrideApplierTests
    {
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

        private static AchievementDetail Apply(AchievementDetail row, AchievementOverride userOverride, bool hasManualLink)
        {
            row.CustomLayer = userOverride.ToLayer();
            AchievementLayers.Resolve(row);
            userOverride?.ApplyUnlockTimeTo(row, hasManualLink);
            return row;
        }

        [TestMethod]
        public void FullOverride_ReachesEveryField()
        {
            var row = Apply(new AchievementDetail { ApiName = "a", Unlocked = true }, FullOverride(), hasManualLink: false);

            Assert.AreEqual("Renamed", row.DisplayName);
            Assert.AreEqual("Rewritten", row.Description);
            Assert.AreEqual(42, row.Points);
            Assert.AreEqual("gold", row.TrophyType);
            Assert.IsTrue(row.Hidden);
            Assert.AreEqual(FullOverride().UnlockTimeUtc, row.UnlockTimeUtc);
        }

        [TestMethod]
        public void NullHidden_LeavesTheProviderValue()
        {
            var row = Apply(new AchievementDetail { ApiName = "a", Hidden = true }, new AchievementOverride(), hasManualLink: false);

            // Null is "no opinion", not "not hidden".
            Assert.IsTrue(row.Hidden);
        }

        [TestMethod]
        public void ManuallyTrackedGame_KeepsTheLinksUnlockTime()
        {
            var linkTime = new DateTime(2025, 5, 5, 0, 0, 0, DateTimeKind.Utc);
            var row = Apply(
                new AchievementDetail { ApiName = "a", Unlocked = true, UnlockTimeUtc = linkTime },
                FullOverride(),
                hasManualLink: true);

            Assert.AreEqual(linkTime, row.UnlockTimeUtc);
            // Everything else still applies; only the timestamp is withheld.
            Assert.AreEqual("Renamed", row.DisplayName);
        }

        [TestMethod]
        public void ClearUnlockTime_Clears()
        {
            var row = Apply(
                new AchievementDetail { ApiName = "a", Unlocked = true, UnlockTimeUtc = new DateTime(2025, 5, 5, 0, 0, 0, DateTimeKind.Utc) },
                new AchievementOverride { ClearUnlockTime = true },
                hasManualLink: false);

            Assert.IsNull(row.UnlockTimeUtc);
        }

        [TestMethod]
        public void ClearTrophyTypeAndDescription_Blank()
        {
            var row = Apply(
                new AchievementDetail { ApiName = "a", TrophyType = "gold", Description = "Provider text" },
                new AchievementOverride { ClearTrophyType = true, ClearDescription = true },
                hasManualLink: false);

            Assert.IsNull(row.TrophyType);
            Assert.IsNull(row.Description);
        }

        [TestMethod]
        public void ClearFlags_LoseToExplicitValues()
        {
            var row = Apply(
                new AchievementDetail { ApiName = "a", TrophyType = "gold", Description = "Provider text" },
                new AchievementOverride { TrophyType = "bronze", ClearTrophyType = true, Description = "Rewritten", ClearDescription = true },
                hasManualLink: false);

            Assert.AreEqual("bronze", row.TrophyType);
            Assert.AreEqual("Rewritten", row.Description);
        }

        [TestMethod]
        public void LockedRow_DoesNotTakeAnUnlockTime()
        {
            var row = Apply(new AchievementDetail { ApiName = "a", Unlocked = false }, FullOverride(), hasManualLink: false);

            // Unlock status stays provider-owned, so a locked row has no timestamp to correct.
            Assert.IsNull(row.UnlockTimeUtc);
        }

        [TestMethod]
        public void BlankOverride_LeavesProviderValuesAlone()
        {
            var row = Apply(
                new AchievementDetail { ApiName = "a", DisplayName = "Provider name", Description = "Provider text", Points = 7, TrophyType = "bronze" },
                new AchievementOverride { DisplayName = "  ", Description = string.Empty, TrophyType = null },
                hasManualLink: false);

            Assert.AreEqual("Provider name", row.DisplayName);
            Assert.AreEqual("Provider text", row.Description);
            Assert.AreEqual(7, row.Points);
            Assert.AreEqual("bronze", row.TrophyType);
        }

        [TestMethod]
        public void ResolvingAgain_WithoutTheOverride_RestoresTheProviderValues()
        {
            var row = Apply(new AchievementDetail { ApiName = "a", DisplayName = "Provider name", Points = 7 }, FullOverride(), hasManualLink: false);

            row.CustomLayer = null;
            AchievementLayers.Resolve(row);

            Assert.AreEqual("Provider name", row.DisplayName);
            Assert.AreEqual(7, row.Points);
        }
    }
}
