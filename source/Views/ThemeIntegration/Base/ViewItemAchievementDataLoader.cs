using System;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Playnite.SDK;
using Playnite.SDK.Models;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.Cache;
using PlayniteAchievements.Services.GameCustomData;

namespace PlayniteAchievements.Views.ThemeIntegration.Base
{
    /// <summary>
    /// Loads per-game achievement data for view-item theme controls off the UI thread.
    /// Whole-library cache loads (projection warms, friends overview snapshots) can hold
    /// the cache lock for seconds, so a synchronous read on the UI thread stalls
    /// Playnite's main view once per visible grid item. A single gate serializes the
    /// reads (they serialize on the cache lock anyway), so a burst of visible items
    /// pins at most one thread-pool thread on that lock.
    /// An instance binds one host control: it follows the host's DataContext, game context
    /// and load state, re-reads on cache changes for the host's game, and writes the
    /// unlocked count, achievement count and has-data properties the host registers.
    /// </summary>
    internal sealed class ViewItemAchievementDataLoader
    {
        private static readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);

        private readonly FrameworkElement _host;
        private readonly DependencyProperty _unlockedCountProperty;
        private readonly DependencyProperty _achievementCountProperty;
        private readonly DependencyProperty _hasDataProperty;
        private readonly ILogger _logger;
        private readonly Action _onApplied;
        private readonly bool _traceQueuedRefresh;
        private bool _isCacheEventSubscribed;
        private bool _cacheRefreshQueued;

        // Game id of the current DataContext, written on the UI thread and read on cache-event
        // threads so per-game events can be filtered without a dispatcher round-trip per event.
        private volatile string _currentGameIdText;

        // Game whose data was last requested. DataContextChanged, GameContextChanged and Loaded
        // each name the game; it is read once until a cache change or Unloaded clears this.
        private string _requestedGameIdText;

        /// <summary>
        /// Binds <paramref name="host"/>; call after InitializeComponent so the host's own
        /// handlers keep their order. <paramref name="onApplied"/> runs after loaded data is
        /// written, and <paramref name="traceQueuedRefresh"/> logs each queued refresh.
        /// </summary>
        public ViewItemAchievementDataLoader(
            FrameworkElement host,
            DependencyProperty unlockedCountProperty,
            DependencyProperty achievementCountProperty,
            DependencyProperty hasDataProperty,
            ILogger logger,
            Action onApplied = null,
            bool traceQueuedRefresh = false)
        {
            _host = host;
            _unlockedCountProperty = unlockedCountProperty;
            _achievementCountProperty = achievementCountProperty;
            _hasDataProperty = hasDataProperty;
            _logger = logger;
            _onApplied = onApplied;
            _traceQueuedRefresh = traceQueuedRefresh;

            host.DataContextChanged += (sender, e) => TryUpdateFromDataContext();
            host.Loaded += OnLoaded;
            host.Unloaded += OnUnloaded;
        }

        private static PlayniteAchievementsPlugin Plugin => PlayniteAchievementsPlugin.Instance;

        /// <summary>
        /// Uses the game context when it names a game, otherwise falls back to the DataContext.
        /// </summary>
        public void GameContextChanged(Game newContext)
        {
            if (newContext != null && newContext.Id != Guid.Empty)
            {
                _currentGameIdText = newContext.Id.ToString();
                UpdateForGame(newContext.Id);
            }
            else
            {
                TryUpdateFromDataContext();
            }
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            SubscribeToCacheEvents();
            TryUpdateFromDataContext();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            UnsubscribeFromCacheEvents();
            _cacheRefreshQueued = false;
            _requestedGameIdText = null;
        }

        private void SubscribeToCacheEvents()
        {
            if (_isCacheEventSubscribed)
            {
                return;
            }

            var service = Plugin?.RefreshRuntime;
            if (service == null)
            {
                return;
            }

            service.CacheInvalidated -= RefreshService_CacheInvalidated;
            service.CacheInvalidated += RefreshService_CacheInvalidated;
            service.GameCacheUpdated -= RefreshService_GameCacheUpdated;
            service.GameCacheUpdated += RefreshService_GameCacheUpdated;

            var customDataStore = Plugin?.GameCustomDataStore;
            if (customDataStore != null)
            {
                customDataStore.CustomDataChanged -= GameCustomDataStore_CustomDataChanged;
                customDataStore.CustomDataChanged += GameCustomDataStore_CustomDataChanged;
            }

            _isCacheEventSubscribed = true;
        }

        private void UnsubscribeFromCacheEvents()
        {
            if (!_isCacheEventSubscribed)
            {
                return;
            }

            try
            {
                Plugin?.RefreshRuntime.CacheInvalidated -= RefreshService_CacheInvalidated;
                Plugin?.RefreshRuntime.GameCacheUpdated -= RefreshService_GameCacheUpdated;
                if (Plugin?.GameCustomDataStore != null)
                {
                    Plugin.GameCustomDataStore.CustomDataChanged -= GameCustomDataStore_CustomDataChanged;
                }
            }
            catch
            {
            }

            _isCacheEventSubscribed = false;
        }

        private void RefreshService_CacheInvalidated(object sender, CacheInvalidatedEventArgs e)
        {
            // Scoped invalidations name the changed games; skip on the event thread when none
            // of them is this control's game (mirrors the GameCacheUpdated filter). Unscoped
            // invalidations still refresh unconditionally.
            if (e != null && !e.IsFull &&
                !e.ChangedGameIds.Any(gameId => IsCurrentGame(gameId.ToString())))
            {
                return;
            }

            DispatchRefreshIfLoaded(QueueRefresh);
        }

        private void GameCustomDataStore_CustomDataChanged(object sender, GameCustomDataChangedEventArgs e)
        {
            if (e == null || e.PlayniteGameId == Guid.Empty || !IsCurrentGame(e.PlayniteGameId.ToString()))
            {
                return;
            }

            DispatchRefreshIfLoaded(() => QueueRefreshIfMatches(e.PlayniteGameId));
        }

        private void RefreshService_GameCacheUpdated(object sender, GameCacheUpdatedEventArgs e)
        {
            // Filter on the event thread so a library-wide refresh does not queue one UI
            // dispatch per saved game on every visible item; the dispatched match below
            // re-checks against the live DataContext in case the container was recycled.
            if (!IsCurrentGame(e?.GameId) ||
                !Guid.TryParse(e.GameId, out var updatedGameId) || updatedGameId == Guid.Empty)
            {
                return;
            }

            DispatchRefreshIfLoaded(() => QueueRefreshIfMatches(updatedGameId));
        }

        private bool IsCurrentGame(string gameIdText)
        {
            var current = _currentGameIdText;
            return !string.IsNullOrWhiteSpace(gameIdText) &&
                   !string.IsNullOrEmpty(current) &&
                   string.Equals(current, gameIdText.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        // Cache events arrive on any thread; IsLoaded is read on the UI thread.
        private void DispatchRefreshIfLoaded(Action refresh)
        {
            var dispatcher = _host.Dispatcher;
            if (dispatcher == null)
            {
                return;
            }

            if (dispatcher.CheckAccess())
            {
                if (_host.IsLoaded)
                {
                    refresh();
                }
                return;
            }

            dispatcher.BeginInvoke(
                new Action(() =>
                {
                    if (_host.IsLoaded)
                    {
                        refresh();
                    }
                }),
                DispatcherPriority.Background);
        }

        private void QueueRefreshIfMatches(Guid updatedGameId)
        {
            var game = ThemeViewItemGameResolver.GetGame(_host.DataContext);
            if (game == null || game.Id == Guid.Empty || game.Id != updatedGameId)
            {
                return;
            }

            QueueRefresh();
        }

        private void QueueRefresh()
        {
            if (!_host.IsLoaded || _cacheRefreshQueued)
            {
                return;
            }

            _cacheRefreshQueued = true;
            var dispatcher = _host.Dispatcher;
            if (dispatcher == null)
            {
                RunQueuedRefresh();
                return;
            }

            dispatcher.BeginInvoke(
                new Action(RunQueuedRefresh),
                DispatcherPriority.Background);
        }

        private void RunQueuedRefresh()
        {
            if (_traceQueuedRefresh)
            {
                _logger?.Debug("RunQueuedRefresh: executing");
            }

            _cacheRefreshQueued = false;
            if (!_host.IsLoaded)
            {
                if (_traceQueuedRefresh)
                {
                    _logger?.Debug("RunQueuedRefresh: control no longer loaded, skipping");
                }

                return;
            }

            _requestedGameIdText = null;
            TryUpdateFromDataContext();
        }

        private void TryUpdateFromDataContext()
        {
            var game = ThemeViewItemGameResolver.GetGame(_host.DataContext);
            if (game != null && game.Id != Guid.Empty)
            {
                _currentGameIdText = game.Id.ToString();
                UpdateForGame(game.Id);
            }
            else
            {
                _currentGameIdText = null;
                _requestedGameIdText = null;
                ClearData();
            }
        }

        private void UpdateForGame(Guid gameId)
        {
            var gameIdText = gameId.ToString();
            var dataService = Plugin?.AchievementDataService;
            if (dataService == null || string.Equals(_requestedGameIdText, gameIdText, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _requestedGameIdText = gameIdText;
            LoadAsync(
                dataService,
                gameId,
                _host.Dispatcher,
                isStale: () => !string.Equals(_currentGameIdText, gameIdText, StringComparison.OrdinalIgnoreCase),
                apply: ApplyGameData,
                logger: _logger);
        }

        private void ApplyGameData(GameAchievementData gameData)
        {
            if (gameData == null || !gameData.HasAchievements || (gameData.Achievements?.Count ?? 0) == 0)
            {
                ClearData();
                return;
            }

            _host.SetValue(_unlockedCountProperty, gameData.UnlockedCount);
            _host.SetValue(_achievementCountProperty, gameData.AchievementCount);
            _host.SetValue(_hasDataProperty, true);
            _onApplied?.Invoke();
        }

        private void ClearData()
        {
            _host.SetValue(_unlockedCountProperty, 0);
            _host.SetValue(_achievementCountProperty, 0);
            _host.SetValue(_hasDataProperty, false);
        }

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
