using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Models.Tagging;

namespace PlayniteAchievements.Models.Tagging.Tests
{
    [TestClass]
    public class TaggingSettingsTests
    {
        [TestMethod]
        public void InitializeDefaults_AddsCustomizedConfigs()
        {
            var settings = new TaggingSettings();

            settings.InitializeDefaults(TaggingSettings.GetDefaultDisplayName);

            Assert.IsTrue(settings.TagConfigs.ContainsKey(TagType.Customized));
            Assert.IsTrue(settings.TagConfigs.ContainsKey(TagType.NotCustomized));
            Assert.AreEqual("[PA] Customized", settings.CustomizedConfig.DisplayName);
            Assert.AreEqual("[PA] Not Customized", settings.NotCustomizedConfig.DisplayName);
        }

        [TestMethod]
        public void InitializeDefaults_AddsAreaPairsDisabled_AndKeepsStatusKindsEnabled()
        {
            var settings = new TaggingSettings();

            settings.InitializeDefaults(TaggingSettings.GetDefaultDisplayName);

            foreach (var tagType in CustomizationTagAreas.All.SelectMany(area => area.TagTypes))
            {
                Assert.IsFalse(settings.TagConfigs[tagType].IsEnabled, tagType.ToString());
            }

            Assert.IsTrue(settings.HasAchievementsConfig.IsEnabled);
            Assert.IsTrue(settings.CustomizedConfig.IsEnabled);
            // Customization kinds carry no English in code; without the locale files the key shows.
            Assert.AreEqual("[PA] LOCPlayAch_Tagging_Tag_CustomIcons", settings.TagConfigs[TagType.CustomIcons].DisplayName);
        }

        [TestMethod]
        public void InitializeDefaults_SavedSettingsWithoutAreaPairs_GainThemDisabled()
        {
            var settings = new TaggingSettings
            {
                TagConfigs = new Dictionary<TagType, TagConfig>
                {
                    [TagType.Completed] = new TagConfig { DisplayName = "Done", IsEnabled = false }
                }
            };

            settings.InitializeDefaults(TaggingSettings.GetDefaultDisplayName);

            Assert.AreEqual("Done", settings.CompletedConfig.DisplayName);
            Assert.IsFalse(settings.CompletedConfig.IsEnabled);
            Assert.IsTrue(settings.InProgressConfig.IsEnabled);
            Assert.IsFalse(settings.TagConfigs[TagType.CustomNotifications].IsEnabled);
        }

        [TestMethod]
        public void CustomizationRows_ShareConfigsWithTagConfigs()
        {
            var settings = new TaggingSettings();
            settings.InitializeDefaults(TaggingSettings.GetDefaultDisplayName);

            var rows = settings.CustomizationRows;
            rows.First(row => row.Third == settings.TagConfigs[TagType.CustomPoints]).Third.IsEnabled = true;

            // The Customized pair, then one row per area.
            Assert.AreEqual(CustomizationTagAreas.All.Count + 1, rows.Count);
            Assert.IsTrue(settings.TagConfigs[TagType.CustomPoints].IsEnabled);
            Assert.IsTrue(settings.Clone().TagConfigs[TagType.CustomPoints].IsEnabled);
        }

        [TestMethod]
        public void CustomizationRows_KeepEachLayerInItsColumn()
        {
            var settings = new TaggingSettings();
            settings.InitializeDefaults(TaggingSettings.GetDefaultDisplayName);
            var rows = settings.CustomizationRows;

            var points = rows.Single(row => row.Third == settings.TagConfigs[TagType.CustomPoints]);
            Assert.AreSame(settings.TagConfigs[TagType.PlatformPoints], points.First);
            Assert.AreSame(settings.TagConfigs[TagType.MetadataPoints], points.Second);
            Assert.AreSame(settings.TagConfigs[TagType.NoPoints], points.Fourth);

            var icons = rows.Single(row => row.Third == settings.TagConfigs[TagType.CustomIcons]);
            Assert.AreSame(settings.TagConfigs[TagType.PlatformIcons], icons.First);
            Assert.AreSame(settings.TagConfigs[TagType.MetadataIcons], icons.Second);
            Assert.AreSame(settings.TagConfigs[TagType.NoIcons], icons.Fourth);

            var order = rows.Single(row => row.Third == settings.TagConfigs[TagType.CustomOrder]);
            Assert.AreSame(settings.TagConfigs[TagType.PlatformOrder], order.First);
            Assert.IsNull(order.Fourth);

            var notes = rows.Single(row => row.Third == settings.TagConfigs[TagType.CustomNotes]);
            Assert.IsNull(notes.First);
            Assert.IsNull(notes.Second);
            Assert.AreSame(settings.TagConfigs[TagType.NoNotes], notes.Fourth);

            var notifications = rows.Single(row => row.Third == settings.TagConfigs[TagType.CustomNotifications]);
            Assert.AreEqual(1, notifications.GetConfigs().Count());

            var source = rows.Single(row => row.Third == settings.TagConfigs[TagType.CustomSource]);
            Assert.AreEqual(1, source.GetConfigs().Count());
        }

        [TestMethod]
        public void StatusRows_PairEveryStatusKindOnce()
        {
            var settings = new TaggingSettings();
            settings.InitializeDefaults(TaggingSettings.GetDefaultDisplayName);

            var configs = settings.StatusRows.SelectMany(row => row.GetConfigs()).ToList();

            CollectionAssert.AllItemsAreUnique(configs);
            CollectionAssert.Contains(configs, settings.ExcludedFromSummariesConfig);
            CollectionAssert.Contains(configs, settings.NoAchievementsConfig);

            var achievements = settings.StatusRows[0];
            Assert.AreSame(settings.HasAchievementsConfig, achievements.First);
            Assert.AreSame(settings.TagConfigs[TagType.CustomAchievements], achievements.Second);
            Assert.IsNull(achievements.Third);
            Assert.AreSame(settings.NoAchievementsConfig, achievements.Fourth);

            var tracking = settings.StatusRows[1];
            Assert.AreSame(settings.TagConfigs[TagType.ManualAchievements], tracking.First);
            Assert.AreSame(settings.TagConfigs[TagType.LinkedFile], tracking.Second);
            Assert.AreEqual("[PA] Manual Tracking", TaggingSettings.GetDefaultDisplayName(TagType.ManualAchievements));
            Assert.AreEqual("[PA] Linked File", TaggingSettings.GetDefaultDisplayName(TagType.LinkedFile));
            Assert.AreEqual("[PA] Platform Achievements", TaggingSettings.GetDefaultDisplayName(TagType.HasAchievements));
        }

        [TestMethod]
        public void Clone_PreservesCustomizedConfigs()
        {
            var settings = new TaggingSettings();
            settings.InitializeDefaults(TaggingSettings.GetDefaultDisplayName);
            settings.CustomizedConfig.DisplayName = "Custom Name";
            settings.CustomizedConfig.IsEnabled = false;
            settings.NotCustomizedConfig.DisplayName = "Plain Name";

            var clone = settings.Clone();

            Assert.AreEqual("Custom Name", clone.CustomizedConfig.DisplayName);
            Assert.IsFalse(clone.CustomizedConfig.IsEnabled);
            Assert.AreEqual("Plain Name", clone.NotCustomizedConfig.DisplayName);
        }

        [TestMethod]
        public void PersistedSettingsCopyFrom_PreservesCustomizedConfigs()
        {
            var source = new PersistedSettings
            {
                TaggingSettings = new TaggingSettings()
            };
            source.TaggingSettings.InitializeDefaults(TaggingSettings.GetDefaultDisplayName);
            source.TaggingSettings.CustomizedConfig.DisplayName = "Custom";
            source.TaggingSettings.NotCustomizedConfig.DisplayName = "Not Custom";

            var target = new PersistedSettings();
            target.CopyFrom(source);

            Assert.AreEqual("Custom", target.TaggingSettings.CustomizedConfig.DisplayName);
            Assert.AreEqual("Not Custom", target.TaggingSettings.NotCustomizedConfig.DisplayName);
        }

        [TestMethod]
        public void SavedConfigs_ForAKindThatNoLongerExists_AreSkippedOnLoad()
        {
            const string json = "{\"EnableTagging\":true,\"TagConfigs\":{" +
                "\"Completed\":{\"DisplayName\":\"Done\",\"IsEnabled\":true}," +
                "\"DefaultSource\":{\"DisplayName\":\"Gone\",\"IsEnabled\":true}}}";

            var settings = Newtonsoft.Json.JsonConvert.DeserializeObject<TaggingSettings>(json);

            Assert.IsTrue(settings.EnableTagging);
            Assert.AreEqual(1, settings.TagConfigs.Count);
            Assert.AreEqual("Done", settings.TagConfigs[TagType.Completed].DisplayName);
            StringAssert.Contains(Newtonsoft.Json.JsonConvert.SerializeObject(settings), "\"Completed\":");
        }

        [TestMethod]
        public void InitializeDefaults_NeverCreatesRetiredKinds_ButKeepsAStoredOne()
        {
            var fresh = new TaggingSettings();
            fresh.InitializeDefaults(TaggingSettings.GetDefaultDisplayName);
            Assert.IsFalse(fresh.TagConfigs.ContainsKey(TagType.CustomManualTracking));

            const string json = "{\"TagConfigs\":{\"CustomManualTracking\":{\"DisplayName\":\"[PA] Linked Source\",\"IsEnabled\":true,\"TagId\":\"11111111-1111-1111-1111-111111111111\"}}}";
            var stored = Newtonsoft.Json.JsonConvert.DeserializeObject<TaggingSettings>(json);
            stored.InitializeDefaults(TaggingSettings.GetDefaultDisplayName);
            Assert.IsTrue(stored.TagConfigs.ContainsKey(TagType.CustomManualTracking),
                "a stored retired config stays until the cleanup deletes its item");
        }

        [TestMethod]
        public void InitializeDefaults_FillsFeatureConfigsWithExactlyTheFeatureKinds()
        {
            var settings = new TaggingSettings();

            settings.InitializeDefaults(TaggingSettings.GetDefaultDisplayName);

            CollectionAssert.AreEquivalent(TaggingSettings.FeatureKinds.ToList(), settings.FeatureConfigs.Keys.ToList());
            Assert.IsTrue(settings.FeatureConfigs.Values.All(config => config.IsEnabled));
            Assert.AreEqual("[PA] Manual Tracking", settings.FeatureConfigs[TagType.ManualAchievements].DisplayName);
            Assert.IsFalse(settings.EnableFeatureSync);
        }

        [TestMethod]
        public void FeatureStatusRows_UseFeatureConfigs_NotTagConfigs()
        {
            var settings = new TaggingSettings();
            settings.InitializeDefaults(TaggingSettings.GetDefaultDisplayName);

            var row = settings.FeatureStatusRows.Single();

            Assert.AreSame(settings.FeatureConfigs[TagType.HasAchievements], row.First);
            Assert.AreNotSame(settings.HasAchievementsConfig, row.First);
            Assert.AreSame(settings.GetFeatureConfig(TagType.CustomAchievements), row.Second);
            Assert.AreSame(settings.FeatureConfigs[TagType.ManualAchievements], row.Third);
            Assert.AreSame(settings.FeatureConfigs[TagType.LinkedFile], row.Fourth);
            Assert.AreEqual(4, row.GetConfigs().Count());
        }

        [TestMethod]
        public void Clone_PreservesEnableFeatureSyncAndFeatureConfigs()
        {
            var settings = new TaggingSettings { EnableFeatureSync = true, CompletionStatusSwitchSeeded = true };
            settings.InitializeDefaults(TaggingSettings.GetDefaultDisplayName);
            settings.FeatureConfigs[TagType.ManualAchievements].DisplayName = "Linked";
            settings.FeatureConfigs[TagType.ManualAchievements].IsEnabled = false;
            settings.FeatureConfigs[TagType.ManualAchievements].TagId = System.Guid.NewGuid();

            var clone = settings.Clone();

            Assert.IsTrue(clone.EnableFeatureSync);
            Assert.IsTrue(clone.CompletionStatusSwitchSeeded);
            Assert.AreEqual("Linked", clone.FeatureConfigs[TagType.ManualAchievements].DisplayName);
            Assert.IsFalse(clone.FeatureConfigs[TagType.ManualAchievements].IsEnabled);
            Assert.AreEqual(settings.FeatureConfigs[TagType.ManualAchievements].TagId, clone.FeatureConfigs[TagType.ManualAchievements].TagId);
            Assert.AreNotSame(settings.FeatureConfigs[TagType.ManualAchievements], clone.FeatureConfigs[TagType.ManualAchievements]);
            Assert.AreEqual("[PA] Manual Tracking", clone.TagConfigs[TagType.ManualAchievements].DisplayName);
        }

        [TestMethod]
        public void PersistedSettingsCopyFrom_PreservesFeatureConfigs()
        {
            var source = new PersistedSettings
            {
                TaggingSettings = new TaggingSettings { EnableFeatureSync = true }
            };
            source.TaggingSettings.InitializeDefaults(TaggingSettings.GetDefaultDisplayName);
            source.TaggingSettings.FeatureConfigs[TagType.HasAchievements].DisplayName = "Store";

            var target = new PersistedSettings();
            target.CopyFrom(source);

            Assert.IsTrue(target.TaggingSettings.EnableFeatureSync);
            Assert.AreEqual("Store", target.TaggingSettings.FeatureConfigs[TagType.HasAchievements].DisplayName);
        }

        [TestMethod]
        public void SavedSettingsWithoutFeatureConfigs_LoadThenGainDefaults()
        {
            const string json = "{\"EnableTagging\":true,\"TagConfigs\":{\"Completed\":{\"DisplayName\":\"Done\",\"IsEnabled\":true}}}";

            var settings = Newtonsoft.Json.JsonConvert.DeserializeObject<TaggingSettings>(json);
            settings.InitializeDefaults(TaggingSettings.GetDefaultDisplayName);

            Assert.IsFalse(settings.EnableFeatureSync);
            Assert.IsFalse(settings.CompletionStatusSwitchSeeded);
            Assert.AreEqual(4, settings.FeatureConfigs.Count);
            Assert.AreEqual("[PA] Platform Achievements", settings.FeatureConfigs[TagType.HasAchievements].DisplayName);
        }

        [TestMethod]
        public void SavedFeatureConfigs_WithNullOrUnknownEntries_LoadAndGainDefaults()
        {
            const string json = "{\"FeatureConfigs\":{" +
                "\"HasAchievements\":{\"DisplayName\":\"Store\",\"IsEnabled\":false}," +
                "\"DefaultSource\":{\"DisplayName\":\"Gone\",\"IsEnabled\":true}}}";

            var settings = Newtonsoft.Json.JsonConvert.DeserializeObject<TaggingSettings>(json);
            settings.InitializeDefaults(TaggingSettings.GetDefaultDisplayName);

            Assert.AreEqual(4, settings.FeatureConfigs.Count);
            Assert.AreEqual("Store", settings.FeatureConfigs[TagType.HasAchievements].DisplayName);
            Assert.IsFalse(settings.FeatureConfigs[TagType.HasAchievements].IsEnabled);
            Assert.IsTrue(settings.FeatureConfigs[TagType.CustomAchievements].IsEnabled);

            var nulled = Newtonsoft.Json.JsonConvert.DeserializeObject<TaggingSettings>("{\"FeatureConfigs\":null}");
            nulled.InitializeDefaults(TaggingSettings.GetDefaultDisplayName);
            Assert.AreEqual(4, nulled.FeatureConfigs.Count);
        }

        [TestMethod]
        public void InitializeDefaults_KeepsBlankName_AndResolvesItToTheDefault()
        {
            var settings = new TaggingSettings
            {
                TagConfigs = new Dictionary<TagType, TagConfig>
                {
                    [TagType.Completed] = new TagConfig { DisplayName = "", IsEnabled = true }
                }
            };

            settings.InitializeDefaults(tagType => "Default " + tagType);

            Assert.AreEqual("", settings.CompletedConfig.DisplayName);
            Assert.AreEqual("Default Completed", settings.CompletedConfig.DefaultDisplayName);
            Assert.AreEqual("Default Completed", settings.CompletedConfig.ResolvedName);
            Assert.AreEqual("Default InProgress", settings.InProgressConfig.DisplayName);
        }

        [TestMethod]
        public void ResolvedName_PrefersTypedName()
        {
            var config = new TagConfig { DisplayName = "Done", DefaultDisplayName = "Default" };

            Assert.AreEqual("Done", config.ResolvedName);
            config.DisplayName = "  ";
            Assert.AreEqual("Default", config.ResolvedName);
        }

        [TestMethod]
        public void Clone_KeepsDefaultDisplayName()
        {
            var settings = new TaggingSettings();
            settings.InitializeDefaults(tagType => "Default " + tagType);
            settings.FeatureConfigs[TagType.HasAchievements].DisplayName = null;

            var clone = settings.Clone();

            Assert.AreEqual("Default HasAchievements", clone.FeatureConfigs[TagType.HasAchievements].ResolvedName);
        }
    }
}
