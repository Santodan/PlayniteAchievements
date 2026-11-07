using PlayniteAchievements.Providers;
using PlayniteAchievements.Providers.Local;

namespace PlayniteAchievements
{
    public partial class PlayniteAchievementsPlugin
    {
        private static bool ShouldPreloadSantodanSteamUserMenu() =>
            PlayniteAchievements.Providers.ProviderRegistry.Settings<LocalSettings>().PreloadSteamUserMenu;
    }
}
