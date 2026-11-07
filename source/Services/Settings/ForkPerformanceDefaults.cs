using PlayniteAchievements.Providers.Local;

namespace PlayniteAchievements.Services.Settings
{
    internal static class ForkPerformanceDefaults
    {
        // Only controls owned by the fork Performance tab. Keep other Local settings intact.
        internal static void Restore(LocalSettings settings)
        {
            if (settings == null) return;
            var defaults = new LocalSettings();
            settings.UseStaticWpfArtwork = defaults.UseStaticWpfArtwork;
            settings.AnimationCacheBudgetMb = defaults.AnimationCacheBudgetMb;
            settings.ManualNotificationPreviews = defaults.ManualNotificationPreviews;
            settings.ReleaseNotificationPreviewsOnLeave = defaults.ReleaseNotificationPreviewsOnLeave;
            settings.ReduceForkNotificationEffects = defaults.ReduceForkNotificationEffects;
            settings.DecodedSoundCacheSeconds = defaults.DecodedSoundCacheSeconds;
            settings.DeferSteamCefCookieLoading = defaults.DeferSteamCefCookieLoading;
            settings.PreloadForkNotificationBrowser = defaults.PreloadForkNotificationBrowser;
            settings.BuildOverviewOnDemand = defaults.BuildOverviewOnDemand;
            settings.PreloadSteamUserMenu = defaults.PreloadSteamUserMenu;
            settings.WatchExternalCaptureChanges = defaults.WatchExternalCaptureChanges;
            settings.DeferScheduledRefreshesDuringGameplay = defaults.DeferScheduledRefreshesDuringGameplay;
            settings.UseCustomLocalPolling = defaults.UseCustomLocalPolling;
            settings.RestrictCustomPollingAuthentication = defaults.RestrictCustomPollingAuthentication;
            settings.CheckCustomPollingSourcesWhileWaiting = defaults.CheckCustomPollingSourcesWhileWaiting;
            settings.ReuseCustomPollingDiscoveryWithinPoll = defaults.ReuseCustomPollingDiscoveryWithinPoll;
            settings.UseLightweightCustomStartup = defaults.UseLightweightCustomStartup;
            settings.ReuseCustomPollingStartupBaseline = defaults.ReuseCustomPollingStartupBaseline;
            settings.UseCustomPollingDirectoryCache = defaults.UseCustomPollingDirectoryCache;
            settings.UseCustomPollingMetadataCache = defaults.UseCustomPollingMetadataCache;
            settings.SuppressCustomPollingOverviewWarm = defaults.SuppressCustomPollingOverviewWarm;
            settings.UseForkNotificationSummaryCache = defaults.UseForkNotificationSummaryCache;
            settings.UseForkThemeMigrationCache = defaults.UseForkThemeMigrationCache;
            settings.SampleProcessCpuThreads = defaults.SampleProcessCpuThreads;
        }
    }
}
