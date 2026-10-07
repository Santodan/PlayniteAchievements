using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.ThemeMigration;

namespace PlayniteAchievements.Tests.Models
{
    [TestClass]
    public class ThemeMigrationSettingsTests
    {
        [TestMethod]
        public void OptionsSurviveSerializationCloneAndCopyWithoutSharingControlChoices()
        {
            var source = new PersistedSettings
            {
                ThemeMigrationUseScrollableAchievements = true,
                ThemeMigrationHighlightLatestAchievement = false,
                ThemeMigrationMode = MigrationMode.Custom,
                ThemeMigrationControlOptions = new Dictionary<string, bool>
                {
                    ["PluginButton"] = false,
                    ["PluginCompactUnlocked"] = true
                }
            };
            var copied = new PersistedSettings();
            copied.CopyFrom(source);
            foreach (var restored in new[] { source.Clone(), copied,
                JsonConvert.DeserializeObject<PersistedSettings>(JsonConvert.SerializeObject(source)) })
            {
                Assert.IsTrue(restored.ThemeMigrationUseScrollableAchievements);
                Assert.IsFalse(restored.ThemeMigrationHighlightLatestAchievement);
                Assert.AreEqual(MigrationMode.Custom, restored.ThemeMigrationMode);
                var selection = CustomMigrationSelection.FromSettings(restored);
                Assert.IsFalse(selection.ShouldModernizeControl("PluginButton"));
                Assert.IsTrue(selection.ShouldModernizeControl("PluginCompactUnlocked"));
                Assert.IsFalse(selection.HighlightLatestUnlockedAchievement);
                restored.ThemeMigrationControlOptions["PluginButton"] = true;
                Assert.IsFalse(source.ThemeMigrationControlOptions["PluginButton"]);
            }
        }

        [TestMethod]
        public void FullModeIgnoresCustomChoicesButRespectsScrollableAndHighlightOptions()
        {
            var settings = new PersistedSettings
            {
                ThemeMigrationMode = MigrationMode.Full,
                ThemeMigrationHighlightLatestAchievement = false,
                ThemeMigrationControlOptions = new Dictionary<string, bool> { ["PluginButton"] = false }
            };
            var selection = CustomMigrationSelection.FromSettings(settings);
            Assert.IsTrue(selection.ShouldModernizeControl("PluginButton"));
            Assert.IsFalse(selection.ShouldModernizeControl("PluginCompactUnlocked"));
            Assert.IsFalse(selection.HighlightLatestUnlockedAchievement);
            settings.ThemeMigrationUseScrollableAchievements = true;
            Assert.IsTrue(CustomMigrationSelection.FromSettings(settings).ShouldModernizeControl("PluginCompactUnlocked"));
        }

        [TestMethod]
        public void CustomModeUsesDefaultsForChoicesNotYetSaved()
        {
            var settings = new PersistedSettings { ThemeMigrationMode = MigrationMode.Custom };
            var selection = CustomMigrationSelection.FromSettings(settings);
            Assert.IsTrue(selection.ShouldModernizeControl("PluginButton"));
            Assert.IsFalse(selection.ShouldModernizeControl("PluginCompactUnlocked"));
            Assert.IsTrue(selection.HighlightLatestUnlockedAchievement);
        }
    }
}
