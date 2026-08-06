using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.UI;

namespace PlayniteAchievements.Tests.Services.UI
{
    [TestClass]
    public class UnlockScreenshotVariantPolicyTests
    {
        private static PersistedSettings MakeSettings(
            bool enableNotifications = true,
            bool enableUnlockScreenshots = true,
            bool clean = true,
            bool withToast = true,
            bool framed = true,
            RaritySelection cleanRarities = RaritySelection.All,
            RaritySelection withToastRarities = RaritySelection.All,
            RaritySelection framedRarities = RaritySelection.All,
            bool cleanAlwaysCompletion = false,
            bool withToastAlwaysCompletion = false,
            bool framedAlwaysCompletion = false)
        {
            return new PersistedSettings
            {
                EnableNotifications = enableNotifications,
                EnableUnlockScreenshots = enableUnlockScreenshots,
                UnlockScreenshotClean = clean,
                UnlockScreenshotWithToast = withToast,
                UnlockScreenshotFramed = framed,
                UnlockScreenshotCleanRarities = cleanRarities,
                UnlockScreenshotWithToastRarities = withToastRarities,
                UnlockScreenshotFramedRarities = framedRarities,
                UnlockScreenshotCleanAlwaysCaptureCompletion = cleanAlwaysCompletion,
                UnlockScreenshotWithToastAlwaysCaptureCompletion = withToastAlwaysCompletion,
                UnlockScreenshotFramedAlwaysCaptureCompletion = framedAlwaysCompletion
            };
        }

        private static ScreenshotVariants Resolve(
            PersistedSettings settings,
            RarityTier rarity = RarityTier.Common,
            bool isCompletion = false,
            string providerKey = "Steam")
        {
            return UnlockScreenshotVariantPolicy.Resolve(rarity, isCompletion, providerKey, settings);
        }

        [TestMethod]
        public void Resolve_NullSettings_None()
        {
            Assert.AreEqual(ScreenshotVariants.None, Resolve(null));
        }

        /// <summary>
        /// The invariant this whole policy exists to hold: the with-notification variant is
        /// produced whether or not a notification is shown, because its card is rendered from a
        /// headless toast window rather than read off the screen.
        /// </summary>
        [TestMethod]
        public void Resolve_NotificationsOff_StillProducesWithToastVariant()
        {
            var settings = MakeSettings(enableNotifications: false);

            var variants = Resolve(settings);

            Assert.IsTrue(variants.HasFlag(ScreenshotVariants.WithToast));
            Assert.AreEqual(
                ScreenshotVariants.Clean | ScreenshotVariants.WithToast | ScreenshotVariants.Framed,
                variants);
        }

        [TestMethod]
        public void Resolve_NotificationsOffAndProviderToastsOff_StillProducesWithToastVariant()
        {
            var settings = MakeSettings(enableNotifications: false, clean: false, framed: false);
            settings.SetProviderNotificationOverride("Steam", new ProviderNotificationOverride
            {
                UnlockToasts = false,
                FriendUnlockToasts = false
            });

            Assert.AreEqual(ScreenshotVariants.WithToast, Resolve(settings));
        }

        [TestMethod]
        public void Resolve_ScreenshotsMasterOff_None()
        {
            var settings = MakeSettings(enableUnlockScreenshots: false);

            Assert.AreEqual(ScreenshotVariants.None, Resolve(settings));
        }

        [TestMethod]
        public void Resolve_PerVariantRaritiesAreIndependent()
        {
            // Only the with-notification variant admits Rare; the other two are Common-only.
            var settings = MakeSettings(
                cleanRarities: RaritySelection.Common,
                withToastRarities: RaritySelection.Rare,
                framedRarities: RaritySelection.Common);

            Assert.AreEqual(ScreenshotVariants.WithToast, Resolve(settings, RarityTier.Rare));
            Assert.AreEqual(
                ScreenshotVariants.Clean | ScreenshotVariants.Framed,
                Resolve(settings, RarityTier.Common));
        }

        [TestMethod]
        public void Resolve_CompletionBypassIsPerVariant()
        {
            // The unlock's rarity is excluded everywhere; only the framed variant bypasses on
            // completion.
            var settings = MakeSettings(
                cleanRarities: RaritySelection.Common,
                withToastRarities: RaritySelection.Common,
                framedRarities: RaritySelection.Common,
                framedAlwaysCompletion: true);

            Assert.AreEqual(
                ScreenshotVariants.Framed,
                Resolve(settings, RarityTier.UltraRare, isCompletion: true));
            Assert.AreEqual(
                ScreenshotVariants.None,
                Resolve(settings, RarityTier.UltraRare, isCompletion: false));
        }

        [TestMethod]
        public void Resolve_ProviderOverrideWinsPerVariant()
        {
            var settings = MakeSettings(clean: true, withToast: false, framed: true);
            settings.SetProviderNotificationOverride("Steam", new ProviderNotificationOverride
            {
                ScreenshotClean = false,
                ScreenshotWithToast = true
            });

            // Clean forced off, with-notification forced on, framed inherits the global true.
            Assert.AreEqual(
                ScreenshotVariants.WithToast | ScreenshotVariants.Framed,
                Resolve(settings));
        }

        [TestMethod]
        public void Resolve_ProviderOverrideDoesNotLeakToOtherProviders()
        {
            var settings = MakeSettings(clean: false, withToast: false, framed: false);
            settings.SetProviderNotificationOverride("Steam", new ProviderNotificationOverride
            {
                ScreenshotClean = true
            });

            Assert.AreEqual(ScreenshotVariants.Clean, Resolve(settings, providerKey: "Steam"));
            Assert.AreEqual(ScreenshotVariants.None, Resolve(settings, providerKey: "GOG"));
        }
    }
}
