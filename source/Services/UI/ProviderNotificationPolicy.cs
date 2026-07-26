using PlayniteAchievements.Models.Settings;

namespace PlayniteAchievements.Services.UI
{
    /// <summary>
    /// Resolves the effective unlock-notification feature flags for one provider by combining
    /// the global notification settings with that provider's stored override (null = inherit).
    /// The master switches always win: per-provider values only modulate feature defaults.
    /// Recomputed per call; unlock events are rare so no caching is needed.
    /// </summary>
    internal static class ProviderNotificationPolicy
    {
        internal readonly struct EffectiveNotifications
        {
            public EffectiveNotifications(
                bool unlockToasts,
                bool friendUnlockToasts,
                bool screenshotClean,
                bool screenshotWithToast,
                bool screenshotFramed,
                bool recordings)
            {
                UnlockToasts = unlockToasts;
                FriendUnlockToasts = friendUnlockToasts;
                ScreenshotClean = screenshotClean;
                ScreenshotWithToast = screenshotWithToast;
                ScreenshotFramed = screenshotFramed;
                Recordings = recordings;
            }

            public bool UnlockToasts { get; }

            public bool FriendUnlockToasts { get; }

            public bool ScreenshotClean { get; }

            public bool ScreenshotWithToast { get; }

            public bool ScreenshotFramed { get; }

            public bool Recordings { get; }

            public bool AnyScreenshot => ScreenshotClean || ScreenshotWithToast || ScreenshotFramed;
        }

        /// <summary>
        /// The effective notification flags for a provider. A null or blank provider key (and a
        /// provider without a stored override) resolves to the globals; null settings resolve to
        /// all-false.
        /// </summary>
        public static EffectiveNotifications Resolve(PersistedSettings settings, string providerKey)
        {
            if (settings == null)
            {
                return default;
            }

            var overrides = settings.GetProviderNotificationOverride(providerKey);
            var toastsOn = settings.EnableNotifications;
            var screenshotsOn = settings.EnableUnlockScreenshots;

            return new EffectiveNotifications(
                unlockToasts: toastsOn && (overrides?.UnlockToasts ?? settings.EnableUnlockToasts),
                friendUnlockToasts: toastsOn && (overrides?.FriendUnlockToasts ?? settings.EnableFriendUnlockToasts),
                screenshotClean: screenshotsOn && (overrides?.ScreenshotClean ?? settings.UnlockScreenshotClean),
                screenshotWithToast: screenshotsOn && (overrides?.ScreenshotWithToast ?? settings.UnlockScreenshotWithToast),
                screenshotFramed: screenshotsOn && (overrides?.ScreenshotFramed ?? settings.UnlockScreenshotFramed),
                // Recordings resolve to only the per-provider filter (override ?? true): the
                // recording service ANDs its own EnableUnlockRecordings master enable, which is
                // introduced together with that service.
                recordings: overrides?.Recordings ?? true);
        }
    }
}
