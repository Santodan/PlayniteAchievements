using System;
using System.Threading;
using System.Windows.Threading;
using Playnite.SDK;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Services.Achievements;

namespace PlayniteAchievements.Views.ThemeIntegration.Base
{
    /// <summary>
    /// Loads per-game achievement data for view-item theme controls off the UI thread.
    /// Whole-library cache loads (projection warms, friends overview snapshots) can hold
    /// the cache lock for seconds, so a synchronous read on the UI thread stalls
    /// Playnite's main view once per visible grid item. A single gate serializes the
    /// reads (they serialize on the cache lock anyway), so a burst of visible items
    /// pins at most one thread-pool thread on that lock.
    /// </summary>
    internal static class ViewItemAchievementDataLoader
    {
        private static readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);

        /// <summary>
        /// Fetches visible achievement data for <paramref name="gameId"/> on a background
        /// thread and applies it via <paramref name="apply"/> on <paramref name="dispatcher"/>.
        /// <paramref name="isStale"/> is checked before the fetch and again before the apply
        /// so recycled containers drop superseded results; it must be safe to call from any
        /// thread.
        /// </summary>
        public static async void LoadAsync(
            AchievementDataService dataService,
            Guid gameId,
            Dispatcher dispatcher,
            Func<bool> isStale,
            Action<GameAchievementData> apply,
            ILogger logger)
        {
            if (dataService == null || dispatcher == null || apply == null)
            {
                return;
            }

            try
            {
                GameAchievementData gameData;
                await Gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (isStale?.Invoke() == true)
                    {
                        return;
                    }

                    gameData = dataService.GetVisibleGameAchievementData(gameId);
                }
                finally
                {
                    Gate.Release();
                }

                _ = dispatcher.BeginInvoke(
                    new Action(() =>
                    {
                        if (isStale?.Invoke() == true)
                        {
                            return;
                        }

                        apply(gameData);
                    }),
                    DispatcherPriority.Background);
            }
            catch (Exception ex)
            {
                logger?.Debug(ex, $"Failed to load achievement view-item data for game {gameId}.");
            }
        }
    }
}
