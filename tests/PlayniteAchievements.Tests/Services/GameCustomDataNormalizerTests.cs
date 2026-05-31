using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services;
using System;
using System.Collections.Generic;

namespace PlayniteAchievements.Services.Tests
{
    [TestClass]
    public class GameCustomDataNormalizerTests
    {
        [TestMethod]
        public void HasVisibleCustomization_EmptyData_ReturnsFalse()
        {
            Assert.IsFalse(GameCustomDataNormalizer.HasVisibleCustomization(new GameCustomDataFile()));
        }

        [TestMethod]
        public void HasVisibleCustomization_ExclusionsOnly_ReturnsFalse()
        {
            var data = new GameCustomDataFile
            {
                ExcludedFromRefreshes = true,
                ExcludedFromSummaries = true
            };

            Assert.IsFalse(GameCustomDataNormalizer.HasVisibleCustomization(data));
        }

        [TestMethod]
        public void HasVisibleCustomization_VisibleCustomizationKinds_ReturnTrue()
        {
            var cases = new[]
            {
                new GameCustomDataFile { UseSeparateLockedIconsOverride = true },
                new GameCustomDataFile { ManualCapstoneApiName = "capstone" },
                new GameCustomDataFile { AchievementOrder = new List<string> { "ach_one" } },
                new GameCustomDataFile
                {
                    AchievementCategoryOverrides = new Dictionary<string, string>
                    {
                        ["ach_one"] = "Category"
                    }
                },
                new GameCustomDataFile
                {
                    AchievementCategoryTypeOverrides = new Dictionary<string, string>
                    {
                        ["ach_one"] = "DLC"
                    }
                },
                new GameCustomDataFile
                {
                    AchievementUnlockedIconOverrides = new Dictionary<string, string>
                    {
                        ["ach_one"] = "https://example.com/unlocked.png"
                    }
                },
                new GameCustomDataFile
                {
                    AchievementLockedIconOverrides = new Dictionary<string, string>
                    {
                        ["ach_one"] = "https://example.com/locked.png"
                    }
                },
                new GameCustomDataFile { RetroAchievementsGameIdOverride = 42 },
                new GameCustomDataFile { XeniaTitleIdOverride = "4d5307e6" },
                new GameCustomDataFile { ShadPS4MatchIdOverride = "npwr12345_00" },
                new GameCustomDataFile { ForceUseExophase = true },
                new GameCustomDataFile { ExophaseSlugOverride = "game-slug" },
                new GameCustomDataFile
                {
                    ProviderOverride = new ProviderOverrideData
                    {
                        ProviderKey = "Steam",
                        Value = "480"
                    }
                },
                new GameCustomDataFile
                {
                    ManualLink = new ManualAchievementLink
                    {
                        SourceKey = "Steam",
                        SourceGameId = "123"
                    }
                }
            };

            foreach (var item in cases)
            {
                Assert.IsTrue(GameCustomDataNormalizer.HasVisibleCustomization(item));
            }
        }

        [TestMethod]
        public void NormalizeInternal_CanonicalProviderOverride_NormalizesAndClearsLegacyFields()
        {
            var gameId = Guid.NewGuid();
            var normalized = GameCustomDataNormalizer.NormalizeInternal(
                new GameCustomDataFile
                {
                    PlayniteGameId = gameId,
                    ProviderOverride = new ProviderOverrideData
                    {
                        ProviderKey = "steam",
                        Value = " 480 "
                    },
                    RetroAchievementsGameIdOverride = 12345,
                    XeniaTitleIdOverride = "0x4d5307e6",
                    ShadPS4MatchIdOverride = "npwr12345_00",
                    ForceUseExophase = true,
                    ExophaseSlugOverride = "legacy-slug"
                },
                gameId);

            Assert.AreEqual(2, normalized.SchemaVersion);
            AssertProviderOverride(normalized, "Steam", "480");
            AssertLegacyProviderFieldsCleared(normalized);
        }

        [TestMethod]
        public void NormalizeInternal_InvalidCanonicalProviderOverride_DropsOverride()
        {
            var gameId = Guid.NewGuid();
            var normalized = GameCustomDataNormalizer.NormalizeInternal(
                new GameCustomDataFile
                {
                    PlayniteGameId = gameId,
                    ProviderOverride = new ProviderOverrideData
                    {
                        ProviderKey = "Steam",
                        Value = "0"
                    }
                },
                gameId);

            Assert.IsNull(normalized.ProviderOverride);
        }

        [TestMethod]
        public void NormalizeInternal_LegacyProviderFields_NormalizeIntoCanonicalOverride()
        {
            AssertLegacyProviderOverride(
                new GameCustomDataFile { RetroAchievementsGameIdOverride = 42 },
                "RetroAchievements",
                "42");
            AssertLegacyProviderOverride(
                new GameCustomDataFile { XeniaTitleIdOverride = "0x4d5307e6" },
                "Xenia",
                "4D5307E6");
            AssertLegacyProviderOverride(
                new GameCustomDataFile { ShadPS4MatchIdOverride = "npwr12345_00" },
                "ShadPS4",
                "NPWR12345_00");
            AssertLegacyProviderOverride(
                new GameCustomDataFile { ForceUseExophase = true },
                "Exophase",
                null);
            AssertLegacyProviderOverride(
                new GameCustomDataFile { ExophaseSlugOverride = " exophase-slug " },
                "Exophase",
                "exophase-slug");
        }

        [TestMethod]
        public void NormalizeInternal_ExophaseProviderOverride_AllowsEmptyValue()
        {
            var gameId = Guid.NewGuid();
            var normalized = GameCustomDataNormalizer.NormalizeInternal(
                new GameCustomDataFile
                {
                    PlayniteGameId = gameId,
                    ProviderOverride = new ProviderOverrideData
                    {
                        ProviderKey = "Exophase",
                        Value = " "
                    }
                },
                gameId);

            AssertProviderOverride(normalized, "Exophase", null);
        }

        [TestMethod]
        public void NormalizeInternal_Rpcs3ProviderOverride_NormalizesCanonicalValue()
        {
            var gameId = Guid.NewGuid();
            var normalized = GameCustomDataNormalizer.NormalizeInternal(
                new GameCustomDataFile
                {
                    PlayniteGameId = gameId,
                    ProviderOverride = new ProviderOverrideData
                    {
                        ProviderKey = "rpcs3",
                        Value = " npwr12345_00 "
                    }
                },
                gameId);

            AssertProviderOverride(normalized, "RPCS3", "NPWR12345_00");
        }

        private static void AssertLegacyProviderOverride(
            GameCustomDataFile data,
            string providerKey,
            string value)
        {
            var gameId = Guid.NewGuid();
            data.PlayniteGameId = gameId;

            var normalized = GameCustomDataNormalizer.NormalizeInternal(data, gameId);

            AssertProviderOverride(normalized, providerKey, value);
            AssertLegacyProviderFieldsCleared(normalized);
        }

        private static void AssertProviderOverride(
            GameCustomDataFile data,
            string providerKey,
            string value)
        {
            Assert.IsNotNull(data.ProviderOverride);
            Assert.AreEqual(providerKey, data.ProviderOverride.ProviderKey);
            Assert.AreEqual(value, data.ProviderOverride.Value);
        }

        private static void AssertLegacyProviderFieldsCleared(GameCustomDataFile data)
        {
            Assert.IsNull(data.RetroAchievementsGameIdOverride);
            Assert.IsNull(data.XeniaTitleIdOverride);
            Assert.IsNull(data.ShadPS4MatchIdOverride);
            Assert.IsNull(data.ForceUseExophase);
            Assert.IsNull(data.ExophaseSlugOverride);
        }
    }
}
