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

            var configs = settings.StatusRows.SelectMany(row => new[] { row.First, row.Second, row.Third }).Where(config => config != null).ToList();

            CollectionAssert.AllItemsAreUnique(configs);
            CollectionAssert.Contains(configs, settings.ExcludedFromSummariesConfig);
            CollectionAssert.Contains(configs, settings.NoAchievementsConfig);

            var achievements = settings.StatusRows[0];
            Assert.AreSame(settings.HasAchievementsConfig, achievements.First);
            Assert.AreSame(settings.TagConfigs[TagType.CustomAchievements], achievements.Second);
            Assert.AreSame(settings.NoAchievementsConfig, achievements.Third);
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
    }
}
