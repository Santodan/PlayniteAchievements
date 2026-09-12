using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Providers.Manual;

namespace PlayniteAchievements.Tests.Manual
{
    /// <summary>
    /// Covers when manual tracking is offered. The rule decides whether a user can replace a
    /// provider's achievement data, so each branch is pinned rather than left to the two surfaces
    /// that read it.
    /// </summary>
    [TestClass]
    public class ManualTrackingAvailabilityTests
    {
        [TestMethod]
        public void AnExistingLink_IsAlwaysOffered()
        {
            // Otherwise a linked game could become unreachable for inspection or unlinking.
            Assert.IsTrue(ManualTrackingAvailability.CanLink(
                hasManualLink: true,
                trackingOverrideEnabled: false,
                isExcluded: true,
                hasCachedAchievements: true,
                hasNonManualProviderData: true));
        }

        [TestMethod]
        public void TheOverrideSetting_OffersItUnconditionally()
        {
            // This is the deliberate "replace my provider's data" path.
            Assert.IsTrue(ManualTrackingAvailability.CanLink(
                hasManualLink: false,
                trackingOverrideEnabled: true,
                isExcluded: true,
                hasCachedAchievements: true,
                hasNonManualProviderData: true));
        }

        [TestMethod]
        public void AGameWithNothingCached_IsOffered()
        {
            Assert.IsTrue(ManualTrackingAvailability.CanLink(
                hasManualLink: false,
                trackingOverrideEnabled: false,
                isExcluded: false,
                hasCachedAchievements: false,
                hasNonManualProviderData: false));
        }

        [TestMethod]
        public void AGameBackedByAnotherProvider_IsNotOffered()
        {
            // Without the override setting, linking must not be able to displace data the user
            // never asked to replace.
            Assert.IsFalse(ManualTrackingAvailability.CanLink(
                hasManualLink: false,
                trackingOverrideEnabled: false,
                isExcluded: false,
                hasCachedAchievements: true,
                hasNonManualProviderData: true));
        }

        [TestMethod]
        public void AnExcludedGame_IsNotOffered()
        {
            Assert.IsFalse(ManualTrackingAvailability.CanLink(
                hasManualLink: false,
                trackingOverrideEnabled: false,
                isExcluded: true,
                hasCachedAchievements: false,
                hasNonManualProviderData: false));
        }
    }
}
