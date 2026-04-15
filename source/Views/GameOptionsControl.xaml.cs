using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Playnite.SDK;
using PlayniteAchievements.Models;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Providers.Manual;
using PlayniteAchievements.Services;
using PlayniteAchievements.ViewModels;

namespace PlayniteAchievements.Views
{
    public partial class GameOptionsControl : UserControl
    {
        private readonly RefreshRuntime _refreshService;
        private readonly ICacheManager _cacheManager;
        private readonly Action _persistSettingsForUi;
        private readonly AchievementOverridesService _achievementOverridesService;
        private readonly AchievementDataService _achievementDataService;
        private readonly IPlayniteAPI _playniteApi;
        private readonly ILogger _logger;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly ManualSourceRegistry _manualSourceRegistry;
        private readonly GameOptionsViewModel _viewModel;
        private readonly GameOptionsDataSnapshotProvider _gameDataSnapshotProvider;

        private GameOptionsCapstonesTab _capstoneControl;
        private GameOptionsManualTrackingTab _manualControl;
        private GameOptionsAchievementOrderTab _achievementOrderControl;
        private GameOptionsCategoryTab _categoryControl;
        private GameOptionsAchievementIconsTab _achievementIconsControl;
        private ManualAchievementsViewModel _manualViewModel;
        private GameOptionsAchievementOrderViewModel _achievementOrderViewModel;
        private GameOptionsCategoryViewModel _categoryViewModel;
        private GameOptionsAchievementIconsViewModel _achievementIconsViewModel;
        private bool _manualStartAtEditing;
        private bool _manualRefreshPending;
        private bool _capstoneRefreshPending;
        private bool _achievementOrderRefreshPending;
        private bool _categoryRefreshPending;
        private bool _achievementIconsRefreshPending;
        private bool _ensureTabContentQueued;

        internal GameOptionsControl(
            Guid gameId,
            GameOptionsTab initialTab,
            RefreshRuntime refreshRuntime,
            ICacheManager cacheManager,
            Action persistSettingsForUi,
            AchievementOverridesService achievementOverridesService,
            AchievementDataService achievementDataService,
            IPlayniteAPI playniteApi,
            ILogger logger,
            PlayniteAchievementsSettings settings,
            ManualSourceRegistry manualSourceRegistry)
        {
            _refreshService = refreshRuntime ?? throw new ArgumentNullException(nameof(refreshRuntime));
            _cacheManager = cacheManager ?? throw new ArgumentNullException(nameof(cacheManager));
            _persistSettingsForUi = persistSettingsForUi ?? throw new ArgumentNullException(nameof(persistSettingsForUi));
            _achievementOverridesService = achievementOverridesService ?? throw new ArgumentNullException(nameof(achievementOverridesService));
            _achievementDataService = achievementDataService ?? throw new ArgumentNullException(nameof(achievementDataService));
            _playniteApi = playniteApi;
            _logger = logger;
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _manualSourceRegistry = manualSourceRegistry ?? throw new ArgumentNullException(nameof(manualSourceRegistry));
            _gameDataSnapshotProvider = new GameOptionsDataSnapshotProvider(gameId, _achievementDataService);

            _viewModel = new GameOptionsViewModel(
                gameId,
                initialTab,
                PlayniteAchievementsPlugin.Instance,
                _refreshService,
                _persistSettingsForUi,
                _achievementOverridesService,
                _gameDataSnapshotProvider,
                _playniteApi,
                _settings,
                _logger);

            DataContext = _viewModel;
            InitializeComponent();

            _viewModel.PropertyChanged += ViewModel_PropertyChanged;
            _refreshService.GameCacheUpdated += RefreshService_GameCacheUpdated;
            _refreshService.CacheDeltaUpdated += RefreshService_CacheDeltaUpdated;
            Loaded += GameOptionsControl_Loaded;
        }

        public string WindowTitle
        {
            get
            {
                var format = ResourceProvider.GetString("LOCPlayAch_GameOptions_WindowTitle");
                if (string.IsNullOrWhiteSpace(format))
                {
                    format = "Game Options - {0}";
                }

                return string.Format(format, _viewModel?.GameName ?? "Game");
            }
        }

        private void GameOptionsControl_Loaded(object sender, System.Windows.RoutedEventArgs e)
        {
            QueueEnsureSelectedTabContent();
        }

        public void Cleanup()
        {
            Loaded -= GameOptionsControl_Loaded;

            if (_viewModel != null)
            {
                _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
            }
            if (_refreshService != null)
            {
                _refreshService.GameCacheUpdated -= RefreshService_GameCacheUpdated;
                _refreshService.CacheDeltaUpdated -= RefreshService_CacheDeltaUpdated;
            }

            CleanupCapstone();
            CleanupManual();
            CleanupAchievementOrder();
            CleanupCategory();
            CleanupAchievementIcons();
        }

        private void ViewModel_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e == null)
            {
                return;
            }

            if (e.PropertyName == nameof(GameOptionsViewModel.SelectedTab))
            {
                QueueEnsureSelectedTabContent();
            }
            else if (e.PropertyName == nameof(GameOptionsViewModel.HasManualTrackingLink) &&
                     _viewModel.SelectedTab == GameOptionsTab.ManualTracking)
            {
                // Do not recreate the manual tab when a link changes mid-flow.
                // The wizard saves a transient link before refresh starts, and rolling back
                // removes it during failure handling. Recreating here resets the stage/progress
                // UI while work is still in flight.
                // Only recreate if not currently in a refresh operation.
                if (!_viewModel.HasManualTrackingLink && !IsManualViewModelRefreshing())
                {
                    EnsureManualControl(forceRecreate: true);
                }
            }
            else if (e.PropertyName == nameof(GameOptionsViewModel.CustomDataRevision))
            {
                HandleCustomDataRevisionChanged();
            }
            else if (e.PropertyName == nameof(GameOptionsViewModel.HasCapstoneData) &&
                     _viewModel.SelectedTab == GameOptionsTab.Capstones)
            {
                EnsureCapstoneControl(forceRecreate: true);
            }
        }

        private void QueueEnsureSelectedTabContent()
        {
            if (_ensureTabContentQueued)
            {
                return;
            }

            _ensureTabContentQueued = true;
            _ = Dispatcher.BeginInvoke(new Action(() =>
            {
                _ensureTabContentQueued = false;
                EnsureSelectedTabContent();
            }), DispatcherPriority.Background);
        }

        private void EnsureSelectedTabContent()
        {
            if (_viewModel == null)
            {
                return;
            }

            if (_viewModel.SelectedTab == GameOptionsTab.Capstones)
            {
                var hadCapstoneControl = _capstoneControl != null;
                EnsureCapstoneControl(forceRecreate: false);
                if (_capstoneRefreshPending && _capstoneControl != null)
                {
                    if (hadCapstoneControl)
                    {
                        _capstoneControl.RefreshData();
                    }

                    _capstoneRefreshPending = false;
                }
            }
            else if (_viewModel.SelectedTab == GameOptionsTab.ManualTracking)
            {
                EnsureManualControl(forceRecreate: false);
                if (_manualRefreshPending && !IsManualViewModelRefreshing() && _manualControl != null)
                {
                    _manualRefreshPending = false;
                }
            }
            else if (_viewModel.SelectedTab == GameOptionsTab.AchievementOrder)
            {
                var hadAchievementOrderControl = _achievementOrderControl != null;
                EnsureAchievementOrderControl(forceRecreate: false);
                if (_achievementOrderRefreshPending)
                {
                    if (hadAchievementOrderControl)
                    {
                        _achievementOrderControl?.RefreshData();
                    }

                    _achievementOrderRefreshPending = false;
                }
            }
            else if (_viewModel.SelectedTab == GameOptionsTab.Category)
            {
                var hadCategoryControl = _categoryControl != null;
                EnsureCategoryControl(forceRecreate: false);
                if (_categoryRefreshPending)
                {
                    if (hadCategoryControl)
                    {
                        _categoryViewModel?.ReloadData();
                    }

                    _categoryRefreshPending = false;
                }
            }
            else if (_viewModel.SelectedTab == GameOptionsTab.CustomIcons)
            {
                var hadAchievementIconsControl = _achievementIconsControl != null;
                EnsureAchievementIconsControl(forceRecreate: false);
                if (_achievementIconsRefreshPending)
                {
                    if (hadAchievementIconsControl)
                    {
                        _achievementIconsControl?.RefreshData();
                    }

                    _achievementIconsRefreshPending = false;
                }
            }
        }

        private bool IsManualViewModelRefreshing()
        {
            return _manualViewModel?.IsRefreshingStage ?? false;
        }

        private void EnsureCapstoneControl(bool forceRecreate)
        {
            if (!_viewModel.HasCapstoneData)
            {
                CleanupCapstone();
                return;
            }

            if (_capstoneControl != null && !forceRecreate)
            {
                return;
            }

            CleanupCapstone();

            _capstoneControl = new GameOptionsCapstonesTab(
                _viewModel.GameId,
                _achievementOverridesService,
                _gameDataSnapshotProvider,
                _playniteApi,
                _logger,
                _settings);
            _capstoneControl.CapstoneChanged += CapstoneControl_CapstoneChanged;
            CapstoneHost.Content = _capstoneControl;
            _capstoneRefreshPending = false;
        }

        private void CleanupCapstone()
        {
            if (_capstoneControl != null)
            {
                try
                {
                    _capstoneControl.CapstoneChanged -= CapstoneControl_CapstoneChanged;
                    _capstoneControl.Cleanup();
                }
                catch (Exception ex)
                {
                    _logger?.Debug(ex, "Failed to cleanup capstone tab control.");
                }
            }

            _capstoneControl = null;
            if (CapstoneHost != null)
            {
                CapstoneHost.Content = null;
            }
        }

        private void EnsureManualControl(bool forceRecreate)
        {
            var game = _playniteApi?.Database?.Games?.Get(_viewModel.GameId);
            if (game == null)
            {
                return;
            }

            var startAtEditing = ManualAchievementsProvider.TryGetManualLink(game.Id, out var existingLink);
            if (_manualControl != null && !forceRecreate && IsManualViewModelRefreshing())
            {
                // The manual flow sets a transient link before refresh completes. That can
                // temporarily flip startAtEditing and trigger an unintended control recreate,
                // which cancels the in-flight refresh via Cleanup(). Keep the active VM alive.
                _logger?.Debug($"Deferring manual tab recreation while refresh is active (startAtEditing={startAtEditing}).");
                _manualStartAtEditing = startAtEditing;
                return;
            }

            if (_manualControl != null && !forceRecreate && _manualStartAtEditing == startAtEditing)
            {
                return;
            }

            CleanupManual();

            _manualStartAtEditing = startAtEditing;

            // Get all available sources
            var availableSources = _manualSourceRegistry.GetAllSources();

            // Determine the initial source based on existing link or default to Steam
            IManualSource initialSource;
            if (startAtEditing &&
                existingLink != null)
            {
                // Use the source from the existing link
                initialSource = _manualSourceRegistry.GetSourceByKey(existingLink.SourceKey);
                if (initialSource == null)
                {
                    _logger?.Warn($"Unknown manual source key '{existingLink.SourceKey}', falling back to Steam");
                    initialSource = _manualSourceRegistry.GetDefaultSource();
                }
            }
            else
            {
                initialSource = _manualSourceRegistry.GetDefaultSource();
            }

            _manualViewModel = new ManualAchievementsViewModel(
                game,
                _refreshService,
                _cacheManager,
                _achievementDataService,
                availableSources,
                initialSource,
                _settings,
                SaveSettings,
                _logger,
                _playniteApi,
                startAtEditingStage: startAtEditing);
            _manualViewModel.ManualLinkSaved += ManualViewModel_ManualLinkSaved;

            _manualControl = new GameOptionsManualTrackingTab(_manualViewModel);
            _manualControl.UnlinkCommand = _viewModel.UnlinkManualTrackingCommand;
            ManualHost.Content = _manualControl;
        }

        private void EnsureAchievementOrderControl(bool forceRecreate)
        {
            if (_achievementOrderControl != null && !forceRecreate)
            {
                return;
            }

            CleanupAchievementOrder();

            _achievementOrderViewModel = new GameOptionsAchievementOrderViewModel(
                _viewModel.GameId,
                _achievementOverridesService,
                _gameDataSnapshotProvider,
                _settings,
                _logger);
            _achievementOrderControl = new GameOptionsAchievementOrderTab(_achievementOrderViewModel);
            AchievementOrderHost.Content = _achievementOrderControl;
        }

        private void EnsureCategoryControl(bool forceRecreate)
        {
            if (_categoryControl != null && !forceRecreate)
            {
                return;
            }

            CleanupCategory();

            _categoryViewModel = new GameOptionsCategoryViewModel(
                _viewModel.GameId,
                _achievementOverridesService,
                _gameDataSnapshotProvider,
                _settings,
                _logger);
            _categoryControl = new GameOptionsCategoryTab(_categoryViewModel);
            CategoryHost.Content = _categoryControl;
        }

        private void EnsureAchievementIconsControl(bool forceRecreate)
        {
            if (_achievementIconsControl != null && !forceRecreate)
            {
                return;
            }

            CleanupAchievementIcons();

            _achievementIconsViewModel = new GameOptionsAchievementIconsViewModel(
                _viewModel.GameId,
                _achievementOverridesService,
                _gameDataSnapshotProvider,
                PlayniteAchievementsPlugin.Instance?.ManagedCustomIconService,
                _settings,
                _logger);
            _achievementIconsControl = new GameOptionsAchievementIconsTab(_achievementIconsViewModel);
            _achievementIconsControl.IconOverridesSaved += AchievementIconsControl_IconOverridesSaved;
            AchievementIconsHost.Content = _achievementIconsControl;
        }

        private void ManualViewModel_ManualLinkSaved(object sender, EventArgs e)
        {
            HandleStateChanged(refreshCapstone: true);
        }

        private void CapstoneControl_CapstoneChanged(object sender, EventArgs e)
        {
            _gameDataSnapshotProvider?.Invalidate();
            _viewModel?.Reload();
        }

        private void AchievementIconsControl_IconOverridesSaved(object sender, EventArgs e)
        {
            _gameDataSnapshotProvider?.Invalidate();
            _achievementIconsControl?.RefreshData();
            _viewModel?.NotifyIconOverridesChanged();
        }

        private void RefreshService_GameCacheUpdated(object sender, GameCacheUpdatedEventArgs e)
        {
            if (_viewModel == null ||
                !Guid.TryParse(e?.GameId, out var updatedGameId) ||
                updatedGameId != _viewModel.GameId)
            {
                return;
            }

            DispatchHandleStateChanged(refreshCapstone: true);
        }

        private void RefreshService_CacheDeltaUpdated(object sender, CacheDeltaEventArgs e)
        {
            if (e?.IsFullReset != true)
            {
                return;
            }

            DispatchHandleStateChanged(refreshCapstone: true);
        }

        private void DispatchHandleStateChanged(bool refreshCapstone)
        {
            if (Dispatcher.CheckAccess())
            {
                HandleStateChanged(refreshCapstone);
                return;
            }

            _ = Dispatcher.BeginInvoke(new Action(() => HandleStateChanged(refreshCapstone)));
        }

        private void HandleStateChanged(bool refreshCapstone)
        {
            _gameDataSnapshotProvider?.Invalidate();
            _viewModel.Reload();

            _manualRefreshPending = true;
            _achievementOrderRefreshPending = true;
            _categoryRefreshPending = true;
            _achievementIconsRefreshPending = true;

            if (refreshCapstone)
            {
                _capstoneRefreshPending = true;
            }

            EnsureSelectedTabContent();
        }

        private void HandleCustomDataRevisionChanged()
        {
            _gameDataSnapshotProvider?.Invalidate();
            _manualRefreshPending = true;
            _capstoneRefreshPending = true;
            _achievementOrderRefreshPending = true;
            _categoryRefreshPending = true;
            _achievementIconsRefreshPending = true;
            EnsureSelectedTabContent();
        }

        private void CleanupManual()
        {
            if (_manualViewModel != null)
            {
                _manualViewModel.ManualLinkSaved -= ManualViewModel_ManualLinkSaved;
            }

            if (_manualControl != null)
            {
                try
                {
                    _manualControl.Cleanup();
                }
                catch (Exception ex)
                {
                    _logger?.Debug(ex, "Failed to cleanup manual tracking tab control.");
                }
            }

            _manualControl = null;
            _manualViewModel = null;

            if (ManualHost != null)
            {
                ManualHost.Content = null;
            }
        }

        private void CleanupAchievementOrder()
        {
            _achievementOrderControl = null;
            _achievementOrderViewModel = null;

            if (AchievementOrderHost != null)
            {
                AchievementOrderHost.Content = null;
            }
        }

        private void CleanupCategory()
        {
            _categoryControl = null;
            _categoryViewModel = null;

            if (CategoryHost != null)
            {
                CategoryHost.Content = null;
            }
        }

        private void CleanupAchievementIcons()
        {
            if (_achievementIconsControl != null)
            {
                try
                {
                    _achievementIconsControl.IconOverridesSaved -= AchievementIconsControl_IconOverridesSaved;
                    _achievementIconsControl.Cleanup();
                }
                catch (Exception ex)
                {
                    _logger?.Debug(ex, "Failed to cleanup achievement icon tab control.");
                }
            }

            _achievementIconsControl = null;
            _achievementIconsViewModel = null;

            if (AchievementIconsHost != null)
            {
                AchievementIconsHost.Content = null;
            }
        }

        private void SaveSettings(PlayniteAchievementsSettings settings)
        {
            try
            {
                PlayniteAchievementsPlugin.Instance?.SavePluginSettings(settings);
                HandleStateChanged(refreshCapstone: false);
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, "Failed to persist settings from Game Options view.");
            }
        }
    }
}




