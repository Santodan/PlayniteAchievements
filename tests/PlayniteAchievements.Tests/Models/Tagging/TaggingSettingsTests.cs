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

            foreach (var area in CustomizationTagAreas.All)
            {
                Assert.IsFalse(settings.TagConfigs[area.CustomTag].IsEnabled, area.CustomTag.ToString());
                Assert.IsFalse(settings.TagConfigs[area.DefaultTag].IsEnabled, area.DefaultTag.ToString());
            }

            Assert.IsTrue(settings.HasAchievementsConfig.IsEnabled);
            Assert.IsTrue(settings.CustomizedConfig.IsEnabled);
            Assert.AreEqual("[PA] Custom Icons", settings.TagConfigs[TagType.CustomIcons].DisplayName);
            Assert.AreEqual("[PA] Default Icons", settings.TagConfigs[TagType.DefaultIcons].DisplayName);
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
            rows.First(row => row.Area.Area == CustomizationArea.Points).CustomConfig.IsEnabled = true;

            Assert.AreEqual(CustomizationTagAreas.All.Count, rows.Count);
            Assert.IsTrue(settings.TagConfigs[TagType.CustomPoints].IsEnabled);
            Assert.IsTrue(settings.Clone().TagConfigs[TagType.CustomPoints].IsEnabled);
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
    }
}
