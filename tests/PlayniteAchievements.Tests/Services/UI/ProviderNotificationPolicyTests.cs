using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.UI;

namespace PlayniteAchievements.Tests.Services.UI
{
    [TestClass]
    public class ProviderNotificationPolicyTests
    {
        private static PersistedSettings MakeSettings(
            bool enableNotifications = true,
            bool enableUnlockToasts = true,
            bool enableFriendUnlockToasts = true,
            bool enableUnlockScreenshots = true,
            bool screenshotClean = false,
            bool screenshotWithToast = true,
            bool screenshotFramed = false)
        {
            return new PersistedSettings
            {
                EnableNotifications = enableNotifications,
                EnableUnlockToasts = enableUnlockToasts,
                EnableFriendUnlockToasts = enableFriendUnlockToasts,
                EnableUnlockScreenshots = enableUnlockScreenshots,
                UnlockScreenshotClean = screenshotClean,
                UnlockScreenshotWithToast = screenshotWithToast,
                UnlockScreenshotFramed = screenshotFramed
            };
        }

        [TestMethod]
        public void Resolve_NullSettings_AllFalse()
        {
            var effective = ProviderNotificationPolicy.Resolve(null, "Steam");

            Assert.IsFalse(effective.UnlockToasts);
            Assert.IsFalse(effective.FriendUnlockToasts);
            Assert.IsFalse(effective.ScreenshotClean);
            Assert.IsFalse(effective.ScreenshotWithToast);
            Assert.IsFalse(effective.ScreenshotFramed);
            Assert.IsFalse(effective.Recordings);
            Assert.IsFalse(effective.AnyScreenshot);
        }

        [TestMethod]
        public void Resolve_NoOverride_FollowsGlobals()
        {
            var settings = MakeSettings(
                enableUnlockToasts: true,
                enableFriendUnlockToasts: false,
                screenshotClean: true,
                screenshotWithToast: false,
                screenshotFramed: false);

            var effective = ProviderNotificationPolicy.Resolve(settings, "Steam");

            Assert.IsTrue(effective.UnlockToasts);
            Assert.IsFalse(effective.FriendUnlockToasts);
            Assert.IsTrue(effective.ScreenshotClean);
            Assert.IsFalse(effective.ScreenshotWithToast);
            Assert.IsFalse(effective.ScreenshotFramed);
            Assert.IsTrue(effective.Recordings);
            Assert.IsTrue(effective.AnyScreenshot);
        }

        [TestMethod]
        public void Resolve_NullOrBlankProviderKey_FollowsGlobals()
        {
            var settings = MakeSettings(enableUnlockToasts: true);
            settings.SetProviderNotificationOverride(
                "Steam",
                new ProviderNotificationOverride { UnlockToasts = false });

            Assert.IsTrue(ProviderNotificationPolicy.Resolve(settings, null).UnlockToasts);
            Assert.IsTrue(ProviderNotificationPolicy.Resolve(settings, "   ").UnlockToasts);
        }

        [TestMethod]
        public void Resolve_UnknownProviderKey_FollowsGlobals()
        {
            var settings = MakeSettings(enableUnlockToasts: false, screenshotWithToast: true);
            settings.SetProviderNotificationOverride(
                "Steam",
                new ProviderNotificationOverride { UnlockToasts = true, ScreenshotWithToast = false });

            var effective = ProviderNotificationPolicy.Resolve(settings, "Epic");

            Assert.IsFalse(effective.UnlockToasts);
            Assert.IsTrue(effective.ScreenshotWithToast);
        }

        [TestMethod]
        public void Resolve_OverrideWinsOverFeatureGlobalPerFeature()
        {
            var settings = MakeSettings(
                enableUnlockToasts: false,
                enableFriendUnlockToasts: true,
                screenshotClean: false,
                screenshotWithToast: true,
                screenshotFramed: false);
            settings.SetProviderNotificationOverride("Steam", new ProviderNotificationOverride
            {
                UnlockToasts = true,
                FriendUnlockToasts = false,
                ScreenshotClean = true,
                ScreenshotWithToast = false,
                ScreenshotFramed = true,
                Recordings = false
            });

            var effective = ProviderNotificationPolicy.Resolve(settings, "Steam");

            Assert.IsTrue(effective.UnlockToasts);
            Assert.IsFalse(effective.FriendUnlockToasts);
            Assert.IsTrue(effective.ScreenshotClean);
            Assert.IsFalse(effective.ScreenshotWithToast);
            Assert.IsTrue(effective.ScreenshotFramed);
            Assert.IsFalse(effective.Recordings);
        }

        [TestMethod]
        public void Resolve_PartialOverride_UnsetFeaturesInheritGlobals()
        {
            var settings = MakeSettings(
                enableUnlockToasts: true,
                enableFriendUnlockToasts: true,
                screenshotWithToast: true);
            settings.SetProviderNotificationOverride(
                "Steam",
                new ProviderNotificationOverride { UnlockToasts = false });

            var effective = ProviderNotificationPolicy.Resolve(settings, "Steam");

            Assert.IsFalse(effective.UnlockToasts);
            Assert.IsTrue(effective.FriendUnlockToasts);
            Assert.IsTrue(effective.ScreenshotWithToast);
            Assert.IsTrue(effective.Recordings);
        }

        [TestMethod]
        public void Resolve_NotificationsMasterOff_KillsBothToastFeaturesDespiteOverrideOn()
        {
            var settings = MakeSettings(
                enableNotifications: false,
                enableUnlockToasts: true,
                enableFriendUnlockToasts: true);
            settings.SetProviderNotificationOverride("Steam", new ProviderNotificationOverride
            {
                UnlockToasts = true,
                FriendUnlockToasts = true
            });

            var effective = ProviderNotificationPolicy.Resolve(settings, "Steam");

            Assert.IsFalse(effective.UnlockToasts);
            Assert.IsFalse(effective.FriendUnlockToasts);
            // Screenshots have their own master switch and are unaffected by EnableNotifications.
            Assert.IsTrue(effective.ScreenshotWithToast);
        }

        [TestMethod]
        public void Resolve_ScreenshotsMasterOff_KillsAllVariantsDespiteOverrideOn()
        {
            var settings = MakeSettings(
                enableUnlockScreenshots: false,
                screenshotClean: true,
                screenshotWithToast: true,
                screenshotFramed: true);
            settings.SetProviderNotificationOverride("Steam", new ProviderNotificationOverride
            {
                ScreenshotClean = true,
                ScreenshotWithToast = true,
                ScreenshotFramed = true
            });

            var effective = ProviderNotificationPolicy.Resolve(settings, "Steam");

            Assert.IsFalse(effective.ScreenshotClean);
            Assert.IsFalse(effective.ScreenshotWithToast);
            Assert.IsFalse(effective.ScreenshotFramed);
            Assert.IsFalse(effective.AnyScreenshot);
            // Toasts are unaffected by the screenshot master switch.
            Assert.IsTrue(effective.UnlockToasts);
        }

        [TestMethod]
        public void Resolve_ProviderKeyMatchIsCaseInsensitive()
        {
            var settings = MakeSettings(enableUnlockToasts: true);
            settings.SetProviderNotificationOverride(
                "Steam",
                new ProviderNotificationOverride { UnlockToasts = false });

            Assert.IsFalse(ProviderNotificationPolicy.Resolve(settings, "STEAM").UnlockToasts);
            Assert.IsFalse(ProviderNotificationPolicy.Resolve(settings, "steam").UnlockToasts);
        }

        [TestMethod]
        public void Resolve_RecordingsDefaultTrueAndOverridable()
        {
            var settings = MakeSettings();
            Assert.IsTrue(ProviderNotificationPolicy.Resolve(settings, "Steam").Recordings);

            settings.SetProviderNotificationOverride(
                "Steam",
                new ProviderNotificationOverride { Recordings = false });
            Assert.IsFalse(ProviderNotificationPolicy.Resolve(settings, "Steam").Recordings);
        }
    }
}
