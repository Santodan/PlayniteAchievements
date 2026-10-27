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
        public void Resolve_ProgressToasts_FollowGlobalUnlessOverriddenAndDieWithTheMaster()
        {
            var settings = MakeSettings();
            Assert.IsTrue(
                ProviderNotificationPolicy.Resolve(settings, "Steam").ProgressToasts,
                "Progress notifications default on.");

            settings.EnableProgressToasts = false;
            Assert.IsFalse(ProviderNotificationPolicy.Resolve(settings, "Steam").ProgressToasts);

            settings.SetProviderNotificationOverride(
                "Steam",
                new ProviderNotificationOverride { ProgressToasts = true });
            Assert.IsTrue(ProviderNotificationPolicy.Resolve(settings, "Steam").ProgressToasts);
            Assert.IsFalse(
                ProviderNotificationPolicy.Resolve(settings, "Epic").ProgressToasts,
                "Another provider keeps inheriting the global.");

            settings.EnableNotifications = false;
            Assert.IsFalse(
                ProviderNotificationPolicy.Resolve(settings, "Steam").ProgressToasts,
                "The notifications master switch wins over a per-provider On.");
            Assert.IsFalse(ProviderNotificationPolicy.Resolve(null, "Steam").ProgressToasts);
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
            // Load-bearing: screenshots have their own master switch, and the with-notification
            // variant survives notifications being off because its card is rendered headlessly
            // rather than read off the screen.
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

        [TestMethod]
        public void Resolve_GameWithoutOverrides_MatchesProviderResolution()
        {
            var settings = MakeSettings(enableUnlockToasts: true, screenshotClean: true);
            settings.NotificationDelaySeconds = 8;
            settings.SetProviderNotificationOverride(
                "Steam",
                new ProviderNotificationOverride { UnlockToasts = false, RecordingClean = true });

            var withGame = ProviderNotificationPolicy.Resolve(settings, "Steam", new GameCustomDataFile());
            var without = ProviderNotificationPolicy.Resolve(settings, "Steam");

            Assert.AreEqual(without.UnlockToasts, withGame.UnlockToasts);
            Assert.IsFalse(withGame.UnlockToasts);
            Assert.AreEqual(without.ScreenshotClean, withGame.ScreenshotClean);
            Assert.AreEqual(without.RecordingClean, withGame.RecordingClean);
            Assert.IsTrue(withGame.RecordingClean);
            Assert.AreEqual(8, withGame.Behavior.NotificationDelaySeconds);
        }

        [TestMethod]
        public void Resolve_GameBehaviorOverride_ReplacesProviderAndGlobalMaster()
        {
            var settings = MakeSettings(enableNotifications: false, enableUnlockToasts: false);
            settings.SetProviderNotificationOverride(
                "Steam",
                new ProviderNotificationOverride { UnlockToasts = false });
            var gameData = new GameCustomDataFile
            {
                NotificationBehaviorOverride = new GameNotificationBehaviorOverride
                {
                    EnableNotifications = true,
                    EnableUnlockToasts = true,
                    NotificationDelaySeconds = 3
                }
            };

            var effective = ProviderNotificationPolicy.Resolve(settings, "Steam", gameData);

            Assert.IsTrue(effective.UnlockToasts);
            Assert.IsFalse(effective.FriendUnlockToasts);
            Assert.AreEqual(3, effective.Behavior.NotificationDelaySeconds);
            // Captures still follow the globals.
            Assert.IsFalse(effective.ScreenshotClean);
            Assert.IsTrue(effective.ScreenshotWithToast);
        }

        [TestMethod]
        public void Resolve_GameBehaviorOverride_CanTurnToastsOff()
        {
            var settings = MakeSettings();
            var gameData = new GameCustomDataFile
            {
                NotificationBehaviorOverride = new GameNotificationBehaviorOverride
                {
                    EnableNotifications = true,
                    EnableUnlockToasts = false
                }
            };

            Assert.IsFalse(ProviderNotificationPolicy.Resolve(settings, "Steam", gameData).UnlockToasts);
            Assert.IsTrue(ProviderNotificationPolicy.Resolve(settings, "Steam").UnlockToasts);
        }

        [TestMethod]
        public void Resolve_GameCaptureOverride_EnablesRecordingsWhileGlobalIsOff()
        {
            var settings = MakeSettings(enableUnlockScreenshots: false);
            settings.EnableUnlockRecordings = false;
            settings.SetProviderNotificationOverride(
                "Steam",
                new ProviderNotificationOverride { Recordings = false });
            var gameData = new GameCustomDataFile
            {
                CaptureOverride = new GameCaptureOverride
                {
                    EnableUnlockScreenshots = true,
                    UnlockScreenshotFramed = true,
                    EnableUnlockRecordings = true,
                    UnlockRecordingClean = true
                }
            };

            var effective = ProviderNotificationPolicy.Resolve(settings, "Steam", gameData);

            Assert.IsTrue(effective.RecordingsEnabled);
            Assert.IsTrue(effective.Recordings, "the provider's all-clips switch does not apply to a game with its own captures");
            Assert.IsTrue(effective.RecordingClean);
            Assert.IsFalse(effective.RecordingWithToast);
            Assert.IsTrue(effective.ScreenshotFramed);
            Assert.IsFalse(effective.ScreenshotWithToast);
            Assert.IsFalse(ProviderNotificationPolicy.Resolve(settings, "Steam").RecordingsEnabled);
        }

        [TestMethod]
        public void CrossesProgressInterval_ZeroPercent_EveryAdvance()
        {
            Assert.IsTrue(ProviderNotificationPolicy.CrossesProgressInterval(3, 4, 1000, 0));
            Assert.IsFalse(ProviderNotificationPolicy.CrossesProgressInterval(4, 4, 1000, 0));
        }

        [TestMethod]
        public void CrossesProgressInterval_FiresOnlyAtStepBoundaries()
        {
            Assert.IsFalse(ProviderNotificationPolicy.CrossesProgressInterval(0, 99, 1000, 10));
            Assert.IsTrue(ProviderNotificationPolicy.CrossesProgressInterval(99, 100, 1000, 10));
            Assert.IsFalse(ProviderNotificationPolicy.CrossesProgressInterval(100, 199, 1000, 10));
        }

        [TestMethod]
        public void CrossesProgressInterval_JumpOverSeveralSteps_Fires()
        {
            Assert.IsTrue(ProviderNotificationPolicy.CrossesProgressInterval(95, 250, 1000, 10));
        }

        [TestMethod]
        public void CrossesProgressInterval_StepSmallerThanOneUnit_FiresEveryUnit()
        {
            Assert.IsTrue(ProviderNotificationPolicy.CrossesProgressInterval(0, 1, 5, 10));
            Assert.IsTrue(ProviderNotificationPolicy.CrossesProgressInterval(1, 2, 5, 10));
        }

        [TestMethod]
        public void Resolve_GameBehavior_OwnIntervalReplacesGlobal()
        {
            var settings = MakeSettings();
            settings.ProgressToastIntervalPercent = 25;
            Assert.AreEqual(25, ProviderNotificationPolicy.Resolve(settings, "Steam").ProgressToastIntervalPercent);

            var gameBehavior = GameNotificationBehaviorOverride.FromEffective(settings, null).Clone();
            Assert.AreEqual(25, gameBehavior.ProgressToastIntervalPercent);

            gameBehavior.ProgressToastIntervalPercent = 0;
            var gameData = new GameCustomDataFile { NotificationBehaviorOverride = gameBehavior };
            Assert.AreEqual(0, ProviderNotificationPolicy.Resolve(settings, "Steam", gameData).ProgressToastIntervalPercent);
        }

        [TestMethod]
        public void ProgressToastIntervalPercent_ClampsToRange()
        {
            var settings = new PersistedSettings { ProgressToastIntervalPercent = 150 };
            Assert.AreEqual(99, settings.ProgressToastIntervalPercent);
            settings.ProgressToastIntervalPercent = -5;
            Assert.AreEqual(0, settings.ProgressToastIntervalPercent);
        }
    }
}
