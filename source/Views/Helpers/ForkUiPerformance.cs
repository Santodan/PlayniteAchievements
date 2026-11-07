using PlayniteAchievements.Providers;
using PlayniteAchievements.Providers.Local;

namespace PlayniteAchievements.Views.Helpers
{
    internal static class ForkUiPerformance
    {
        internal static bool StaticArtwork => ProviderRegistry.Settings<LocalSettings>().UseStaticWpfArtwork;
        internal static long AnimationCacheBytes =>
            (long)ProviderRegistry.Settings<LocalSettings>().AnimationCacheBudgetMb * 1024 * 1024;
    }
}
