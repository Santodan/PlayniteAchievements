using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Providers.Exophase;

namespace PlayniteAchievements.Providers.Tests
{
    [TestClass]
    public class ExophaseSettingsTests
    {
        [TestMethod]
        public void Constructor_DefaultsManagedProvidersToStubOnlyPlatforms()
        {
            var settings = new ExophaseSettings();

            Assert.IsTrue(settings.ManagedProviders.Contains("blizzard"));
            Assert.IsTrue(settings.ManagedProviders.Contains("origin"));
            Assert.IsTrue(settings.ManagedProviders.Contains("android"));
            Assert.IsTrue(settings.ManagedProviders.Contains("apple"));
            Assert.IsTrue(settings.ManagedProviders.Contains("ubisoft"));

            Assert.IsFalse(settings.ManagedProviders.Contains("steam"));
            Assert.IsFalse(settings.ManagedProviders.Contains("gog"));
            Assert.IsFalse(settings.ManagedProviders.Contains("epic"));
            Assert.IsFalse(settings.ManagedProviders.Contains("psn"));
            Assert.IsFalse(settings.ManagedProviders.Contains("xbox"));
            Assert.IsFalse(settings.ManagedProviders.Contains("retro"));
        }

        [TestMethod]
        public void DeserializeFromJson_WhenManagedProvidersMissing_KeepsDefaultStubPlatforms()
        {
            var settings = new ExophaseSettings();

            settings.DeserializeFromJson("{\"IsEnabled\":true}");

            Assert.IsTrue(settings.ManagedProviders.Contains("blizzard"));
            Assert.IsTrue(settings.ManagedProviders.Contains("origin"));
            Assert.IsTrue(settings.ManagedProviders.Contains("android"));
            Assert.IsTrue(settings.ManagedProviders.Contains("apple"));
            Assert.IsTrue(settings.ManagedProviders.Contains("ubisoft"));
        }
    }
}
