using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using PlayniteAchievements.Providers.Local;

namespace PlayniteAchievements.Tests.Models
{
    [TestClass]
    public class LocalBrowserPerformanceSettingsTests
    {
        [TestMethod]
        public void RestorePerformanceDefaultsPreservesUnrelatedSettings()
        {
            var settings = new LocalSettings
            {
                UseStaticWpfArtwork = true,
                AnimationCacheBudgetMb = 8,
                DecodedSoundCacheSeconds = 0,
                PreloadForkNotificationBrowser = false,
                WatchExternalCaptureChanges = false,
                UseCustomLocalPolling = true,
                EnablePollingDebugLogging = true,
                UnlockOverlayDurationMilliseconds = 8000
            };
            PlayniteAchievements.Services.Settings.ForkPerformanceDefaults.Restore(settings);
            Assert.IsFalse(settings.UseStaticWpfArtwork);
            Assert.AreEqual(128, settings.AnimationCacheBudgetMb);
            Assert.AreEqual(60, settings.DecodedSoundCacheSeconds);
            Assert.IsTrue(settings.PreloadForkNotificationBrowser);
            Assert.IsTrue(settings.WatchExternalCaptureChanges);
            Assert.IsFalse(settings.UseCustomLocalPolling);
            Assert.IsTrue(settings.EnablePollingDebugLogging);
            Assert.AreEqual(8000, settings.UnlockOverlayDurationMilliseconds);
        }

        [TestMethod]
        public void ExperimentalUiOptionsKeepExistingDefaultsAndClampMemoryBudgets()
        {
            var old = JsonConvert.DeserializeObject<LocalSettings>("{}");
            Assert.IsFalse(old.UseStaticWpfArtwork);
            Assert.IsFalse(old.ManualNotificationPreviews);
            Assert.IsFalse(old.ReleaseNotificationPreviewsOnLeave);
            Assert.IsFalse(old.ReduceForkNotificationEffects);
            Assert.AreEqual(128, old.AnimationCacheBudgetMb);
            Assert.AreEqual(60, old.DecodedSoundCacheSeconds);
            var limited = JsonConvert.DeserializeObject<LocalSettings>(
                "{\"AnimationCacheBudgetMb\":-1,\"DecodedSoundCacheSeconds\":999}");
            Assert.AreEqual(8, limited.AnimationCacheBudgetMb);
            Assert.AreEqual(60, limited.DecodedSoundCacheSeconds);
            limited.AnimationCacheBudgetMb = int.MaxValue;
            limited.DecodedSoundCacheSeconds = -1;
            Assert.AreEqual(128, limited.AnimationCacheBudgetMb);
            Assert.AreEqual(0, limited.DecodedSoundCacheSeconds);
            limited.UseStaticWpfArtwork = true;
            limited.ManualNotificationPreviews = true;
            limited.ReleaseNotificationPreviewsOnLeave = true;
            limited.ReduceForkNotificationEffects = true;
            var copy = JsonConvert.DeserializeObject<LocalSettings>(JsonConvert.SerializeObject(limited));
            Assert.IsTrue(copy.UseStaticWpfArtwork);
            Assert.IsTrue(copy.ManualNotificationPreviews);
            Assert.IsTrue(copy.ReleaseNotificationPreviewsOnLeave);
            Assert.IsTrue(copy.ReduceForkNotificationEffects);
            Assert.AreEqual(0, copy.DecodedSoundCacheSeconds);
        }

        [TestMethod]
        public void BackgroundOptionsPreserveOldDefaultsAndRoundTripIndependently()
        {
            var settings = JsonConvert.DeserializeObject<LocalSettings>("{}");
            Assert.IsFalse(settings.BuildOverviewOnDemand);
            Assert.IsTrue(settings.PreloadSteamUserMenu);
            Assert.IsTrue(settings.WatchExternalCaptureChanges);
            Assert.IsFalse(settings.DeferScheduledRefreshesDuringGameplay);
            settings.BuildOverviewOnDemand = true;
            settings.PreloadSteamUserMenu = false;
            settings.WatchExternalCaptureChanges = false;
            settings.DeferScheduledRefreshesDuringGameplay = true;
            var copy = JsonConvert.DeserializeObject<LocalSettings>(JsonConvert.SerializeObject(settings));
            Assert.IsTrue(copy.BuildOverviewOnDemand);
            Assert.IsFalse(copy.PreloadSteamUserMenu);
            Assert.IsFalse(copy.WatchExternalCaptureChanges);
            Assert.IsTrue(copy.DeferScheduledRefreshesDuringGameplay);
            Assert.IsFalse(copy.UseCustomLocalPolling);
        }

        [TestMethod]
        public void ExistingSettingsWithoutBrowserOptionsKeepOriginalStartupBehavior()
        {
            var restored = JsonConvert.DeserializeObject<LocalSettings>("{\"UseCustomLocalPolling\":true}");
            Assert.IsFalse(restored.DeferSteamCefCookieLoading);
            Assert.IsTrue(restored.PreloadForkNotificationBrowser);
        }

        [TestMethod]
        public void BrowserChoicesSurviveSettingsCopiesWithoutEnablingCustomPolling()
        {
            var settings = new LocalSettings
            {
                DeferSteamCefCookieLoading = true,
                PreloadForkNotificationBrowser = false
            };
            var copy = JsonConvert.DeserializeObject<LocalSettings>(JsonConvert.SerializeObject(settings));
            Assert.IsTrue(copy.DeferSteamCefCookieLoading);
            Assert.IsFalse(copy.PreloadForkNotificationBrowser);
            Assert.IsFalse(copy.UseCustomLocalPolling);
            copy.PreloadForkNotificationBrowser = true;
            copy.DeferSteamCefCookieLoading = false;
            Assert.IsFalse(settings.PreloadForkNotificationBrowser);
            Assert.IsTrue(settings.DeferSteamCefCookieLoading);
        }
    }
}
