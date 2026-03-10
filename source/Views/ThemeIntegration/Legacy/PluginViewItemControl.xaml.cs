// --SUCCESSSTORY--
using System;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using Playnite.SDK;
using Playnite.SDK.Controls;
using Playnite.SDK.Models;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.Logging;

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
        private static readonly string[] DataContextGamePropertyCandidates =
        {
            "Game",
            "Source",
            "Item",
            "SourceItem",
            "Value"
        };
        private PlayniteAchievementsPlugin Plugin => PlayniteAchievementsPlugin.Instance;
        private bool _isCacheEventSubscribed;
        private bool _cacheRefreshQueued;

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

            var service = Plugin?.AchievementService;
            if (service == null)
            {
                return;
            }

            service.CacheInvalidated -= AchievementService_CacheInvalidated;
            service.CacheInvalidated += AchievementService_CacheInvalidated;
            service.GameCacheUpdated -= AchievementService_GameCacheUpdated;
            service.GameCacheUpdated += AchievementService_GameCacheUpdated;
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
                Plugin?.AchievementService.CacheInvalidated -= AchievementService_CacheInvalidated;
                Plugin?.AchievementService.GameCacheUpdated -= AchievementService_GameCacheUpdated;
            }
            catch
            {
            }

            _isCacheEventSubscribed = false;
        }

        private void AchievementService_CacheInvalidated(object sender, EventArgs e)
        {
            // CacheInvalidated fires when any cache change occurs (throttled).
            // Always refresh this control since we don't know which game changed.
            _logger.Debug($"CacheInvalidated event received, IsLoaded={IsLoaded}");
            if (!IsLoaded)
            {
                return;
            }

            var dispatcher = Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
            {
                QueueRefresh();
            }
            else
            {
                dispatcher.BeginInvoke(
                    new Action(QueueRefresh),
                    DispatcherPriority.Render);
            }
        }

        private void AchievementService_GameCacheUpdated(object sender, GameCacheUpdatedEventArgs e)
        {
            var updatedGameId = ParseUpdatedGameId(e);
            if (!updatedGameId.HasValue)
            {
                _logger.Debug($"GameCacheUpdated event received but GameId was null or invalid");
                return;
            }

            _logger.Debug($"GameCacheUpdated event received, GameId={updatedGameId.Value}, IsLoaded={IsLoaded}");
            if (!IsLoaded)
            {
                return;
            }

            var dispatcher = Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
            {
                QueueRefreshIfMatches(updatedGameId.Value);
                return;
            }

            dispatcher.BeginInvoke(
                new Action(() => QueueRefreshIfMatches(updatedGameId.Value)),
                DispatcherPriority.Render);
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
            _logger.Debug($"QueueRefreshIfMatches: updatedGameId={updatedGameId}, currentGameId={currentGameId}");
            if (!currentGameId.HasValue || currentGameId.Value != updatedGameId)
            {
                return;
            }

            QueueRefresh();
        }

        private void QueueRefresh()
        {
            _logger.Debug($"QueueRefresh: IsLoaded={IsLoaded}, _cacheRefreshQueued={_cacheRefreshQueued}");
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

            // Try common wrapper property names used by different Playnite view templates.
            foreach (var propertyName in DataContextGamePropertyCandidates)
            {
                if (TryGetGamePropertyValue(dataContext, propertyName, out var wrappedGame))
                {
                    return wrappedGame;
                }
            }

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
            _logger.Debug($"UpdateForGame called for gameId={gameId}");
            var gameData = Plugin?.AchievementService?.GetGameAchievementData(gameId);

            if (gameData == null || !gameData.HasAchievements || (gameData.Achievements?.Count ?? 0) == 0)
            {
                _logger.Debug($"UpdateForGame: Clearing data (no achievements)");
                ClearData();
                return;
            }

            var achievements = gameData.Achievements;
            var unlocked = achievements.Count(a => a.Unlocked);
            var total = achievements.Count;
            _logger.Debug($"UpdateForGame: Setting UnlockedCount={unlocked}, AchievementCount={total}");
            UnlockedCount = unlocked;
            AchievementCount = total;
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
