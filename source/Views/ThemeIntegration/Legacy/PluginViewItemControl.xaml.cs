// --SUCCESSSTORY--
using System;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using Playnite.SDK;
using Playnite.SDK.Controls;
using Playnite.SDK.Models;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.Cache;
using PlayniteAchievements.Services.GameCustomData;
using PlayniteAchievements.Services.Logging;
using PlayniteAchievements.Services.Refresh;
using PlayniteAchievements.Views.ThemeIntegration.Base;

namespace PlayniteAchievements.Views.ThemeIntegration.Legacy
{
    /// <summary>
    /// SuccessStory-compatible view item control for theme integration.
    /// Matches the original SuccessStory plugin styling.
    /// Displays achievement summary per game in grid/list views.
    /// </summary>
    public partial class PluginViewItemControl : PluginUserControl
    {
        private static readonly ILogger _logger = PluginLogger.GetLogger(nameof(PluginViewItemControl));
        private PlayniteAchievementsPlugin Plugin => PlayniteAchievementsPlugin.Instance;
        private bool _isCacheEventSubscribed;
        private bool _cacheRefreshQueued;

        // Game id of the current DataContext, written on the UI thread and read on cache-event
        // threads so per-game events can be filtered without a dispatcher round-trip per event.
        private volatile string _currentGameIdText;

        #region IntegrationViewItemWithProgressBar Property

        public static readonly DependencyProperty IntegrationViewItemWithProgressBarProperty =
            DependencyProperty.Register(
                nameof(IntegrationViewItemWithProgressBar),
                typeof(bool),
                typeof(PluginViewItemControl),
                new PropertyMetadata(false));

        public bool IntegrationViewItemWithProgressBar
        {
            get => (bool)GetValue(IntegrationViewItemWithProgressBarProperty);
            set => SetValue(IntegrationViewItemWithProgressBarProperty, value);
        }

        #endregion

        // Dependency properties for achievement data (bound in XAML)
        public static readonly DependencyProperty UnlockedCountProperty =
            DependencyProperty.Register(nameof(UnlockedCount), typeof(int), typeof(PluginViewItemControl), new PropertyMetadata(0));

        public int UnlockedCount
        {
            get => (int)GetValue(UnlockedCountProperty);
            set => SetValue(UnlockedCountProperty, value);
        }

        public static readonly DependencyProperty AchievementCountProperty =
            DependencyProperty.Register(nameof(AchievementCount), typeof(int), typeof(PluginViewItemControl), new PropertyMetadata(0));

        public int AchievementCount
        {
            get => (int)GetValue(AchievementCountProperty);
            set => SetValue(AchievementCountProperty, value);
        }

        public PluginViewItemControl()
        {
            InitializeComponent();
            FormattingCulture.Apply(this);
            DataContextChanged += OnDataContextChanged;
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
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
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            TryUpdateFromDataContext();
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

            // CacheInvalidated fires when any cache change occurs (throttled).
            // Must dispatch to UI thread first before accessing IsLoaded
            var dispatcher = Dispatcher;
            if (dispatcher == null)
            {
                return;
            }

            if (dispatcher.CheckAccess())
            {
                // Already on UI thread
                if (IsLoaded)
                {
                    QueueRefresh();
                }
            }
            else
            {
                // On background thread - dispatch to UI thread
                dispatcher.BeginInvoke(
                    new Action(() =>
                    {
                        if (IsLoaded)
                        {
                            QueueRefresh();
                        }
                    }),
                    DispatcherPriority.Background);
            }
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
            if (!IsCurrentGame(e?.GameId))
            {
                return;
            }

            var updatedGameId = ParseUpdatedGameId(e);
            if (!updatedGameId.HasValue)
            {
                return;
            }

            DispatchRefreshIfLoaded(() => QueueRefreshIfMatches(updatedGameId.Value));
        }

        private bool IsCurrentGame(string gameIdText)
        {
            var current = _currentGameIdText;
            return !string.IsNullOrWhiteSpace(gameIdText) &&
                   !string.IsNullOrEmpty(current) &&
                   string.Equals(current, gameIdText.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        private void DispatchRefreshIfLoaded(Action refresh)
        {
            var dispatcher = Dispatcher;
            if (dispatcher == null || refresh == null)
            {
                return;
            }

            if (dispatcher.CheckAccess())
            {
                if (IsLoaded)
                {
                    refresh();
                }
                return;
            }

            dispatcher.BeginInvoke(
                new Action(() =>
                {
                    if (IsLoaded)
                    {
                        refresh();
                    }
                }),
                DispatcherPriority.Background);
        }

        private Guid? GetCurrentGameIdFromDataContext()
        {
            var game = ThemeViewItemGameResolver.GetGame(DataContext);
            if (game == null || game.Id == Guid.Empty)
            {
                return null;
            }

            return game.Id;
        }

        private static Guid? ParseUpdatedGameId(GameCacheUpdatedEventArgs args)
        {
            if (args == null || string.IsNullOrWhiteSpace(args.GameId))
            {
                return null;
            }

            if (Guid.TryParse(args.GameId, out var gameId) && gameId != Guid.Empty)
            {
                return gameId;
            }

            return null;
        }

        private void QueueRefreshIfMatches(Guid updatedGameId)
        {
            var currentGameId = GetCurrentGameIdFromDataContext();
            if (!currentGameId.HasValue || currentGameId.Value != updatedGameId)
            {
                return;
            }

            QueueRefresh();
        }

        private void QueueRefresh()
        {
            if (!IsLoaded || _cacheRefreshQueued)
            {
                return;
            }

            _cacheRefreshQueued = true;
            var dispatcher = Dispatcher;
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
            _logger.Debug("RunQueuedRefresh: executing");
            _cacheRefreshQueued = false;
            if (!IsLoaded)
            {
                _logger.Debug("RunQueuedRefresh: control no longer loaded, skipping");
                return;
            }

            TryUpdateFromDataContext();
        }

        public override void GameContextChanged(Game oldContext, Game newContext)
        {
            // GameContext can be used if DataContext doesn't provide a game
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

        private void TryUpdateFromDataContext()
        {
            var game = ThemeViewItemGameResolver.GetGame(DataContext);
            if (game != null && game.Id != Guid.Empty)
            {
                _currentGameIdText = game.Id.ToString();
                UpdateForGame(game.Id);
            }
            else
            {
                _currentGameIdText = null;
                ClearData();
            }
        }

        private void UpdateForGame(Guid gameId)
        {
            // Fetch off the UI thread: the cache read can wait multiple seconds behind
            // whole-library loads (projection warms, friends overview snapshots) holding
            // the cache lock, and this runs once per visible grid item.
            var gameIdText = gameId.ToString();
            ViewItemAchievementDataLoader.LoadAsync(
                Plugin?.AchievementDataService,
                gameId,
                Dispatcher,
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

            UnlockedCount = gameData.UnlockedCount;
            AchievementCount = gameData.AchievementCount;
            Visibility = Visibility.Visible;

            // Force visual tree update so WPF re-evaluates bindings
            InvalidateVisual();
        }

        private void ClearData()
        {
            UnlockedCount = 0;
            AchievementCount = 0;
            Visibility = Visibility.Collapsed;
        }
    }
}
// --END SUCCESSSTORY--
