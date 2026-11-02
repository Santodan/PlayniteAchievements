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
            AssertOnly(CustomizationArea.None, Override(new AchievementOverride { Category = "Default" }));
            AssertOnly(CustomizationArea.Tags, Override(new AchievementOverride { ClearTags = true }));
            AssertOnly(CustomizationArea.Tags, Override(new AchievementOverride { Tags = "Missable" }));
            AssertOnly(CustomizationArea.Text, Override(new AchievementOverride { DisplayName = "Renamed" }));
            AssertOnly(CustomizationArea.Text, Override(new AchievementOverride { Description = "Reworded" }));
            AssertOnly(CustomizationArea.Text, Override(new AchievementOverride { ClearDescription = true }));
            AssertOnly(CustomizationArea.None, Override(new AchievementOverride { DisplayName = " " }));
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

            AssertOnly(CustomizationArea.ManualTracking | CustomizationArea.Text, data);
        }

        [TestMethod]
        public void HasVisibleCustomization_CountsNotificationOverrides_WithoutWideningTheExportGate()
        {
            var data = new GameCustomDataFile { NotificationBehaviorOverride = new GameNotificationBehaviorOverride() };

            Assert.IsTrue(GameCustomDataNormalizer.HasVisibleCustomization(data));
            Assert.IsFalse(GameCustomDataNormalizer.HasPortableData(data));
        }

        [TestMethod]
        public void Classify_CustomAchievementValues_CountAsCustom_ButNotTheDefaultCategory()
        {
            var data = new GameCustomDataFile
            {
                CustomAchievements = new List<CustomAchievementDefinition>
                {
                    new CustomAchievementDefinition
                    {
                        Id = "mine",
                        DisplayName = "Mine",
                        TrophyType = "gold",
                        Points = 30,
                        Category = "Default",
                        Tags = "Missable",
                        UnlockedIconPath = "mine.png",
                        IsCapstone = true
                    }
                }
            };

            Assert.AreEqual(
                CustomizationArea.ManualTracking | CustomizationArea.TrophyTypes | CustomizationArea.Points |
                CustomizationArea.Tags | CustomizationArea.Icons | CustomizationArea.Capstones | CustomizationArea.Text,
                CustomizationAreaClassifier.Classify(data));
        }

        [TestMethod]
        public void TagsFor_Text_PlatformMetadataAndCustom_WithNoNoneKind()
        {
            var layers = new GameDataLayers
            {
                Platform = CustomizationArea.Text,
                Metadata = CustomizationArea.Text,
                Present = CustomizationArea.Text
            };

            var tags = new HashSet<TagType>(CustomizationTagAreas.TagsFor(CustomizationArea.Text, layers));
            CollectionAssert.IsSubsetOf(new[] { TagType.PlatformText, TagType.MetadataText, TagType.CustomText }, tags.ToList());

            var none = new HashSet<TagType>(CustomizationTagAreas.TagsFor(
                CustomizationArea.None,
                new GameDataLayers { Platform = CustomizationArea.None, Metadata = CustomizationArea.None, Present = CustomizationArea.None }));
            Assert.IsFalse(none.Any(tag => tag == TagType.PlatformText || tag == TagType.MetadataText || tag == TagType.CustomText));
        }

        [TestMethod]
        public void TagsFor_MasksRecordedBeforeText_ReadTheCachedTextAsThePlatforms()
        {
            var layers = new GameDataLayers
            {
                Platform = CustomizationArea.Icons,
                Metadata = CustomizationArea.Points,
                Present = CustomizationArea.Icons | CustomizationArea.Points | CustomizationArea.Text
            };

            var tags = new HashSet<TagType>(CustomizationTagAreas.TagsFor(CustomizationArea.None, layers));

            Assert.IsTrue(tags.Contains(TagType.PlatformText));
            Assert.IsFalse(tags.Contains(TagType.MetadataText));

            var unknown = new HashSet<TagType>(CustomizationTagAreas.TagsFor(CustomizationArea.None, new GameDataLayers { Present = CustomizationArea.Text }));
            Assert.IsFalse(unknown.Contains(TagType.PlatformText), "unknown masks still give no platform layer");
        }

        [TestMethod]
        public void TagsFor_PairedAndSingleAreas()
        {
            var tags = new HashSet<TagType>(CustomizationTagAreas.TagsFor(CustomizationArea.Goals, default(GameDataLayers)));

            Assert.IsTrue(tags.Contains(TagType.CustomGoals));
            Assert.IsFalse(tags.Contains(TagType.NoGoals));
            Assert.IsTrue(tags.Contains(TagType.NoNotes));
            Assert.IsTrue(tags.Contains(TagType.PlatformOrder));
            Assert.IsFalse(tags.Contains(TagType.CustomNotifications));
            Assert.IsFalse(tags.Contains(TagType.CustomSource));

            var reordered = new HashSet<TagType>(CustomizationTagAreas.TagsFor(CustomizationArea.Order | CustomizationArea.Source, default(GameDataLayers)));
            Assert.IsTrue(reordered.Contains(TagType.CustomOrder));
            Assert.IsFalse(reordered.Contains(TagType.PlatformOrder));
            Assert.IsTrue(reordered.Contains(TagType.CustomSource));
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
                new[] { TagType.PlatformTrophies, TagType.MetadataTrophies, TagType.CustomTrophies, TagType.NoPoints },
                tags.ToList());
            Assert.IsFalse(tags.Contains(TagType.NoTrophies));
            Assert.IsFalse(tags.Contains(TagType.MetadataIcons));
        }

        [TestMethod]
        public void TagsFor_UnknownLayers_OnlyCustomAndNoneFromCachedValues()
        {
            var layers = new GameDataLayers { Present = CustomizationArea.TrophyTypes };

            var tags = new HashSet<TagType>(CustomizationTagAreas.TagsFor(CustomizationArea.Points, layers));

            Assert.IsFalse(tags.Contains(TagType.PlatformTrophies));
            Assert.IsFalse(tags.Contains(TagType.NoTrophies));
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
