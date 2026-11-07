using PlayniteAchievements.Providers;
using PlayniteAchievements.Providers.Local;

namespace PlayniteAchievements.Services
{
    public partial class NotificationPublisher
    {
        private static bool ShouldPreloadSantodanNotificationBrowser() =>
            ProviderRegistry.Settings<LocalSettings>().PreloadForkNotificationBrowser;
    }
}
