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
        public void ShouldHideFromSetupSurfaces_ReturnsFalse_ForFirstClassProviders(string providerKey)
        {
            Assert.IsFalse(ProviderUiPolicies.ShouldHideFromSetupSurfaces(providerKey));
        }

        [DataTestMethod]
        [DataRow("GooglePlay")]
        [DataRow("Apple")]
        [DataRow("EA")]
        [DataRow("Ubisoft")]
        public void ShouldHideFromSetupSurfaces_ReturnsTrue_ForProvidersWithoutSetupSurfaces(string providerKey)
        {
            Assert.IsTrue(ProviderUiPolicies.ShouldHideFromSetupSurfaces(providerKey));
        }
    }
}
