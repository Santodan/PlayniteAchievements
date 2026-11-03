using System.Threading;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Providers.Local;

namespace PlayniteAchievements.Services.Library
{
    internal sealed partial class LibraryProjectionService
    {
        private static bool CustomWarmSuppressionEnabled =>
            ProviderRegistry.Settings<LocalSettings>()?.UseCustomLocalPolling == true &&
            ProviderRegistry.Settings<LocalSettings>()?.SuppressCustomPollingOverviewWarm == true;

        // Called under _sync. The upstream generation/scheduling is untouched when disabled.
        private void CancelSantodanPendingWarm(bool active)
        {
            if (active && CustomWarmSuppressionEnabled)
                Interlocked.Increment(ref _warmGeneration);
        }

        private bool ShouldSkipSantodanWarm(int generation)
        {
            if (!CustomWarmSuppressionEnabled)
                return false;

            lock (_sync)
            {
                if (_disposed || _warmSuppressed || generation != Volatile.Read(ref _warmGeneration))
                    return true;
            }
            // The game, refresh, or visible overview may have changed during the delay.
            return _isRefreshActive?.Invoke() == true || _hasActiveSnapshotPublisher?.Invoke() == true;
        }
    }
}
