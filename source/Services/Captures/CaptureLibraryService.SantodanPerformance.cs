using PlayniteAchievements.Providers;
using PlayniteAchievements.Providers.Local;

namespace PlayniteAchievements.Services.Captures
{
    internal sealed partial class CaptureLibraryService
    {
        private static bool ShouldWatchSantodanExternalCaptures() =>
            ProviderRegistry.Settings<LocalSettings>().WatchExternalCaptureChanges;
    }
}
