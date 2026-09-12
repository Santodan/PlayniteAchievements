using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.GameCustomData;
using PlayniteAchievements.Services.Hydration;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Tests.Services
{
    [TestClass]
    public class AchievementDetailHydratorTests
    {
        [TestMethod]
        public void HydrateAll_EmptyCustomOrder_StampsProviderPositions()
        {
            var details = Details("first", "second", "third");

            Hydrate(details, new ResolvedGameCustomData());

            CollectionAssert.AreEqual(
                new[] { 0, 1, 2 },
                details.Select(d => d.DefaultOrderIndex).ToArray());
        }

        [TestMethod]
        public void HydrateAll_CustomOrder_RanksMatchedAndAppendsUnmatchedInSourceOrder()
        {
            var details = Details("alpha", "beta", "gamma", "delta");
            var customData = new ResolvedGameCustomData
            {
                // Case differs from the details to cover case-insensitive matching.
                AchievementOrder = new List<string> { "GAMMA", "Alpha" }
            };

            Hydrate(details, customData);

            Assert.AreEqual(1, details.Single(d => d.ApiName == "alpha").DefaultOrderIndex);
            Assert.AreEqual(2, details.Single(d => d.ApiName == "beta").DefaultOrderIndex);
            Assert.AreEqual(0, details.Single(d => d.ApiName == "gamma").DefaultOrderIndex);
            Assert.AreEqual(3, details.Single(d => d.ApiName == "delta").DefaultOrderIndex);
        }

        [TestMethod]
        public void HydrateAll_Rehydration_IsIdempotent()
        {
            var details = Details("alpha", "beta");
            var customData = new ResolvedGameCustomData
            {
                AchievementOrder = new List<string> { "beta" }
            };

            Hydrate(details, customData);
            Hydrate(details, customData);

            Assert.AreEqual(1, details.Single(d => d.ApiName == "alpha").DefaultOrderIndex);
            Assert.AreEqual(0, details.Single(d => d.ApiName == "beta").DefaultOrderIndex);
        }

        [TestMethod]
        public void HydrateAll_FieldOverrides_ReplaceProviderValues()
        {
            var details = Details("alpha");
            details[0].Description = "provider description";
            details[0].Points = 10;
            details[0].TrophyType = "bronze";

            Hydrate(details, WithOverride("alpha", new AchievementOverride
            {
                DisplayName = "Renamed",
                Description = "Rewritten",
                Points = 99,
                TrophyType = "gold"
            }));

            var detail = details[0];
            Assert.AreEqual("Renamed", detail.DisplayName);
            Assert.AreEqual("Rewritten", detail.Description);
            Assert.AreEqual(99, detail.Points);
            Assert.AreEqual("gold", detail.TrophyType);
        }

        [TestMethod]
        public void HydrateAll_ClearedFieldOverride_FallsBackToProviderValue()
        {
            var details = Details("alpha");
            details[0].DisplayName = "provider name";
            details[0].Points = 10;

            // An override row that carries a note but no field values must not blank the provider's.
            Hydrate(details, WithOverride("alpha", new AchievementOverride { Note = "note" }));

            Assert.AreEqual("provider name", details[0].DisplayName);
            Assert.AreEqual(10, details[0].Points);
            Assert.AreEqual("note", details[0].AchievementNote);
        }

        [TestMethod]
        public void HydrateAll_UnlockTimeOverride_AppliesOnlyToUnlockedRows()
        {
            var overrideTime = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
            var providerTime = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            var unlocked = Details("alpha");
            unlocked[0].Unlocked = true;
            unlocked[0].UnlockTimeUtc = providerTime;
            Hydrate(unlocked, WithOverride("alpha", new AchievementOverride { UnlockTimeUtc = overrideTime }));
            Assert.AreEqual(overrideTime, unlocked[0].UnlockTimeUtc);

            // A locked row must not gain an unlock time; that would read as unlocked downstream.
            var locked = Details("alpha");
            locked[0].Unlocked = false;
            Hydrate(locked, WithOverride("alpha", new AchievementOverride { UnlockTimeUtc = overrideTime }));
            Assert.IsNull(locked[0].UnlockTimeUtc);
        }

        [TestMethod]
        public void HydrateAll_ClearUnlockTime_RemovesTheProviderTimestampWithoutRelocking()
        {
            // Unchecking the unlock-time box stores a cleared state rather than an empty record:
            // an empty record falls back to the provider's own timestamp, which made the box
            // reappear after every edit.
            var details = Details("alpha");
            details[0].Unlocked = true;
            details[0].UnlockTimeUtc = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            Hydrate(details, WithOverride("alpha", new AchievementOverride { ClearUnlockTime = true }));

            Assert.IsNull(details[0].UnlockTimeUtc);
            Assert.IsTrue(details[0].Unlocked, "Clearing the timestamp must not relock the achievement.");
        }

        [TestMethod]
        public void HydrateAll_ClearUnlockTime_LosesToAnExplicitTimestamp()
        {
            var overrideTime = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
            var details = Details("alpha");
            details[0].Unlocked = true;
            details[0].UnlockTimeUtc = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            Hydrate(details, WithOverride("alpha", new AchievementOverride
            {
                ClearUnlockTime = true,
                UnlockTimeUtc = overrideTime
            }));

            Assert.AreEqual(overrideTime, details[0].UnlockTimeUtc);
        }

        [TestMethod]
        public void HydrateAll_Overrides_NeverChangeUnlockStatusOrRarity()
        {
            // Unlock status and rarity are provider-owned: an override must not be able to move
            // unlocked counts, completion, or the rarity a score is computed from.
            var details = Details("alpha");
            details[0].Unlocked = false;
            details[0].Rarity = RarityTier.UltraRare;
            details[0].GlobalPercentUnlocked = 2.5;

            Hydrate(details, WithOverride("alpha", new AchievementOverride
            {
                DisplayName = "Renamed",
                UnlockTimeUtc = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
                Points = 50
            }));

            Assert.IsFalse(details[0].Unlocked);
            Assert.AreEqual(RarityTier.UltraRare, details[0].Rarity);
            Assert.AreEqual(2.5, details[0].GlobalPercentUnlocked);
        }

        [TestMethod]
        public void HydrateAll_LegacyMirrorMapsOnly_StillResolveCustomization()
        {
            // A caller that populated only the legacy maps must still resolve its customization
            // while both shapes are live.
            var details = Details("alpha");
            Hydrate(details, new ResolvedGameCustomData
            {
                AchievementNotes = new Dictionary<string, string> { ["alpha"] = "legacy note" },
                AchievementCategoryOverrides = new Dictionary<string, string> { ["alpha"] = "Story" }
            });

            Assert.AreEqual("legacy note", details[0].AchievementNote);
            Assert.AreEqual("Story", details[0].Category);
        }

        private static ResolvedGameCustomData WithOverride(string apiName, AchievementOverride entry)
        {
            return new ResolvedGameCustomData
            {
                AchievementOverrides = new Dictionary<string, AchievementOverride> { [apiName] = entry }
            };
        }

        private static void Hydrate(List<AchievementDetail> details, ResolvedGameCustomData customData)
        {
            new AchievementDetailHydrator(new PlayniteAchievementsSettings()).HydrateAllWithCapstoneOverride(
                details,
                Guid.Empty,
                "steam",
                customData);
        }

        private static List<AchievementDetail> Details(params string[] apiNames)
        {
            return apiNames
                .Select(apiName => new AchievementDetail
                {
                    ApiName = apiName,
                    DisplayName = apiName
                })
                .ToList();
        }
    }
}
