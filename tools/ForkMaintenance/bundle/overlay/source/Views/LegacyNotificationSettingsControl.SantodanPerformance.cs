using PlayniteAchievements.Providers.Local;

namespace PlayniteAchievements.Views
{
    public partial class LegacyNotificationSettingsControl
    {
        private LocalSettings SantodanPreviewPerformanceSettings =>
            _providerRegistry?.GetSettingsForEdit("Local") as LocalSettings;
        private bool ManualSantodanPreviewsEnabled =>
            SantodanPreviewPerformanceSettings?.ManualNotificationPreviews == true;

        private void ReleaseSantodanInlinePreviewOnLeave()
        {
            if (SantodanPreviewPerformanceSettings?.ReleaseNotificationPreviewsOnLeave == true &&
                NotificationsCustomInlinePreviewHost != null)
                NotificationsCustomInlinePreviewHost.Content = null;
        }
    }
}
