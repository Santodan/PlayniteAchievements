using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Playnite.SDK;
using PlayniteAchievements.Models;
using PlayniteAchievements.Providers.Manual;
using PlayniteAchievements.Services;
using PlayniteAchievements.ViewModels;

namespace PlayniteAchievements.Views
{
    public partial class GameOptionsControl : UserControl
    {
        private readonly AchievementService _achievementService;
        private readonly IPlayniteAPI _playniteApi;
        private readonly ILogger _logger;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly ManualAchievementsProvider _manualProvider;
        private readonly GameOptionsViewModel _viewModel;

        private GameOptionsCapstonesTab _capstoneControl;
        private GameOptionsManualTrackingTab _manualControl;
        private GameOptionsAchievementOrderTab _achievementOrderControl;
        private GameOptionsCategoryTab _categoryControl;
        private ManualAchievementsViewModel _manualViewModel;
        private GameOptionsAchievementOrderViewModel _achievementOrderViewModel;
        private GameOptionsCategoryViewModel _categoryViewModel;
        private bool _manualStartAtEditing;
        private bool _capstoneRefreshPending;

        internal GameOptionsControl(
            Guid gameId,
            GameOptionsTab initialTab,
            AchievementService achievementService,
            IPlayniteAPI playniteApi,
            ILogger logger,
            PlayniteAchievementsSettings settings,
            ManualAchievementsProvider manualProvider)
        {
            _achievementService = achievementService ?? throw new ArgumentNullException(nameof(achievementService));
            _playniteApi = playniteApi;
            _logger = logger;
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _manualProvider = manualProvider ?? throw new ArgumentNullException(nameof(manualProvider));

            _viewModel = new GameOptionsViewModel(
                gameId,
                initialTab,
                PlayniteAchievementsPlugin.Instance,
                _achievementService,
                _playniteApi,
                _settings,
                _logger);

            DataContext = _viewModel;
            InitializeComponent();

            _viewModel.PropertyChanged += ViewModel_PropertyChanged;
            _achievementService.CacheInvalidated += AchievementService_CacheInvalidated;
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
            EnsureSelectedTabContent();
        }

        public void Cleanup()
        {
            Loaded -= GameOptionsControl_Loaded;

            if (_viewModel != null)
            {
                _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
            }
            if (_achievementService != null)
            {
                _achievementService.CacheInvalidated -= AchievementService_CacheInvalidated;
            }

            CleanupCapstone();
            CleanupManual();
            CleanupAchievementOrder();
            CleanupCategory();
        }

        private void ViewModel_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e == null)
            {
                return;
            }

            if (e.PropertyName == nameof(GameOptionsViewModel.SelectedTab))
            {
                EnsureSelectedTabContent();
            }
            else if (e.PropertyName == nameof(GameOptionsViewModel.HasManualTrackingLink) &&
                     _viewModel.SelectedTab == GameOptionsTab.ManualTracking)
            {
                // Do not recreate the manual tab when a link flips false->true mid-flow.
                // The wizard saves a transient link before refresh starts, and recreating
                // here resets the stage/progress UI while work is still in flight.
                // Still recreate when link is removed (true->false) so UI returns to search.
                if (!_viewModel.HasManualTrackingLink)
                {
                    EnsureManualControl(forceRecreate: true);
                }
            }
            else if (e.PropertyName == nameof(GameOptionsViewModel.HasCapstoneData) &&
                     _viewModel.SelectedTab == GameOptionsTab.Capstones)
            {
                EnsureCapstoneControl(forceRecreate: true);
            }
        }

        private void EnsureSelectedTabContent()
        {
            if (_viewModel == null)
            {
                return;
            }

            if (_viewModel.SelectedTab == GameOptionsTab.Capstones)
            {
                if (_capstoneControl != null && _capstoneRefreshPending)
                {
                    _capstoneControl.RefreshData();
                    _capstoneRefreshPending = false;
                }

                EnsureCapstoneControl(forceRecreate: false);
            }
            else if (_viewModel.SelectedTab == GameOptionsTab.ManualTracking)
            {
                EnsureManualControl(forceRecreate: false);
            }
            else if (_viewModel.SelectedTab == GameOptionsTab.AchievementOrder)
            {
                EnsureAchievementOrderControl(forceRecreate: false);
            }
            else if (_viewModel.SelectedTab == GameOptionsTab.Category)
            {
                EnsureCategoryControl(forceRecreate: false);
            }
        }

        private void EnsureCapstoneControl(bool forceRecreate)
        {
            if (!_viewModel.HasCapstoneData)
            {
                CleanupCapstone();
                return;
            }

            if (_capstoneControl != null && !forceRecreate && !_capstoneRefreshPending)
            {
                return;
            }

            CleanupCapstone();

            _capstoneControl = new GameOptionsCapstonesTab(
                _viewModel.GameId,
                _achievementService,
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
            var startAtEditing = _viewModel.HasManualTrackingLink;
            if (_manualControl != null && !forceRecreate && _manualStartAtEditing == startAtEditing)
            {
                return;
            }

            CleanupManual();

            var game = _playniteApi?.Database?.Games?.Get(_viewModel.GameId);
            if (game == null)
            {
                return;
            }

            _manualStartAtEditing = startAtEditing;

            // Get all available sources
            var availableSources = _manualProvider.GetAllSources();

            // Determine the initial source based on existing link or default to Steam
            IManualSource initialSource;
            if (startAtEditing &&
                _settings.Persisted.ManualAchievementLinks.TryGetValue(game.Id, out var existingLink) &&
                existingLink != null)
            {
                // Use the source from the existing link
                initialSource = _manualProvider.GetSourceByKey(existingLink.SourceKey);
                if (initialSource == null)
                {
                    _logger?.Warn($"Unknown manual source key '{existingLink.SourceKey}', falling back to Steam");
                    initialSource = _manualProvider.GetSteamManualSource();
                }
            }
            else
            {
                // Default to Steam for new manual links
                initialSource = _manualProvider.GetSteamManualSource();
            }

            _manualViewModel = new ManualAchievementsViewModel(
                game,
                _achievementService,
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
                _achievementService,
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
                _achievementService,
                _settings,
                _logger);
            _categoryControl = new GameOptionsCategoryTab(_categoryViewModel);
            CategoryHost.Content = _categoryControl;
        }

        private void ManualViewModel_ManualLinkSaved(object sender, EventArgs e)
        {
            HandleStateChanged(refreshCapstone: true);
        }

        private void CapstoneControl_CapstoneChanged(object sender, EventArgs e)
        {
            HandleStateChanged(refreshCapstone: false);
        }

        private void AchievementService_CacheInvalidated(object sender, EventArgs e)
        {
            if (Dispatcher.CheckAccess())
            {
                HandleStateChanged(refreshCapstone: true);
                return;
            }

            _ = Dispatcher.BeginInvoke(new Action(() => HandleStateChanged(refreshCapstone: true)));
        }

        private void HandleStateChanged(bool refreshCapstone)
        {
            _viewModel.Reload();
            _achievementOrderViewModel?.ReloadData();
            _categoryViewModel?.ReloadData();

            if (refreshCapstone)
            {
                _capstoneRefreshPending = true;
            }

            if (_viewModel.SelectedTab == GameOptionsTab.Capstones && _capstoneRefreshPending)
            {
                if (_capstoneControl != null)
                {
                    _capstoneControl.RefreshData();
                    _capstoneRefreshPending = false;
                }
                else
                {
                    EnsureCapstoneControl(forceRecreate: false);
                }
            }
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
