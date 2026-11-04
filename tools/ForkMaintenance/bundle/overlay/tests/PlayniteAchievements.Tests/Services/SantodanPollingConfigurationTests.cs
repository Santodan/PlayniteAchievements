using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Providers.Local;
using PlayniteAchievements.Services.InGameMonitoring;

namespace PlayniteAchievements.Tests.Services
{
    [TestClass]
    public class SantodanPollingConfigurationTests
    {
        [TestMethod]
        public void LargeNotificationTemplatesAndOtherGamesDoNotChangeTheSourceSignature()
        {
            var gameId = Guid.NewGuid();
            var settings = new LocalSettings();
            string Signature() => SantodanPollingConfiguration.Build(settings, gameId, "", "", "C:\\game", "Game", true);
            var original = Signature();
            settings.OverlayCustomSanPresetHtml = new string('x', 250000);
            settings.SteamAppIdOverrides[Guid.NewGuid()] = 123;
            Assert.AreEqual(original, Signature());
            Assert.IsTrue(original.Length < 4096, "Default source configuration should remain compact.");
            settings.SteamAppIdOverrides[gameId] = 456;
            Assert.AreNotEqual(original, Signature());
        }

        [TestMethod]
        public void MutatingAnOverrideInPlaceAndChangingAccountOrSchemaForcesRefresh()
        {
            var gameId = Guid.NewGuid();
            var settings = new LocalSettings();
            string Signature() => SantodanPollingConfiguration.Build(settings, gameId, "", "", "C:\\game", "Game", true);
            var before = Signature();
            settings.LocalFolderOverrides[gameId] = "C:\\new-save";
            Assert.AreNotEqual(before, Signature());
            before = Signature();
            settings.SteamAppCacheUserId = "another-account";
            Assert.AreNotEqual(before, Signature());
            before = Signature();
            settings.CustomSchemaEnabledOverrides[gameId] = false;
            Assert.AreNotEqual(before, Signature(), "An absent override differs from explicitly disabled.");
            before = Signature();
            settings.ExcludedLocalPaths = "C:\\excluded";
            Assert.AreNotEqual(before, Signature());
        }
    }
}
