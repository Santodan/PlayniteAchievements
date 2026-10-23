using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Models.Tagging;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.GameCustomData;

namespace PlayniteAchievements.Services.GameCustomData.Tests
{
    [TestClass]
    public class CustomizationAreaClassifierTests
    {
        private const string AutoCapstoneId = "auto1";

        [TestMethod]
        public void Classify_EmptyOrNullRecord_IsNone()
        {
            Assert.AreEqual(CustomizationArea.None, CustomizationAreaClassifier.Classify(null));
            Assert.AreEqual(CustomizationArea.None, CustomizationAreaClassifier.Classify(new GameCustomDataFile()));
        }

        [TestMethod]
        public void Classify_EachOverrideField_SetsOnlyItsArea()
        {
            AssertOnly(CustomizationArea.Icons, Override(new AchievementOverride { UnlockedIconPath = "a.png" }));
            AssertOnly(CustomizationArea.Icons, Override(new AchievementOverride { LockedIconPath = "b.png" }));
            AssertOnly(CustomizationArea.TrophyTypes, Override(new AchievementOverride { TrophyType = "gold" }));
            AssertOnly(CustomizationArea.TrophyTypes, Override(new AchievementOverride { ClearTrophyType = true }));
            AssertOnly(CustomizationArea.Points, Override(new AchievementOverride { Points = 50 }));
            AssertOnly(CustomizationArea.Notes, Override(new AchievementOverride { Note = "later" }));
            AssertOnly(CustomizationArea.Categories, Override(new AchievementOverride { Category = "Story" }));
            AssertOnly(CustomizationArea.Categories, Override(new AchievementOverride { ClearTags = true }));
            AssertOnly(CustomizationArea.None, Override(new AchievementOverride { DisplayName = "Renamed" }));
        }

        [TestMethod]
        public void Classify_GameLevelFields_SetTheirAreas()
        {
            AssertOnly(CustomizationArea.Icons, new GameCustomDataFile { UseSeparateLockedIconsOverride = true });
            AssertOnly(CustomizationArea.Order, new GameCustomDataFile { AchievementOrder = new List<string> { "a" } });
            AssertOnly(CustomizationArea.Goals, new GameCustomDataFile { GoalAchievementApiNames = new List<string> { "a" } });
            AssertOnly(CustomizationArea.Capstones, new GameCustomDataFile { ManualCapstoneApiName = "a" });
            AssertOnly(CustomizationArea.Capstones, new GameCustomDataFile { CapstonesMaterialized = true });
            AssertOnly(CustomizationArea.Source, new GameCustomDataFile { ExophaseSlugOverride = "slug" });
            AssertOnly(CustomizationArea.Source, new GameCustomDataFile { RetroAchievementsGameIdOverride = 12 });
            AssertOnly(CustomizationArea.ManualTracking, new GameCustomDataFile { CustomProviderId = "p" });
            AssertOnly(CustomizationArea.Notifications, new GameCustomDataFile { CaptureOverride = new GameCaptureOverride() });
            AssertOnly(CustomizationArea.Categories, new GameCustomDataFile { AchievementCategoryOrder = new List<string> { "Story" } });
            AssertOnly(CustomizationArea.None, new GameCustomDataFile { FilteredAchievementApiNames = new List<string> { "a" } });
        }

        [TestMethod]
        public void Classify_AutoCapstoneGenerationAlone_IsNone()
        {
            var apiName = CustomAchievementProjectionService.BuildApiName(AutoCapstoneId);
            var data = new GameCustomDataFile
            {
                AutoCapstoneGenerated = true,
                CustomAchievements = new List<CustomAchievementDefinition>
                {
                    new CustomAchievementDefinition { Id = AutoCapstoneId, DisplayName = "Mastery", IsAutoCapstone = true }
                },
                AchievementOrder = new List<string> { apiName },
                AchievementOverrides = new Dictionary<string, AchievementOverride>(StringComparer.OrdinalIgnoreCase)
                {
                    [apiName] = new AchievementOverride { Category = "Main" }
                },
                CapstonesMaterialized = true,
                Capstones = new List<CapstoneAssignment> { new CapstoneAssignment { ApiName = apiName } }
            };

            Assert.AreEqual(CustomizationArea.None, CustomizationAreaClassifier.Classify(data));
        }

        [TestMethod]
        public void Classify_UserCustomAchievement_IsManualTracking()
        {
            var data = new GameCustomDataFile
            {
                CustomAchievements = new List<CustomAchievementDefinition>
                {
                    new CustomAchievementDefinition { Id = "mine", DisplayName = "Mine" }
                }
            };

            AssertOnly(CustomizationArea.ManualTracking, data);
        }

        [TestMethod]
        public void TagsFor_PicksOneSideOfEveryPair()
        {
            var tags = new HashSet<TagType>(CustomizationTagAreas.TagsFor(CustomizationArea.Icons | CustomizationArea.Goals));

            Assert.AreEqual(CustomizationTagAreas.All.Count, tags.Count);
            Assert.IsTrue(tags.Contains(TagType.CustomIcons));
            Assert.IsTrue(tags.Contains(TagType.CustomGoals));
            Assert.IsTrue(tags.Contains(TagType.DefaultPoints));
            Assert.IsFalse(tags.Contains(TagType.DefaultIcons));
        }

        private static GameCustomDataFile Override(AchievementOverride value)
        {
            return new GameCustomDataFile
            {
                AchievementOverrides = new Dictionary<string, AchievementOverride>(StringComparer.OrdinalIgnoreCase)
                {
                    ["ACH_1"] = value
                }
            };
        }

        private static void AssertOnly(CustomizationArea expected, GameCustomDataFile data)
        {
            Assert.AreEqual(expected, CustomizationAreaClassifier.Classify(data));
        }
    }
}
