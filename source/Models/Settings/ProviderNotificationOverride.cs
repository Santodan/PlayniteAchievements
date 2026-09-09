using Newtonsoft.Json;

namespace PlayniteAchievements.Models.Settings
{
    /// <summary>
    /// Per-provider overrides for unlock notification features. Each value is nullable;
    /// null means the provider inherits the corresponding global default. Only providers
    /// that deviate from the globals are stored in settings.
    /// </summary>
    public sealed class ProviderNotificationOverride
    {
        public bool? UnlockToasts { get; set; }

        public bool? FriendUnlockToasts { get; set; }

        /// <summary>Incremental-progress notifications (silent, capture-free).</summary>
        public bool? ProgressToasts { get; set; }

        public bool? ScreenshotClean { get; set; }

        public bool? ScreenshotWithToast { get; set; }

        public bool? ScreenshotFramed { get; set; }

        public bool? Recordings { get; set; }

        [JsonIgnore]
        public bool IsAllInherit =>
            UnlockToasts == null &&
            FriendUnlockToasts == null &&
            ProgressToasts == null &&
            ScreenshotClean == null &&
            ScreenshotWithToast == null &&
            ScreenshotFramed == null &&
            Recordings == null;

        public ProviderNotificationOverride Clone()
        {
            return new ProviderNotificationOverride
            {
                UnlockToasts = UnlockToasts,
                FriendUnlockToasts = FriendUnlockToasts,
                ProgressToasts = ProgressToasts,
                ScreenshotClean = ScreenshotClean,
                ScreenshotWithToast = ScreenshotWithToast,
                ScreenshotFramed = ScreenshotFramed,
                Recordings = Recordings
            };
        }
    }
}
