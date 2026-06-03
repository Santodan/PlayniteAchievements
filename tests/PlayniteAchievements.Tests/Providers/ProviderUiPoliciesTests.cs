using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Providers;

namespace PlayniteAchievements.Tests.Providers
{
    [TestClass]
    public class ProviderUiPoliciesTests
    {
        [DataTestMethod]
        [DataRow("BattleNet")]
        [DataRow("Steam")]
        [DataRow("Epic")]
        [DataRow("GOG")]
        [DataRow("EA")]
        [DataRow("Hoyoverse")]
        public void ShouldHideFromSetupSurfaces_ReturnsFalse_ForFirstClassProviders(string providerKey)
        {
            Assert.IsFalse(ProviderUiPolicies.ShouldHideFromSetupSurfaces(providerKey));
        }

        [DataTestMethod]
        [DataRow("GooglePlay")]
        [DataRow("Apple")]
        [DataRow("Ubisoft")]
        public void ShouldHideFromSetupSurfaces_ReturnsTrue_ForProvidersWithoutSetupSurfaces(string providerKey)
        {
            Assert.IsTrue(ProviderUiPolicies.ShouldHideFromSetupSurfaces(providerKey));
        }

        [TestMethod]
        public void GetSettingsGroupResourceKey_GroupsHoyoverseWithManualAggregators()
        {
            Assert.AreEqual(
                "LOCPlayAch_Settings_ProviderGroup_ManualAggregators",
                ProviderUiPolicies.GetSettingsGroupResourceKey("Hoyoverse"));
        }

        [TestMethod]
        public void TryGetSettingsRedirectProviderKey_DoesNotRedirectEa()
        {
            Assert.IsFalse(ProviderUiPolicies.TryGetSettingsRedirectProviderKey("EA", out var redirectProviderKey));
            Assert.IsNull(redirectProviderKey);
        }
    }
}
