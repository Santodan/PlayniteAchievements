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
