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
        public void NativeSourceProfileSchemaAndSwitchesInvalidateOnlyRelevantConfiguration()
        {
            var id = Guid.NewGuid(); var other = Guid.NewGuid(); var settings = new LocalSettings();
            string Signature() => SantodanPollingConfiguration.Build(settings, id, "", "", "C:\\game", "Game", true);
            var before = Signature(); settings.NativeSourceOverrides[other] = new NativeSourceConfiguration { Source = "sms" };
            Assert.AreEqual(before, Signature());
            settings.NativeSourceOverrides[id] = new NativeSourceConfiguration { Source = "ghostship", SavePath = "save_0.json" };
            Assert.AreNotEqual(before, Signature()); before = Signature(); settings.NativeSourceOverrides[id].SavePath = "save_1.json";
            Assert.AreNotEqual(before, Signature()); before = Signature(); settings.NativeSourcesEnabled = false;
            Assert.AreNotEqual(before, Signature());
        }
        [TestMethod]
        public void SharedNativeFolderAndUserChangesInvalidateSourceConfiguration()
        {
            var id = Guid.NewGuid(); var settings = new LocalSettings();
            string Signature() => SantodanPollingConfiguration.Build(settings, id, "", "", "C:\\game", "Game", true);
            var before = Signature(); settings.NativeSourceLocations["kytyps5"] = new NativeSourceLocations { EmulatorFolders = "C:\\Kyty" };
            Assert.AreNotEqual(before, Signature()); before = Signature(); settings.NativeSourceLocations["kytyps5"].UserId = "2";
            Assert.AreNotEqual(before, Signature()); before = Signature(); settings.NativeGameLibraryFolders = "D:\\Ports";
            Assert.AreNotEqual(before, Signature()); before = Signature(); settings.NativeUseQuiver = false;
            Assert.AreNotEqual(before, Signature());
        }
        [TestMethod]
        public void NativeMenuIconChangesDoNotInvalidateAchievementSourceConfiguration()
        {
            var settings = new LocalSettings(); var id = Guid.NewGuid();
            string Signature() => SantodanPollingConfiguration.Build(settings, id, "", "", "C:\\game", "Game", true);
            var original = Signature();
            settings.NativeSourceIcons["ghostship"] = new NativeSourceIconSettings { CustomIconPath = "custom.png", BorrowedProviderIconKey = "PSN" };
            Assert.AreEqual(original, Signature());
        }
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
