using Microsoft.VisualStudio.TestTools.UnitTesting;
using Playnite.SDK.Models;
using PlayniteAchievements.Providers.RetroAchievements;

namespace PlayniteAchievements.Tests.Providers
{
    [TestClass]
    public class RetroAchievementsCapabilityHelperTests
    {
        [TestMethod]
        public void HasConfiguredCredentials_RequiresUsernameAndApiKey()
        {
            var missingApiKey = new RetroAchievementsSettings
            {
                RaUsername = "user",
                RaWebApiKey = ""
            };

            var configured = new RetroAchievementsSettings
            {
                RaUsername = "user",
                RaWebApiKey = "key"
            };

            Assert.IsFalse(RetroAchievementsCapabilityHelper.HasConfiguredCredentials(missingApiKey));
            Assert.IsTrue(RetroAchievementsCapabilityHelper.HasConfiguredCredentials(configured));
        }

        [TestMethod]
        public void EnableFuzzyNameMatching_DefaultsToTrue_AndRoundTripsThroughJson()
        {
            var defaults = new RetroAchievementsSettings();
            Assert.IsTrue(defaults.EnableFuzzyNameMatching);

            var disabled = new RetroAchievementsSettings
            {
                EnableFuzzyNameMatching = false
            };

            var json = disabled.SerializeToJson();
            Assert.IsTrue(json.Contains(nameof(RetroAchievementsSettings.EnableFuzzyNameMatching)));

            var reloaded = new RetroAchievementsSettings();
            reloaded.DeserializeFromJson(json);
            Assert.IsFalse(reloaded.EnableFuzzyNameMatching);
        }

        [TestMethod]
        public void CanSetOverride_NoPlatformMetadata_ReturnsTrue()
        {
            var game = new Game
            {
                Name = "Manual Import"
            };

            Assert.IsTrue(RetroAchievementsCapabilityHelper.CanSetOverride(game));
        }

        [TestMethod]
        public void CanUseNameFallback_NoPlatformMetadataAndEnabled_ReturnsTrue()
        {
            var settings = new RetroAchievementsSettings
            {
                EnableRaNameFallback = true
            };

            var game = new Game
            {
                Name = "Super Mario Bros."
            };

            Assert.IsTrue(RetroAchievementsCapabilityHelper.CanUseNameFallback(game, settings, hasResolvedConsole: false));
            Assert.IsTrue(RetroAchievementsCapabilityHelper.CanUsePlatformlessNameFallback(game, settings));
        }

        [TestMethod]
        public void CanUseNameFallback_Disabled_ReturnsFalse()
        {
            var settings = new RetroAchievementsSettings
            {
                EnableRaNameFallback = false
            };

            var game = new Game
            {
                Name = "Super Mario Bros."
            };

            Assert.IsFalse(RetroAchievementsCapabilityHelper.CanUseNameFallback(game, settings, hasResolvedConsole: false));
            Assert.IsFalse(RetroAchievementsCapabilityHelper.CanUsePlatformlessNameFallback(game, settings));
        }

        [TestMethod]
        public void CanUseNameFallback_BlankName_ReturnsFalse()
        {
            var settings = new RetroAchievementsSettings
            {
                EnableRaNameFallback = true
            };

            var game = new Game
            {
                Name = "   "
            };

            Assert.IsFalse(RetroAchievementsCapabilityHelper.CanUseNameFallback(game, settings, hasResolvedConsole: false));
            Assert.IsFalse(RetroAchievementsCapabilityHelper.CanUsePlatformlessNameFallback(game, settings));
        }
    }
}
