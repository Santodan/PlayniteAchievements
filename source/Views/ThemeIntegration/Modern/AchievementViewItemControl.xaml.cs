using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Playnite.SDK;
using Playnite.SDK.Models;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
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
        private static readonly string[] DataContextGamePropertyCandidates =
        {
            "Game",
            "Source",
            "Item",
            "SourceItem",
            "Value"
        };

        /// <summary>
        /// Cache for reflected property accessors to avoid repeated reflection on each row.
        /// Key is the Type.FullName, value is the property name that yields a Game (or null if none).
        /// </summary>
        private static readonly ConcurrentDictionary<string, string> _gamePropertyCache = new ConcurrentDictionary<string, string>();
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

        private void RefreshService_GameCacheUpdated(object sender, GameCacheUpdatedEventArgs e)
        {
            var updatedGameId = ParseUpdatedGameId(e);
            if (!updatedGameId.HasValue)
            {
                return;
            }

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
                    QueueRefreshIfMatches(updatedGameId.Value);
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
                            QueueRefreshIfMatches(updatedGameId.Value);
                        }
                    }),
                    DispatcherPriority.Render);
            }
        }

        private Guid? GetCurrentGameIdFromDataContext()
        {
            var game = GetGameFromDataContext(DataContext);
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
            var game = GetGameFromDataContext(DataContext);
            if (game != null && game.Id != Guid.Empty)
            {
                UpdateForGame(game.Id);
            }
            else
            {
                ClearData();
            }
        }

        /// <summary>
        /// Extracts a Game from various DataContext types that Playnite uses.
        /// GridView items use GamesCollectionViewEntry which wraps the Game.
        /// Uses cached property names for performance.
        /// </summary>
        private Game GetGameFromDataContext(object dataContext)
        {
            if (dataContext == null)
            {
                return null;
            }

            // Direct Game reference
            if (dataContext is Game game)
            {
                return game;
            }

            var type = dataContext.GetType();
            var typeKey = type.FullName;

            // Check cache for known property name
            if (_gamePropertyCache.TryGetValue(typeKey, out var cachedPropertyName))
            {
                if (cachedPropertyName == null)
                {
                    // Previously determined this type has no Game property
                    return null;
                }
                if (TryGetGamePropertyValue(dataContext, cachedPropertyName, out var cachedGame))
                {
                    return cachedGame;
                }
            }

            // Try common wrapper property names used by different Playnite view templates.
            foreach (var propertyName in DataContextGamePropertyCandidates)
            {
                if (TryGetGamePropertyValue(dataContext, propertyName, out var wrappedGame))
                {
                    // Cache the successful property name for this type
                    _gamePropertyCache.TryAdd(typeKey, propertyName);
                    return wrappedGame;
                }
            }

            // Cache that this type has no Game property
            _gamePropertyCache.TryAdd(typeKey, null);
            return null;
        }

        private static bool TryGetGamePropertyValue(object source, string propertyName, out Game game)
        {
            game = null;
            if (source == null || string.IsNullOrWhiteSpace(propertyName))
            {
                return false;
            }

            var property = source.GetType().GetProperty(propertyName);
            if (property == null || property.GetIndexParameters().Length != 0)
            {
                return false;
            }

            object propertyValue;
            try
            {
                propertyValue = property.GetValue(source);
            }
            catch
            {
                return false;
            }

            if (propertyValue is Game directGame)
            {
                game = directGame;
                return true;
            }

            if (propertyValue == null || ReferenceEquals(propertyValue, source))
            {
                return false;
            }

            var nestedGameProperty = propertyValue.GetType().GetProperty("Game");
            if (nestedGameProperty == null || nestedGameProperty.GetIndexParameters().Length != 0)
            {
                return false;
            }

            try
            {
                game = nestedGameProperty.GetValue(propertyValue) as Game;
            }
            catch
            {
                game = null;
            }

            return game != null;
        }

        private void UpdateForGame(Guid gameId)
        {
            var gameData = Plugin?.AchievementDataService?.GetGameAchievementData(gameId);

            if (gameData == null || !gameData.HasAchievements || (gameData.Achievements?.Count ?? 0) == 0)
            {
                ClearData();
                return;
            }

            var achievements = gameData.Achievements;
            // Use pre-computed counts from GameAchievementData instead of counting with LINQ
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

