using System;
using System.Collections.Generic;
using System.Linq;
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
            AssertOnly(CustomizationArea.Tags, Override(new AchievementOverride { ClearTags = true }));
            AssertOnly(CustomizationArea.Tags, Override(new AchievementOverride { Tags = "Missable" }));
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
            AssertOnly(CustomizationArea.Tags, new GameCustomDataFile
            {
                AchievementTags = new List<PlayniteAchievements.Models.Achievements.AchievementTagDefinition>
                {
                    new PlayniteAchievements.Models.Achievements.AchievementTagDefinition()
                }
            });
            AssertOnly(CustomizationArea.None, new GameCustomDataFile { FilteredAchievementApiNames = new List<string> { "a" } });
        }

        [TestMethod]
        public void Classify_AutoCapstoneGenerationAlone_IsCapstonesOnly()
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

            Assert.AreEqual(CustomizationArea.Capstones, CustomizationAreaClassifier.Classify(data));
        }

        [TestMethod]
        public void Classify_AutoCapstoneDefinitionBeforeNomination_IsCapstones()
        {
            var data = new GameCustomDataFile
            {
                CustomAchievements = new List<CustomAchievementDefinition>
                {
                    new CustomAchievementDefinition { Id = AutoCapstoneId, DisplayName = "Mastery", IsAutoCapstone = true }
                }
            };

            AssertOnly(CustomizationArea.Capstones, data);
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
        public void HasVisibleCustomization_CountsNotificationOverrides_WithoutWideningTheExportGate()
        {
            var data = new GameCustomDataFile { NotificationBehaviorOverride = new GameNotificationBehaviorOverride() };

            Assert.IsTrue(GameCustomDataNormalizer.HasVisibleCustomization(data));
            Assert.IsFalse(GameCustomDataNormalizer.HasPortableData(data));
        }

        [TestMethod]
        public void TagsFor_PairedAndSingleAreas()
        {
            var tags = new HashSet<TagType>(CustomizationTagAreas.TagsFor(CustomizationArea.Goals, default(GameDataLayers)));

            Assert.IsTrue(tags.Contains(TagType.CustomGoals));
            Assert.IsTrue(tags.Contains(TagType.DefaultNotes));
            Assert.IsFalse(tags.Contains(TagType.CustomSource));
            Assert.IsTrue(CustomizationTagAreas.TagsFor(CustomizationArea.Source, default(GameDataLayers)).Contains(TagType.CustomSource));
        }

        [TestMethod]
        public void TagsFor_LayeredAreas_EmitEveryLayerThatApplies()
        {
            var layers = new GameDataLayers
            {
                Platform = CustomizationArea.TrophyTypes | CustomizationArea.Icons,
                Metadata = CustomizationArea.TrophyTypes,
                Present = CustomizationArea.None
            };

            var tags = new HashSet<TagType>(CustomizationTagAreas.TagsFor(CustomizationArea.TrophyTypes, layers));

            CollectionAssert.IsSubsetOf(
                new[] { TagType.PlatformTrophyTypes, TagType.MetadataTrophyTypes, TagType.CustomTrophyTypes, TagType.PlatformIcons, TagType.NoPoints },
                tags.ToList());
            Assert.IsFalse(tags.Contains(TagType.NoTrophyTypes));
            Assert.IsFalse(tags.Contains(TagType.MetadataIcons));
            Assert.IsFalse(tags.Contains(TagType.NoIcons));
        }

        [TestMethod]
        public void TagsFor_UnknownLayers_OnlyCustomAndNoneFromCachedValues()
        {
            var layers = new GameDataLayers { Present = CustomizationArea.Icons };

            var tags = new HashSet<TagType>(CustomizationTagAreas.TagsFor(CustomizationArea.Points, layers));

            Assert.IsFalse(tags.Contains(TagType.PlatformIcons));
            Assert.IsFalse(tags.Contains(TagType.NoIcons));
            Assert.IsTrue(tags.Contains(TagType.CustomPoints));
            Assert.IsFalse(tags.Contains(TagType.NoPoints));
            Assert.IsTrue(tags.Contains(TagType.NoCategories));
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
