using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Playnite.SDK;
using Playnite.SDK.Models;
using PlayniteAchievements.Models;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.Logging;
using PlayniteAchievements.Views.ThemeIntegration.Base;

namespace PlayniteAchievements.Views.ThemeIntegration.Modern
{
    /// <summary>
    /// Modern view item control for theme integration.
    /// Displays achievement summary per game in grid/list views.
    /// </summary>
    public partial class AchievementViewItemControl : ThemeControlBase
    {
        private static readonly ILogger _logger = PluginLogger.GetLogger(nameof(AchievementViewItemControl));
        private bool _isCacheEventSubscribed;
        private bool _cacheRefreshQueued;

        #region ShowProgressBar Property

        public static readonly DependencyProperty ShowProgressBarProperty =
            DependencyProperty.Register(
                nameof(ShowProgressBar),
                typeof(bool),
                typeof(AchievementViewItemControl),
                new PropertyMetadata(false, OnShowProgressBarChanged));

        public bool ShowProgressBar
        {
            get => (bool)GetValue(ShowProgressBarProperty);
            set => SetValue(ShowProgressBarProperty, value);
        }

        private static void OnShowProgressBarChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is AchievementViewItemControl control)
            {
                control.UpdateModeVisibility();
            }
        }

        #endregion

        #region UnlockedCount Property

        public static readonly DependencyProperty UnlockedCountProperty =
            DependencyProperty.Register(
                nameof(UnlockedCount),
                typeof(int),
                typeof(AchievementViewItemControl),
                new PropertyMetadata(0));

        public int UnlockedCount
        {
            get => (int)GetValue(UnlockedCountProperty);
            set => SetValue(UnlockedCountProperty, value);
        }

        #endregion

        #region AchievementCount Property

        public static readonly DependencyProperty AchievementCountProperty =
            DependencyProperty.Register(
                nameof(AchievementCount),
                typeof(int),
                typeof(AchievementViewItemControl),
                new PropertyMetadata(0));

        public int AchievementCount
        {
            get => (int)GetValue(AchievementCountProperty);
            set => SetValue(AchievementCountProperty, value);
        }

        #endregion

        public AchievementViewItemControl()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            SubscribeToCacheEvents();
            TryUpdateFromDataContext();
            UpdateModeVisibility();
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

        private void UpdateModeVisibility()
        {
            if (LabelMode != null && ProgressBarMode != null)
            {
                LabelMode.Visibility = ShowProgressBar ? Visibility.Collapsed : Visibility.Visible;
                ProgressBarMode.Visibility = ShowProgressBar ? Visibility.Visible : Visibility.Collapsed;
            }
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

        private void RefreshService_CacheInvalidated(object sender, EventArgs e)
        {
            // CacheInvalidated fires when any cache change occurs (throttled).
            // Always refresh this control since we don't know which game changed.
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
                // On background thread - Dispatch to UI thread
                dispatcher.BeginInvoke(
                    new Action(() =>
                    {
                        if (IsLoaded)
                        {
                            QueueRefresh();
                        }
                    }),
                    DispatcherPriority.Render);
            }
        }

        private void GameCustomDataStore_CustomDataChanged(object sender, GameCustomDataChangedEventArgs e)
        {
            if (e == null || e.PlayniteGameId == Guid.Empty)
            {
                return;
            }

            DispatchRefreshIfLoaded(() => QueueRefreshIfMatches(e.PlayniteGameId));
        }

        private void RefreshService_GameCacheUpdated(object sender, GameCacheUpdatedEventArgs e)
        {
            var updatedGameId = ParseUpdatedGameId(e);
            if (!updatedGameId.HasValue)
            {
                return;
            }

            DispatchRefreshIfLoaded(() => QueueRefreshIfMatches(updatedGameId.Value));
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
                DispatcherPriority.Render);
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
                DispatcherPriority.Render);
        }

        private void RunQueuedRefresh()
        {
            _cacheRefreshQueued = false;
            if (!IsLoaded)
            {
                return;
            }

            TryUpdateFromDataContext();
        }

        public override void GameContextChanged(Game oldContext, Game newContext)
        {
            // GameContext can be used if DataContext doesn't provide a game
            if (newContext != null && newContext.Id != Guid.Empty)
            {
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
                UpdateForGame(game.Id);
            }
            else
            {
                ClearData();
            }
        }

        private void UpdateForGame(Guid gameId)
        {
            var gameData = Plugin?.AchievementDataService?.GetVisibleGameAchievementData(gameId);

            if (gameData == null || !gameData.HasAchievements || gameData.AchievementCount <= 0)
            {
                ClearData();
                return;
            }

            UnlockedCount = gameData.UnlockedCount;
            AchievementCount = gameData.AchievementCount;
            Visibility = Visibility.Visible;
        }

        private void ClearData()
        {
            UnlockedCount = 0;
            AchievementCount = 0;
            Visibility = Visibility.Collapsed;
        }
    }
}

