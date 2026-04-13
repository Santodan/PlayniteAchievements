using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services;
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
                new GameCustomDataFile { ForceUseExophase = true },
                new GameCustomDataFile { ExophaseSlugOverride = "game-slug" },
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
    }
}
