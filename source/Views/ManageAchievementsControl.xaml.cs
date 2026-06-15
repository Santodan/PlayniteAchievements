using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Playnite.SDK.Events;
using System.Windows.Threading;
using Playnite.SDK;
using PlayniteAchievements.Models;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Providers.Manual;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.UI;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.Views.Helpers;

namespace PlayniteAchievements.Views
{
    public partial class ManageAchievementsControl : UserControl, IFullscreenControllerNavigable
    {
        private static readonly ManageAchievementsTab[] ControllerTabOrder =
        {
            ManageAchievementsTab.Overview,
            ManageAchievementsTab.ManualTracking,
            ManageAchievementsTab.Capstones,
            ManageAchievementsTab.Category,
            ManageAchievementsTab.Filters,
            ManageAchievementsTab.Notes,
            ManageAchievementsTab.AchievementOrder,
            ManageAchievementsTab.CustomIcons,
            ManageAchievementsTab.Overrides
        };

        private readonly RefreshRuntime _refreshService;
        private readonly ICacheManager _cacheManager;
        private readonly Action _persistSettingsForUi;
        private readonly AchievementOverridesService _achievementOverridesService;
        private readonly AchievementDataService _achievementDataService;
        private readonly IPlayniteAPI _playniteApi;
        private readonly ILogger _logger;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly ManualSourceRegistry _manualSourceRegistry;
        private readonly ManageAchievementsViewModel _viewModel;
        private readonly ManageAchievementsDataSnapshotProvider _gameDataSnapshotProvider;

        private ManageAchievementsCapstonesTab _capstoneControl;
        private ManageAchievementsManualTrackingTab _manualControl;
        private ManageAchievementsAchievementOrderTab _achievementOrderControl;
        private ManageAchievementsCategoryTab _categoryControl;
        private ManageAchievementsFiltersTab _filtersControl;
        private ManageAchievementsNotesTab _notesControl;
        private ManageAchievementsAchievementIconsTab _achievementIconsControl;
        private ManualAchievementsViewModel _manualViewModel;
        private ManageAchievementsAchievementOrderViewModel _achievementOrderViewModel;
        private ManageAchievementsCategoryViewModel _categoryViewModel;
        private ManageAchievementsFiltersViewModel _filtersViewModel;
        private ManageAchievementsNotesViewModel _notesViewModel;
        private ManageAchievementsAchievementIconsViewModel _achievementIconsViewModel;
        private bool _manualStartAtEditing;
        private bool _manualRefreshPending;
        private bool _capstoneRefreshPending;
        private bool _achievementOrderRefreshPending;
        private bool _categoryRefreshPending;
        private bool _filtersRefreshPending;
        private bool _notesRefreshPending;
        private bool _achievementIconsRefreshPending;
        private bool _ensureTabContentQueued;

        internal ManageAchievementsControl(
            Guid gameId,
            ManageAchievementsTab initialTab,
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
            _gameDataSnapshotProvider = new ManageAchievementsDataSnapshotProvider(gameId, _achievementDataService);

            _viewModel = new ManageAchievementsViewModel(
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
            Loaded += ManageAchievementsControl_Loaded;
        }

        public string WindowTitle
        {
            get
            {
                var format = ResourceProvider.GetString("LOCPlayAch_ManageAchievements_WindowTitle");
                if (string.IsNullOrWhiteSpace(format))
                {
                    format = "Manage Achievements - {0}";
                }

                return string.Format(format, _viewModel?.GameName ?? "Game");
            }
        }

        private void ManageAchievementsControl_Loaded(object sender, System.Windows.RoutedEventArgs e)
        {
            QueueEnsureSelectedTabContent();
        }

        public void Cleanup()
        {
            Loaded -= ManageAchievementsControl_Loaded;

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
            CleanupFilters();
            CleanupNotes();
            CleanupAchievementIcons();
        }

        private void ViewModel_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e == null)
            {
                return;
            }

            if (e.PropertyName == nameof(ManageAchievementsViewModel.SelectedTab))
            {
                QueueEnsureSelectedTabContent();
            }
            else if (e.PropertyName == nameof(ManageAchievementsViewModel.HasManualTrackingLink) &&
                     _viewModel.SelectedTab == ManageAchievementsTab.ManualTracking)
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
            else if (e.PropertyName == nameof(ManageAchievementsViewModel.CustomDataRevision))
            {
                HandleCustomDataRevisionChanged();
            }
            else if (e.PropertyName == nameof(ManageAchievementsViewModel.HasCapstoneData) &&
                     _viewModel.SelectedTab == ManageAchievementsTab.Capstones)
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

            if (_viewModel.SelectedTab == ManageAchievementsTab.Capstones)
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
            else if (_viewModel.SelectedTab == ManageAchievementsTab.ManualTracking)
            {
                EnsureManualControl(forceRecreate: false);
                if (_manualRefreshPending && !IsManualViewModelRefreshing() && _manualControl != null)
                {
                    _manualRefreshPending = false;
                }
            }
            else if (_viewModel.SelectedTab == ManageAchievementsTab.AchievementOrder)
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
            else if (_viewModel.SelectedTab == ManageAchievementsTab.Category)
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
            else if (_viewModel.SelectedTab == ManageAchievementsTab.Filters)
            {
                var hadFiltersControl = _filtersControl != null;
                EnsureFiltersControl(forceRecreate: false);
                if (_filtersRefreshPending)
                {
                    if (hadFiltersControl)
                    {
                        _filtersControl?.RefreshData();
                    }

                    _filtersRefreshPending = false;
                }
            }
            else if (_viewModel.SelectedTab == ManageAchievementsTab.Notes)
            {
                var hadNotesControl = _notesControl != null;
                EnsureNotesControl(forceRecreate: false);
                if (_notesRefreshPending)
                {
                    if (hadNotesControl)
                    {
                        _notesControl?.RefreshData();
                    }

                    _notesRefreshPending = false;
                }
            }
            else if (_viewModel.SelectedTab == ManageAchievementsTab.CustomIcons)
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

        public bool HandleFullscreenControllerInput(ControllerInput input)
        {
            if (FullscreenControllerNavigationService.IsBackInput(input))
            {
                Window.GetWindow(this)?.Close();
                return true;
            }

            if (FullscreenControllerNavigationService.IsLeftShoulderInput(input))
            {
                return MoveSelectedTab(-1);
            }

            if (FullscreenControllerNavigationService.IsRightShoulderInput(input))
            {
                return MoveSelectedTab(1);
            }

            if (TryHandleSelectedTabControllerInput(input))
            {
                return true;
            }

            if (TryHandleDirectionalNavigation(input))
            {
                return true;
            }

            if (FullscreenControllerNavigationService.IsAcceptInput(input))
            {
                if (FullscreenControllerNavigationService.ActivateFocusedElement())
                {
                    return true;
                }

                return TryHandleGenericReveal();
            }

            return false;
        }

        private bool TryHandleDirectionalNavigation(ControllerInput input)
        {
            if (FullscreenControllerNavigationService.TryGetHorizontalDelta(input, out var horizontalDelta))
            {
                if (horizontalDelta > 0 && IsKeyboardFocusWithinTabSelector())
                {
                    return FocusCurrentContentFirstElement();
                }

                if (horizontalDelta < 0 && TryFocusTabSelectorFromContentLeftEdge())
                {
                    return true;
                }

                return TryMoveContentFocus(horizontalDelta < 0
                    ? FocusNavigationDirection.Left
                    : FocusNavigationDirection.Right);
            }

            if (FullscreenControllerNavigationService.TryGetVerticalDelta(input, out var verticalDelta))
            {
                return TryMoveContentFocus(verticalDelta < 0
                    ? FocusNavigationDirection.Up
                    : FocusNavigationDirection.Down);
            }

            return false;
        }

        private bool TryMoveContentFocus(FocusNavigationDirection direction)
        {
            if (!FullscreenControllerNavigationService.IsKeyboardFocusWithin(ManageAchievementsContentHost))
            {
                return false;
            }

            if (FullscreenControllerNavigationService.MoveFocus(direction, ManageAchievementsContentHost))
            {
                return true;
            }

            var delta = direction == FocusNavigationDirection.Up || direction == FocusNavigationDirection.Left
                ? -1
                : 1;
            return FullscreenControllerNavigationService.FocusElementByDelta(
                GetCurrentContentControllerElements(),
                delta);
        }

        private bool TryFocusTabSelectorFromContentLeftEdge()
        {
            if (!FullscreenControllerNavigationService.IsKeyboardFocusWithin(ManageAchievementsContentHost))
            {
                return false;
            }

            var focused = Keyboard.FocusedElement as DependencyObject;
            var focusedGrid = FullscreenControllerNavigationService.FindAncestor<DataGrid>(focused)
                              ?? focused as DataGrid;
            if (focusedGrid != null &&
                FullscreenControllerNavigationService.IsDescendantOf(focusedGrid, ManageAchievementsContentHost))
            {
                return FullscreenControllerNavigationService.IsFocusAtDataGridLeftEdge(focusedGrid) &&
                       FocusSelectedTabButton();
            }

            if (FullscreenControllerNavigationService.MoveFocus(FocusNavigationDirection.Left, ManageAchievementsContentHost))
            {
                return true;
            }

            return FocusSelectedTabButton();
        }

        private bool TryHandleGenericReveal()
        {
            var focused = Keyboard.FocusedElement as FrameworkElement;
            if (focused == null)
            {
                return false;
            }

            // If we're already on an interactive element, ActivateFocusedElement should have handled it.
            // But if it didn't (e.g. it was a DataGridRow with a nested checkbox), we might be here.
            // Check if the focused element is a checkbox/button first.
            if (focused is ButtonBase || focused is Selector || focused is DatePicker || focused is Expander)
            {
                return false;
            }

            if (focused.DataContext is AchievementDisplayItem item)
            {
                item.ToggleReveal();
                return true;
            }

            // Fallback for manual tracking which uses a different model
            if (focused.DataContext is ManualAchievementEditItem manualItem)
            {
                manualItem.ToggleReveal();
                return true;
            }

            return false;
        }

        private IList<RadioButton> GetVisibleTabButtons()
        {
            return new[]
                {
                    OverviewTabButton,
                    ManualTrackingTabButton,
                    CapstonesTabButton,
                    CategoryTabButton,
                    FiltersTabButton,
                    NotesTabButton,
                    AchievementOrderTabButton,
                    CustomIconsTabButton,
                    OverridesTabButton
                }
                .Where(button => button != null && button.IsVisible && button.IsEnabled)
                .ToList();
        }

        private bool IsKeyboardFocusWithinTabSelector()
        {
            return GetVisibleTabButtons().Any(button => button.IsKeyboardFocusWithin);
        }

        private bool FocusSelectedTabButton()
        {
            var tabButton = GetVisibleTabButtons()
                .FirstOrDefault(button => button.IsChecked == true)
                ?? GetVisibleTabButtons().FirstOrDefault();

            return tabButton != null && FullscreenControllerNavigationService.FocusElement(tabButton);
        }

        private bool FocusCurrentContentFirstElement()
        {
            return FullscreenControllerNavigationService.FocusFirstElement(GetCurrentContentControllerElements());
        }

        private IList<UIElement> GetCurrentContentControllerElements()
        {
            EnsureSelectedTabContent();

            DependencyObject root;
            switch (_viewModel?.SelectedTab)
            {
                case ManageAchievementsTab.Overview:
                    root = OverviewTabControl;
                    break;
                case ManageAchievementsTab.Overrides:
                    root = OverridesTabControl;
                    break;
                case ManageAchievementsTab.ManualTracking:
                    return _manualControl?.GetControllerElements() ?? new List<UIElement>();
                case ManageAchievementsTab.Capstones:
                    return _capstoneControl?.GetControllerElements() ?? new List<UIElement>();
                case ManageAchievementsTab.Category:
                    return _categoryControl?.GetControllerElements() ?? new List<UIElement>();
                case ManageAchievementsTab.Filters:
                    return _filtersControl?.GetControllerElements() ?? new List<UIElement>();
                case ManageAchievementsTab.Notes:
                    return _notesControl?.GetControllerElements() ?? new List<UIElement>();
                case ManageAchievementsTab.AchievementOrder:
                    return _achievementOrderControl?.GetControllerElements() ?? new List<UIElement>();
                case ManageAchievementsTab.CustomIcons:
                    return _achievementIconsControl?.GetControllerElements() ?? new List<UIElement>();
                default:
                    root = ManageAchievementsContentHost;
                    break;
            }

            return FullscreenControllerNavigationService.GetVisibleFocusableElements(root);
        }

        private bool MoveSelectedTab(int delta)
        {
            if (_viewModel == null || delta == 0)
            {
                return false;
            }

            var tabs = ControllerTabOrder
                .Where(IsControllerTabVisible)
                .ToList();
            if (tabs.Count <= 1)
            {
                return false;
            }

            var currentIndex = tabs.IndexOf(_viewModel.SelectedTab);
            if (currentIndex < 0)
            {
                currentIndex = 0;
            }

            var nextIndex = (currentIndex + delta + tabs.Count) % tabs.Count;
            _viewModel.SelectedTab = tabs[nextIndex];
            QueueFocusSelectedTab();
            return true;
        }

        private bool TryHandleSelectedTabControllerInput(ControllerInput input)
        {
            if (_viewModel == null)
            {
                return false;
            }

            switch (_viewModel.SelectedTab)
            {
                case ManageAchievementsTab.ManualTracking:
                    return _manualControl?.HandleFullscreenControllerInput(input) == true;
                case ManageAchievementsTab.Category:
                    return _categoryControl?.HandleFullscreenControllerInput(input) == true;
                case ManageAchievementsTab.Filters:
                    return _filtersControl?.HandleFullscreenControllerInput(input) == true;
                case ManageAchievementsTab.Notes:
                    return _notesControl?.HandleFullscreenControllerInput(input) == true;
                case ManageAchievementsTab.Capstones:
                    return _capstoneControl?.HandleFullscreenControllerInput(input) == true;
                case ManageAchievementsTab.AchievementOrder:
                    return _achievementOrderControl?.HandleFullscreenControllerInput(input) == true;
                case ManageAchievementsTab.CustomIcons:
                    return _achievementIconsControl?.HandleFullscreenControllerInput(input) == true;
                default:
                    return false;
            }
        }

        private bool IsControllerTabVisible(ManageAchievementsTab tab)
        {
            if (_viewModel == null)
            {
                return false;
            }

            switch (tab)
            {
                case ManageAchievementsTab.ManualTracking:
                    return _viewModel.ShowManualTrackingTab;
                case ManageAchievementsTab.Capstones:
                case ManageAchievementsTab.Category:
                case ManageAchievementsTab.Filters:
                case ManageAchievementsTab.Notes:
                case ManageAchievementsTab.AchievementOrder:
                case ManageAchievementsTab.CustomIcons:
                    return _viewModel.HasCapstoneData;
                default:
                    return true;
            }
        }

        private void QueueFocusSelectedTab()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                var tabButton = FindVisualChildren<RadioButton>(this)
                    .FirstOrDefault(button =>
                        button.Visibility == Visibility.Visible &&
                        button.IsChecked == true);
                if (tabButton != null)
                {
                    tabButton.Focus();
                    Keyboard.Focus(tabButton);
                }
            }), DispatcherPriority.Input);
        }

        private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root)
            where T : DependencyObject
        {
            if (root == null)
            {
                yield break;
            }

            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T typed)
                {
                    yield return typed;
                }

                foreach (var nested in FindVisualChildren<T>(child))
                {
                    yield return nested;
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

            _capstoneControl = new ManageAchievementsCapstonesTab(
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

            _manualControl = new ManageAchievementsManualTrackingTab(_manualViewModel);
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

            _achievementOrderViewModel = new ManageAchievementsAchievementOrderViewModel(
                _viewModel.GameId,
                _achievementOverridesService,
                _gameDataSnapshotProvider,
                _settings,
                _logger);
            _achievementOrderControl = new ManageAchievementsAchievementOrderTab(_achievementOrderViewModel);
            AchievementOrderHost.Content = _achievementOrderControl;
        }

        private void EnsureCategoryControl(bool forceRecreate)
        {
            if (_categoryControl != null && !forceRecreate)
            {
                return;
            }

            CleanupCategory();

            _categoryViewModel = new ManageAchievementsCategoryViewModel(
                _viewModel.GameId,
                _achievementOverridesService,
                _gameDataSnapshotProvider,
                _settings,
                _logger);
            _categoryControl = new ManageAchievementsCategoryTab(_categoryViewModel);
            CategoryHost.Content = _categoryControl;
        }

        private void EnsureFiltersControl(bool forceRecreate)
        {
            if (_filtersControl != null && !forceRecreate)
            {
                return;
            }

            CleanupFilters();

            _filtersViewModel = new ManageAchievementsFiltersViewModel(
                _viewModel.GameId,
                _achievementOverridesService,
                _gameDataSnapshotProvider,
                _settings,
                _logger);
            _filtersControl = new ManageAchievementsFiltersTab(_filtersViewModel);
            FiltersHost.Content = _filtersControl;
        }

        private void EnsureNotesControl(bool forceRecreate)
        {
            if (_notesControl != null && !forceRecreate)
            {
                return;
            }

            CleanupNotes();

            _notesViewModel = new ManageAchievementsNotesViewModel(
                _viewModel.GameId,
                _achievementOverridesService,
                _gameDataSnapshotProvider,
                _settings,
                _logger);
            _notesControl = new ManageAchievementsNotesTab(_notesViewModel);
            NotesHost.Content = _notesControl;
        }

        private void EnsureAchievementIconsControl(bool forceRecreate)
        {
            if (_achievementIconsControl != null && !forceRecreate)
            {
                return;
            }

            CleanupAchievementIcons();

            _achievementIconsViewModel = new ManageAchievementsAchievementIconsViewModel(
                _viewModel.GameId,
                _achievementOverridesService,
                _gameDataSnapshotProvider,
                PlayniteAchievementsPlugin.Instance?.ManagedCustomIconService,
                _settings,
                _logger);
            _achievementIconsControl = new ManageAchievementsAchievementIconsTab(_achievementIconsViewModel);
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
            _filtersRefreshPending = true;
            _notesRefreshPending = true;
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
            _filtersRefreshPending = true;
            _notesRefreshPending = true;
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

        private void CleanupFilters()
        {
            _filtersControl = null;
            _filtersViewModel = null;

            if (FiltersHost != null)
            {
                FiltersHost.Content = null;
            }
        }

        private void CleanupNotes()
        {
            _notesControl = null;
            _notesViewModel = null;

            if (NotesHost != null)
            {
                NotesHost.Content = null;
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
                _logger?.Warn(ex, "Failed to persist settings from Manage Achievements view.");
            }
        }
    }
}




