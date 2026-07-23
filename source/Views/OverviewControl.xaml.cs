using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Playnite.SDK;
using Playnite.SDK.Events;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.UI;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.Views.Helpers;

namespace PlayniteAchievements.Views
{
    public partial class OverviewControl : UserControl, IDisposable, IFullscreenControllerNavigable
    {
        private readonly OverviewViewModel _viewModel;
        private readonly ILogger _logger;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly RefreshRuntime _refreshService;
        private readonly ICacheManager _cacheManager;
        private readonly Action _persistSettingsForUi;
        private readonly AchievementOverridesService _achievementOverridesService;
        private readonly AchievementDataService _achievementDataService;
        private readonly IPlayniteAPI _playniteApi;
        private const double OverviewColumnRatioChangeThreshold = 0.001d;
        private bool _isActive;
        private Guid? _lastSelectedOverviewGameId;
        private DataGridRow _pendingRightClickRow;
        private bool _committingOverviewSelection;
        private DataGrid GameSummariesGrid => GameSummariesGridControl?.InternalDataGrid;

        // Column persistence state (for GameSummariesGrid only - AchievementDataGridControl handles its own)
        private readonly Dictionary<DataGridColumn, EventHandler> _columnWidthChangedHandlers = new Dictionary<DataGridColumn, EventHandler>();
        private readonly Dictionary<string, double> _pendingOverviewWidthUpdates = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        private DispatcherTimer _saveTimer;
        private bool _isApplyingWidths;
        private bool _isResizeInProgress;
        private string _lastOverviewResizedKey;

        private static readonly IReadOnlyDictionary<string, double> DefaultAchievementWidthSeeds =
            new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                ["Achievement"] = 520,
                ["UnlockDate"] = 230,
                ["CategoryType"] = 200,
                ["CategoryLabel"] = 200,
                ["Rarity"] = 170,
                ["Points"] = 120
            };

        private static readonly IReadOnlyDictionary<string, double> DefaultOverviewWidthSeeds =
            new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                ["OverviewGameName"] = 500,
                ["OverviewLastPlayed"] = 240,
                ["OverviewPlaytime"] = 170,
                ["OverviewProgression"] = 360,
                ["TotalAchievements"] = 180,
                ["GameSummaryCollectionScore"] = 180,
                ["GameSummaryPrestigeScore"] = 180,
                ["GameSummaryPoints"] = 140
            };

        public OverviewControl()
        {
            InitializeComponent();
            InitSaveTimer();
        }

        public OverviewControl(
            IPlayniteAPI api,
            ILogger logger,
            RefreshRuntime refreshRuntime,
            ICacheManager cacheManager,
            Action persistSettingsForUi,
            AchievementOverridesService achievementOverridesService,
            AchievementDataService achievementDataService,
            GameCustomDataStore gameCustomDataStore,
            RefreshEntryPoint refreshEntryPoint,
            PlayniteAchievementsSettings settings)
        {
            InitializeComponent();
            InitSaveTimer();

            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _settings = settings;
            _refreshService = refreshRuntime ?? throw new ArgumentNullException(nameof(refreshRuntime));
            _cacheManager = cacheManager ?? throw new ArgumentNullException(nameof(cacheManager));
            _persistSettingsForUi = persistSettingsForUi ?? throw new ArgumentNullException(nameof(persistSettingsForUi));
            _achievementOverridesService = achievementOverridesService ?? throw new ArgumentNullException(nameof(achievementOverridesService));
            _achievementDataService = achievementDataService ?? throw new ArgumentNullException(nameof(achievementDataService));
            _playniteApi = api ?? throw new ArgumentNullException(nameof(api));

            _viewModel = new OverviewViewModel(
                refreshRuntime,
                _persistSettingsForUi,
                _achievementDataService,
                gameCustomDataStore,
                refreshEntryPoint ?? throw new ArgumentNullException(nameof(refreshEntryPoint)),
                api,
                logger,
                settings);
            DataContext = _viewModel;
            _viewModel.PropertyChanged += ViewModel_PropertyChanged;
            _viewModel.SetActive(false);
            PlayniteAchievementsPlugin.SettingsSaved += Plugin_SettingsSaved;
        }

        private void ScoreCard_InfoRequested(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            ScoreInfoDialogPresenter.Show();
        }

        private void InitSaveTimer()
        {
            _saveTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(350)
            };
            _saveTimer.Tick += SaveTimer_Tick;
        }

        public void Activate()
        {
            if (_isActive) return;
            _isActive = true;
            _viewModel?.SetActive(true);

            Dispatcher.BeginInvoke(new Action(() =>
            {
                GameSummariesGridControl?.Refresh();
                RecentAchievementsDataGrid?.Refresh();
                SidebarAllAchievementsDataGrid?.Refresh();
                GameAchievementsGrid?.Refresh();
                ResetOverviewSortDirection();
                ResetAchievementsSortDirection();
                ResetRecentAchievementsSortDirection();
                ResetSidebarAllAchievementsSortDirection();
            }), DispatcherPriority.Loaded);

            FocusInitialFullscreenControllerTarget();
        }

        private void FocusInitialFullscreenControllerTarget()
        {
            try
            {
                if (_playniteApi?.ApplicationInfo?.Mode != ApplicationMode.Fullscreen)
                {
                    return;
                }

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (!_isActive || !IsVisible)
                    {
                        return;
                    }

                    if (!FocusLeftFilterArea())
                    {
                        FocusOverviewGrid();
                    }
                }), DispatcherPriority.Input);
            }
            catch
            {
                // Focus seeding is best-effort; activation should not fail if Playnite state is unavailable.
            }
        }

        public void Deactivate()
        {
            if (!_isActive) return;
            FlushPendingUpdates();
            _isActive = false;
            _viewModel?.SetActive(false);
        }

        public void RefreshView()
        {
            _ = _viewModel?.RefreshViewAsync();
        }

        public void Dispose()
        {
            try
            {
                Deactivate();
                if (_viewModel != null)
                {
                    _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
                }
                PlayniteAchievementsPlugin.SettingsSaved -= Plugin_SettingsSaved;
                FlushPendingUpdates();
                DetachAllHandlers();
                GameSummariesGridControl?.Dispose();
                RecentAchievementsDataGrid?.Dispose();
                SidebarAllAchievementsDataGrid?.Dispose();
                GameAchievementsGrid?.Dispose();
                _viewModel?.Dispose();
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "OverviewControl dispose failed.");
            }
        }

        #region Event Handlers

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            ApplyOverviewColumnRatio();
            GameSummariesGridControl?.Refresh();
            RecentAchievementsDataGrid?.Refresh();
            SidebarAllAchievementsDataGrid?.Refresh();
            GameAchievementsGrid?.Refresh();
            ResetOverviewSortDirection();
            ResetAchievementsSortDirection();
            ResetRecentAchievementsSortDirection();
            ResetSidebarAllAchievementsSortDirection();
            UpdatePieChartLayout();
        }

        private void AttachGameSummaryAlignmentProviders()
        {
            if (GameSummariesGrid == null || _settings?.Persisted == null)
            {
                return;
            }

            DataGridAlignmentBehavior.SetColumnCellAlignmentOverridesProvider(
                GameSummariesGrid,
                () => _settings.Persisted.OverviewGameSummariesColumnAlignments);
            DataGridAlignmentBehavior.SetColumnCellVerticalAlignmentOverridesProvider(
                GameSummariesGrid,
                () => _settings.Persisted.OverviewGameSummariesColumnVerticalAlignments);
            DataGridAlignmentBehavior.SetColumnHeaderHorizontalAlignmentOverridesProvider(
                GameSummariesGrid,
                () => _settings.Persisted.OverviewGameSummariesColumnHeaderAlignments);
            DataGridAlignmentBehavior.Refresh(GameSummariesGrid);
        }
        private void Plugin_SettingsSaved(object sender, EventArgs e)
        {
            ResetOverviewSortDirection();
            ResetAchievementsSortDirection();
            ResetRecentAchievementsSortDirection();
            ResetSidebarAllAchievementsSortDirection();
        }

        private void ViewModel_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (_viewModel == null || e == null) return;

            if (e.PropertyName == nameof(OverviewViewModel.SelectedGameHasCustomAchievementOrder))
            {
                ResetAchievementsSortDirection();
                return;
            }

            if (e.PropertyName == nameof(OverviewViewModel.OverviewSortPath) ||
                e.PropertyName == nameof(OverviewViewModel.OverviewSortDirection))
            {
                ResetOverviewSortDirection();
                return;
            }

            if (e.PropertyName == nameof(OverviewViewModel.ShowOverviewGamesPieChart)
                || e.PropertyName == nameof(OverviewViewModel.ShowOverviewProviderPieChart)
                || e.PropertyName == nameof(OverviewViewModel.ShowOverviewRarityPieChart)
                || e.PropertyName == nameof(OverviewViewModel.ShowOverviewTrophyPieChart)
                || e.PropertyName == nameof(OverviewViewModel.ShowOverviewPieCharts)
                || e.PropertyName == nameof(OverviewViewModel.ShowOverviewBarCharts))
            {
                UpdatePieChartLayout();
            }

            if (e.PropertyName != nameof(OverviewViewModel.IsGameSelected) &&
                e.PropertyName != nameof(OverviewViewModel.IsSelectedGameContentReady)) return;

            // Defer grid operations to Render priority to batch with visibility change
            // This prevents flicker by ensuring all layout happens in one render pass
            Dispatcher.BeginInvoke(new Action(() =>
            {
                ResetAchievementsSortDirection();

                if (TryApplyPendingToggleWidths()) return;
                QueueActiveGridNormalization(rescaleAll: false);
            }), DispatcherPriority.Render);
        }

        private void SaveTimer_Tick(object sender, EventArgs e)
        {
            _saveTimer?.Stop();
            var shouldNormalizeOverview = _pendingOverviewWidthUpdates.Count > 0;
            FlushPendingUpdates();

            if (_isResizeInProgress) return;

            if (shouldNormalizeOverview)
            {
                NormalizeGridColumns(GameSummariesGrid);
            }
        }

        private void ClearLeftSearch_Click(object sender, RoutedEventArgs e) => _viewModel?.ClearLeftSearch();
        private void ClearRightSearch_Click(object sender, RoutedEventArgs e) => _viewModel?.ClearRightSearch();

        private void DefaultSortModeButton_Click(object sender, RoutedEventArgs e)
        {
            if (_viewModel == null || !(sender is Button button))
            {
                return;
            }

            var menu = button.ContextMenu;
            if (menu == null)
            {
                return;
            }

            var itemStyle = button.TryFindResource("AchievementMultiSelectMenuItemStyle") as Style;
            var current = _viewModel.DefaultAchievementSortMode;

            var modes = new[]
            {
                (Mode: CompactListSortMode.None,         Key: "LOCPlayAch_Common_Default",              Fallback: "Default"),
                (Mode: CompactListSortMode.DisplayOrder, Key: "LOCPlayAch_SortMode_RetroAchievements",  Fallback: "RetroAchievements"),
                (Mode: CompactListSortMode.Custom,       Key: "LOCPlayAch_SortMode_CustomManual",       Fallback: "Custom (Manual)"),
            };

            menu.Items.Clear();
            foreach (var (mode, locKey, fallback) in modes)
            {
                var label = ResourceProvider.GetString(locKey) ?? fallback;
                var item = new MenuItem
                {
                    Header = label,
                    IsChecked = (mode == current),
                    Style = itemStyle,
                };
                var capturedMode = mode;
                item.Click += (s, _) => _viewModel.DefaultAchievementSortMode = capturedMode;
                menu.Items.Add(item);
            }

            OpenSelectorContextMenu(button, menu);
        }

        private void RefreshModeSelectionButton_Click(object sender, RoutedEventArgs e)
        {
            if (_viewModel == null)
            {
                return;
            }

            OpenSingleSelectRefreshModeContextMenu(
                RefreshModeSelectionButton,
                _viewModel.RefreshModes,
                _viewModel.SelectedRefreshMode,
                selectedKey => _viewModel.SelectedRefreshMode = selectedKey);
        }

        private void ToggleDefaultAchievementSortDescending_Click(object sender, RoutedEventArgs e)
        {
            if (_viewModel == null)
            {
                return;
            }

            _viewModel.DefaultAchievementSortDescending = !_viewModel.DefaultAchievementSortDescending;
        }

        private void ConfigureDefaultAchievementSort_Click(object sender, RoutedEventArgs e)
        {
            if (_settings == null)
            {
                return;
            }

            if (ManualAchievementSortDialog.TryShowDialog(_settings, _persistSettingsForUi))
            {
                _persistSettingsForUi?.Invoke();
                _viewModel?.RefreshManualSortSettings();
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    ResetOverviewSortDirection();
                    ResetAchievementsSortDirection();
                    ResetRecentAchievementsSortDirection();
                    ResetSidebarAllAchievementsSortDirection();
                    QueueActiveGridNormalization(rescaleAll: true);
                }), DispatcherPriority.Render);
            }
        }

        private static void OpenSingleSelectRefreshModeContextMenu(
            Button button,
            IEnumerable<RefreshMode> modes,
            string selectedModeKey,
            Action<string> setSelection)
        {
            if (button == null || setSelection == null)
            {
                return;
            }

            var menu = button.ContextMenu;
            if (menu == null)
            {
                return;
            }

            menu.Items.Clear();
            if (modes == null)
            {
                return;
            }

            var itemStyle = button.TryFindResource("AchievementMultiSelectMenuItemStyle") as Style;
            var submenuStyle = button.TryFindResource("AchievementCascadeMenuItemStyle") as Style;
            var availableModes = modes
                .Where(mode => mode != null && !string.IsNullOrWhiteSpace(mode.Key))
                .ToList();

            foreach (var mode in availableModes.Where(mode => !IsPresetRefreshMode(mode)))
            {
                menu.Items.Add(CreateRefreshModeMenuItem(mode, selectedModeKey, setSelection, itemStyle));
            }

            var presetModes = availableModes.Where(IsPresetRefreshMode).ToList();
            if (presetModes.Count > 0)
            {
                var presetsMenuItem = new MenuItem
                {
                    Header = ResourceProvider.GetString("LOCPlayAch_CustomRefresh_PresetsHeader") ?? "Presets",
                    ItemContainerStyle = itemStyle,
                    Style = submenuStyle
                };

                foreach (var presetMode in presetModes)
                {
                    presetsMenuItem.Items.Add(CreateRefreshModeMenuItem(presetMode, selectedModeKey, setSelection, itemStyle));
                }

                menu.Items.Add(presetsMenuItem);
            }

            if (menu.Items.Count == 0)
            {
                return;
            }

            OpenSelectorContextMenu(button, menu);
        }

        private static bool IsPresetRefreshMode(RefreshMode mode)
        {
            return mode != null
                && !string.IsNullOrWhiteSpace(mode.Key)
                && mode.Key.StartsWith("CustomPreset:", StringComparison.Ordinal);
        }

        private static MenuItem CreateRefreshModeMenuItem(
            RefreshMode mode,
            string selectedModeKey,
            Action<string> setSelection,
            Style itemStyle)
        {
            var modeKey = mode.Key;
            var item = new MenuItem
            {
                Header = !string.IsNullOrWhiteSpace(mode.ShortDisplayName)
                    ? mode.ShortDisplayName
                    : (!string.IsNullOrWhiteSpace(mode.DisplayName) ? mode.DisplayName : modeKey),
                IsCheckable = true,
                IsChecked = string.Equals(modeKey, selectedModeKey, StringComparison.Ordinal)
            };

            if (itemStyle != null)
            {
                item.Style = itemStyle;
            }

            item.Click += (_, __) => setSelection(modeKey);
            return item;
        }

        private void ProviderFilterSelectionButton_Click(object sender, RoutedEventArgs e)
        {
            if (_viewModel == null)
            {
                return;
            }

            OpenMultiSelectFilterContextMenu(
                ProviderFilterSelectionButton,
                _viewModel.ProviderFilterOptions,
                option => _viewModel.IsProviderFilterSelected(option),
                (option, isSelected) => _viewModel.SetProviderFilterSelected(option, isSelected),
                option => _viewModel.GetProviderFilterDisplayName(option));
        }

        private void CompletenessFilterSelectionButton_Click(object sender, RoutedEventArgs e)
        {
            if (_viewModel == null)
            {
                return;
            }

            OpenMultiSelectFilterContextMenu(
                CompletenessFilterSelectionButton,
                _viewModel.CompletenessFilterOptions,
                option => _viewModel.IsCompletenessFilterSelected(option),
                (option, isSelected) => _viewModel.SetCompletenessFilterSelected(option, isSelected));
        }

        private void PlayStatusFilterSelectionButton_Click(object sender, RoutedEventArgs e)
        {
            if (_viewModel == null)
            {
                return;
            }

            OpenMultiSelectFilterContextMenu(
                PlayStatusFilterSelectionButton,
                _viewModel.PlayStatusFilterOptions,
                option => _viewModel.IsPlayStatusFilterSelected(option),
                (option, isSelected) => _viewModel.SetPlayStatusFilterSelected(option, isSelected));
        }

        private void SelectedGameTypeFilterSelectionButton_Click(object sender, RoutedEventArgs e)
        {
            if (_viewModel == null)
            {
                return;
            }

            OpenMultiSelectFilterContextMenu(
                SelectedGameTypeFilterSelectionButton,
                _viewModel.SelectedGameTypeFilterOptions,
                option => _viewModel.IsSelectedGameTypeFilterSelected(option),
                (option, isSelected) => _viewModel.SetSelectedGameTypeFilterSelected(option, isSelected));
        }

        private void SelectedGameCategoryFilterSelectionButton_Click(object sender, RoutedEventArgs e)
        {
            if (_viewModel == null)
            {
                return;
            }

            OpenMultiSelectFilterContextMenu(
                SelectedGameCategoryFilterSelectionButton,
                _viewModel.SelectedGameCategoryFilterOptions,
                option => _viewModel.IsSelectedGameCategoryFilterSelected(option),
                (option, isSelected) => _viewModel.SetSelectedGameCategoryFilterSelected(option, isSelected));
        }

        private void OpenMultiSelectFilterContextMenu(
            Button button,
            IEnumerable<string> options,
            Func<string, bool> isSelected,
            Action<string, bool> setSelection,
            Func<string, string> getDisplayLabel = null)
        {
            if (button == null || isSelected == null || setSelection == null)
            {
                return;
            }

            var menu = button.ContextMenu;
            if (menu == null)
            {
                return;
            }

            menu.Items.Clear();
            if (options == null)
            {
                return;
            }

            var itemStyle = button.TryFindResource("AchievementMultiSelectMenuItemStyle") as Style;
            foreach (var option in options.Where(value => !string.IsNullOrWhiteSpace(value)))
            {
                var displayLabel = getDisplayLabel?.Invoke(option);
                if (string.IsNullOrWhiteSpace(displayLabel))
                {
                    displayLabel = option;
                }

                var item = new MenuItem
                {
                    Header = displayLabel,
                    IsCheckable = true,
                    StaysOpenOnClick = true,
                    IsChecked = isSelected(option)
                };
                if (itemStyle != null)
                {
                    item.Style = itemStyle;
                }
                item.Click += (_, __) => setSelection(option, item.IsChecked);
                menu.Items.Add(item);
            }

            if (menu.Items.Count == 0)
            {
                return;
            }

            OpenSelectorContextMenu(button, menu);
        }

        private static void OpenSelectorContextMenu(Button button, ContextMenu menu)
        {
            if (button == null || menu == null)
            {
                return;
            }

            RoutedEventHandler onClosed = null;
            onClosed = (_, __) =>
            {
                menu.Closed -= onClosed;
                button.ReleaseMouseCapture();
            };

            menu.Closed += onClosed;
            menu.PlacementTarget = button;
            menu.Placement = PlacementMode.Bottom;
            menu.HorizontalOffset = 0;
            menu.VerticalOffset = 0;
            if (button.IsKeyboardFocusWithin)
            {
                FullscreenControllerNavigationService.OpenContextMenu(button, menu);
            }
            else
            {
                menu.IsOpen = true;
            }
        }

        private void ClearGameSelection_Click(object sender, RoutedEventArgs e)
        {
            if (_viewModel?.IsGameSelected == true)
            {
                PrecomputeToggleWidths(toGameSelected: false);
            }
            _viewModel?.ClearGameSelection();
            _lastSelectedOverviewGameId = null;
        }

        public bool HandleFullscreenControllerInput(ControllerInput input)
        {
            if (_viewModel == null)
            {
                return false;
            }

            if (FullscreenControllerNavigationService.IsBackInput(input))
            {
                return TryHandleControllerBack();
            }

            if (FullscreenControllerNavigationService.IsSecondaryClickInput(input))
            {
                return TryHandleControllerSecondaryClick();
            }

            if (FullscreenControllerNavigationService.IsAcceptInput(input))
            {
                return TryHandleControllerActivation();
            }

            if (input == ControllerInput.DPadUp || input == ControllerInput.LeftStickUp)
            {
                return TryHandleControllerUp();
            }

            if (input == ControllerInput.DPadDown || input == ControllerInput.LeftStickDown)
            {
                return TryHandleControllerDown();
            }

            if (input == ControllerInput.DPadLeft || input == ControllerInput.LeftStickLeft)
            {
                return TryHandleControllerLeft();
            }

            if (input == ControllerInput.DPadRight || input == ControllerInput.LeftStickRight)
            {
                return TryHandleControllerRight();
            }

            return false;
        }

        private bool TryHandleControllerUp()
        {
            var focusedGrid = GetFocusedSidebarGrid();
            if (focusedGrid != null)
            {
                if (IsGridColumnHeaderFocused(focusedGrid))
                {
                    return FocusFilterAreaForGrid(focusedGrid);
                }

                if (IsGridAtFirstRow(focusedGrid))
                {
                    return FocusColumnHeaderForGrid(focusedGrid) || FocusFilterAreaForGrid(focusedGrid);
                }
            }

            return false;
        }

        private bool TryHandleControllerDown()
        {
            var focusedGrid = GetFocusedSidebarGrid();
            if (focusedGrid != null && IsGridColumnHeaderFocused(focusedGrid))
            {
                return FullscreenControllerNavigationService.FocusDataGrid(focusedGrid);
            }

            if (IsKeyboardFocusWithinLeftFilterArea())
            {
                return FocusColumnHeaderForGrid(GameSummariesGrid) || FocusOverviewGrid();
            }
            if (IsKeyboardFocusWithinRightFilterArea())
            {
                var grid = GetActiveRightGrid();
                return FocusColumnHeaderForGrid(grid) || FocusActiveRightGrid();
            }
            return false;
        }

        private bool TryHandleControllerLeft()
        {
            var focusedGrid = GetFocusedSidebarGrid();
            if (focusedGrid != null)
            {
                if (ReferenceEquals(focusedGrid, RecentAchievementsDataGrid?.InternalDataGrid) ||
                    ReferenceEquals(focusedGrid, GameAchievementsGrid?.InternalDataGrid))
                {
                    return FocusOverviewGrid(focusedGrid.SelectedIndex);
                }
            }

            if (IsKeyboardFocusWithinRightFilterArea())
            {
                if (FocusFilterElementByDelta(GetRightFilterControllerElements(), -1))
                {
                    return true;
                }

                return FocusLeftFilterArea(preferLast: true);
            }

            if (IsKeyboardFocusWithinLeftFilterArea())
            {
                return FocusFilterElementByDelta(GetLeftFilterControllerElements(), -1);
            }

            return false;
        }

        private bool TryHandleControllerRight()
        {
            var focusedGrid = GetFocusedSidebarGrid();
            if (focusedGrid != null)
            {
                if (ReferenceEquals(focusedGrid, GameSummariesGrid))
                {
                    return FocusActiveRightGrid(focusedGrid.SelectedIndex);
                }
            }

            if (IsKeyboardFocusWithinLeftFilterArea())
            {
                if (FocusFilterElementByDelta(GetLeftFilterControllerElements(), 1))
                {
                    return true;
                }

                return FocusRightFilterArea();
            }

            if (IsKeyboardFocusWithinRightFilterArea())
            {
                return FocusFilterElementByDelta(GetRightFilterControllerElements(), 1);
            }

            return false;
        }

        private bool TryHandleControllerBack()
        {
            var command = _viewModel?.CloseViewCommand;
            if (command == null || !command.CanExecute(null))
            {
                return false;
            }

            command.Execute(null);
            return true;
        }

        private bool TryHandleControllerActivation()
        {
            var focusedGrid = GetFocusedSidebarGrid();
            if (IsGridColumnHeaderFocused(focusedGrid))
            {
                return ActivateFocusedGridColumnHeader(focusedGrid);
            }

            if (GameSummariesGrid?.IsKeyboardFocusWithin == true)
            {
                return TrySelectFocusedOverviewGame();
            }

            if (RecentAchievementsDataGrid?.IsKeyboardFocusWithin == true)
            {
                return RecentAchievementsDataGrid.ActivateSelectedItem();
            }

            if (GameAchievementsGrid?.IsKeyboardFocusWithin == true)
            {
                return GameAchievementsGrid.ActivateSelectedItem();
            }

            return FullscreenControllerNavigationService.ActivateFocusedElement();
        }

        private bool TryHandleControllerSecondaryClick()
        {
            if (TryOpenFocusedSelectorContextMenu())
            {
                return true;
            }

            var focusedGrid = GetFocusedSidebarGrid();
            if (focusedGrid == null)
            {
                return false;
            }

            if (IsGridColumnHeaderFocused(focusedGrid))
            {
                return TryOpenColumnVisibilityMenuForController(focusedGrid);
            }

            return TryOpenSelectedGridRowContextMenu(focusedGrid);
        }

        private bool FocusOverviewGrid(int? preferredIndex = null)
        {
            return FullscreenControllerNavigationService.FocusDataGrid(GameSummariesGrid, preferredIndex);
        }

        private bool FocusActiveRightGrid(int? preferredIndex = null)
        {
            var grid = GetActiveRightGrid();
            return grid != null && FullscreenControllerNavigationService.FocusDataGrid(grid, preferredIndex);
        }

        private DataGrid GetActiveRightGrid()
        {
            var control = (GameAchievementsGrid?.IsVisible == true && _viewModel?.IsSelectedGameContentReady == true)
                ? (object)GameAchievementsGrid
                : (object)RecentAchievementsDataGrid;

            return (control as Controls.AchievementDataGridControl)?.InternalDataGrid;
        }

        private bool FocusColumnHeaderForGrid(DataGrid grid)
        {
            return grid != null && FullscreenControllerNavigationService.FocusDataGridColumnHeader(grid);
        }

        private bool FocusFilterAreaForGrid(DataGrid grid)
        {
            if (ReferenceEquals(grid, GameSummariesGrid))
            {
                return FocusLeftFilterArea();
            }
            return FocusRightFilterArea();
        }

        private bool FocusLeftFilterArea(bool preferLast = false)
        {
            return FocusFilterArea(GetLeftFilterControllerElements(), preferLast);
        }

        private bool FocusRightFilterArea(bool preferLast = false)
        {
            return FocusFilterArea(GetRightFilterControllerElements(), preferLast);
        }

        private static bool FocusFilterArea(IList<UIElement> elements, bool preferLast)
        {
            if (elements == null || elements.Count == 0)
            {
                return false;
            }

            return FullscreenControllerNavigationService.FocusFirstElement(
                preferLast ? elements.Reverse().ToArray() : elements);
        }

        private static bool FocusFilterElementByDelta(IList<UIElement> elements, int delta)
        {
            return FullscreenControllerNavigationService.FocusElementByDelta(elements, delta);
        }

        private bool TryOpenSelectedGridRowContextMenu(DataGrid grid)
        {
            if (grid == null)
            {
                return false;
            }

            var row = FullscreenControllerNavigationService.GetTargetDataGridRow(grid);
            if (row == null)
            {
                return false;
            }

            return OpenContextMenuForRow(row, useControllerPlacement: true);
        }

        private bool TryOpenColumnVisibilityMenuForController(DataGrid grid)
        {
            if (grid == null || !IsGridColumnHeaderFocused(grid))
            {
                return false;
            }

            if (ReferenceEquals(grid, GameSummariesGrid))
            {
                var header = FullscreenControllerNavigationService.GetFocusedDataGridColumnHeader(grid);
                var menu = BuildColumnVisibilityMenu(grid, header?.Column);
                if (menu == null || menu.Items.Count == 0)
                {
                    return false;
                }

                return FullscreenControllerNavigationService.OpenContextMenu(header, menu);
            }

            if (ReferenceEquals(grid, RecentAchievementsDataGrid?.InternalDataGrid))
            {
                return RecentAchievementsDataGrid.OpenColumnVisibilityMenuForController();
            }

            if (ReferenceEquals(grid, GameAchievementsGrid?.InternalDataGrid))
            {
                return GameAchievementsGrid.OpenColumnVisibilityMenuForController();
            }

            return false;
        }

        private bool IsGridColumnHeaderFocused(DataGrid grid)
        {
            if (grid == null)
            {
                return false;
            }

            return FullscreenControllerNavigationService.IsFocusWithinDataGridColumnHeader(grid);
        }

        private bool ActivateFocusedGridColumnHeader(DataGrid grid)
        {
            if (ReferenceEquals(grid, GameSummariesGrid))
            {
                return FullscreenControllerNavigationService.ActivateFocusedDataGridColumnHeader(grid);
            }

            if (ReferenceEquals(grid, RecentAchievementsDataGrid?.InternalDataGrid))
            {
                return RecentAchievementsDataGrid.ActivateFocusedColumnHeaderForController();
            }

            if (ReferenceEquals(grid, GameAchievementsGrid?.InternalDataGrid))
            {
                return GameAchievementsGrid.ActivateFocusedColumnHeaderForController();
            }

            return false;
        }

        private bool TryOpenFocusedSelectorContextMenu()
        {
            var focusedButton = VisualTreeHelpers.FindVisualParent<Button>(
                                    Keyboard.FocusedElement as DependencyObject)
                                ?? Keyboard.FocusedElement as Button;
            if (focusedButton == null)
            {
                return false;
            }

            if (ReferenceEquals(focusedButton, RefreshModeSelectionButton))
            {
                RefreshModeSelectionButton_Click(focusedButton, new RoutedEventArgs());
                return RefreshModeSelectionButton.ContextMenu?.IsOpen == true;
            }

            if (ReferenceEquals(focusedButton, ProviderFilterSelectionButton))
            {
                ProviderFilterSelectionButton_Click(focusedButton, new RoutedEventArgs());
                return ProviderFilterSelectionButton.ContextMenu?.IsOpen == true;
            }

            if (ReferenceEquals(focusedButton, CompletenessFilterSelectionButton))
            {
                CompletenessFilterSelectionButton_Click(focusedButton, new RoutedEventArgs());
                return CompletenessFilterSelectionButton.ContextMenu?.IsOpen == true;
            }

            if (ReferenceEquals(focusedButton, PlayStatusFilterSelectionButton))
            {
                PlayStatusFilterSelectionButton_Click(focusedButton, new RoutedEventArgs());
                return PlayStatusFilterSelectionButton.ContextMenu?.IsOpen == true;
            }

            if (ReferenceEquals(focusedButton, SelectedGameTypeFilterSelectionButton))
            {
                SelectedGameTypeFilterSelectionButton_Click(focusedButton, new RoutedEventArgs());
                return SelectedGameTypeFilterSelectionButton.ContextMenu?.IsOpen == true;
            }

            if (ReferenceEquals(focusedButton, SelectedGameCategoryFilterSelectionButton))
            {
                SelectedGameCategoryFilterSelectionButton_Click(focusedButton, new RoutedEventArgs());
                return SelectedGameCategoryFilterSelectionButton.ContextMenu?.IsOpen == true;
            }

            return false;
        }

        private DataGrid GetFocusedSidebarGrid()
        {
            var focused = Keyboard.FocusedElement as DependencyObject;
            var focusedGrid = VisualTreeHelpers.FindVisualParent<DataGrid>(focused)
                              ?? focused as DataGrid;

            if (IsSidebarGrid(focusedGrid))
            {
                return focusedGrid;
            }

            if (GameSummariesGrid?.IsKeyboardFocusWithin == true)
            {
                return GameSummariesGrid;
            }

            if (RecentAchievementsDataGrid?.IsKeyboardFocusWithin == true)
            {
                return RecentAchievementsDataGrid.InternalDataGrid;
            }

            if (GameAchievementsGrid?.IsKeyboardFocusWithin == true)
            {
                return GameAchievementsGrid.InternalDataGrid;
            }

            return null;
        }

        private bool IsSidebarGrid(DataGrid grid)
        {
            return grid != null &&
                   (ReferenceEquals(grid, GameSummariesGrid) ||
                    ReferenceEquals(grid, RecentAchievementsDataGrid?.InternalDataGrid) ||
                    ReferenceEquals(grid, GameAchievementsGrid?.InternalDataGrid));
        }

        private static bool IsGridAtFirstRow(DataGrid grid)
        {
            if (grid?.Items == null || grid.Items.Count == 0)
            {
                return false;
            }

            var focusedRow = FullscreenControllerNavigationService.FindAncestor<DataGridRow>(
                Keyboard.FocusedElement as DependencyObject);
            if (focusedRow != null &&
                ReferenceEquals(ItemsControl.ItemsControlFromItemContainer(focusedRow), grid))
            {
                return focusedRow.GetIndex() <= 0;
            }

            return grid.SelectedIndex <= 0;
        }

        private bool IsKeyboardFocusWithinLeftPane()
        {
            return IsKeyboardFocusWithinLeftFilterArea() ||
                   GameSummariesGrid?.IsKeyboardFocusWithin == true;
        }

        private bool IsKeyboardFocusWithinRightPane()
        {
            return IsKeyboardFocusWithinRightFilterArea() ||
                   RecentAchievementsDataGrid?.IsKeyboardFocusWithin == true ||
                   GameAchievementsGrid?.IsKeyboardFocusWithin == true;
        }

        private bool IsKeyboardFocusWithinLeftFilterArea()
        {
            return LeftSearchTextBox?.IsKeyboardFocusWithin == true ||
                   ClearLeftSearchButton?.IsKeyboardFocusWithin == true ||
                   ProviderFilterSelectionButton?.IsKeyboardFocusWithin == true ||
                   CompletenessFilterSelectionButton?.IsKeyboardFocusWithin == true ||
                   PlayStatusFilterSelectionButton?.IsKeyboardFocusWithin == true;
        }

        private bool IsKeyboardFocusWithinRightFilterArea()
        {
            return RightSearchTextBox?.IsKeyboardFocusWithin == true ||
                   ClearRightSearchButton?.IsKeyboardFocusWithin == true ||
                   SelectedGameTypeFilterSelectionButton?.IsKeyboardFocusWithin == true ||
                   SelectedGameCategoryFilterSelectionButton?.IsKeyboardFocusWithin == true ||
                   SelectedGameUnlockedFilterCheckBox?.IsKeyboardFocusWithin == true ||
                   SelectedGameLockedFilterCheckBox?.IsKeyboardFocusWithin == true ||
                   SelectedGameHiddenFilterCheckBox?.IsKeyboardFocusWithin == true ||
                   ClearGameSelectionButton?.IsKeyboardFocusWithin == true;
        }

        private bool IsKeyboardFocusWithinHeaderArea()
        {
            return CloseViewButton?.IsKeyboardFocusWithin == true ||
                   RefreshModeSelectionButton?.IsKeyboardFocusWithin == true ||
                   RefreshActionButton?.IsKeyboardFocusWithin == true;
        }

        private bool TrySelectFocusedOverviewGame()
        {
            var item = GameSummariesGrid?.SelectedItem as GameSummaryItem
                       ?? GameSummariesGrid?.CurrentItem as GameSummaryItem;
            if (item == null)
            {
                return false;
            }

            if (IsCommittedOverviewGame(item))
            {
                return ClearCommittedOverviewGameSelection();
            }

            return CommitOverviewGameSelection(item);
        }

        private bool IsCommittedOverviewGame(GameSummaryItem item)
        {
            return item?.PlayniteGameId.HasValue == true &&
                   _viewModel?.SelectedGame?.PlayniteGameId.HasValue == true &&
                   item.PlayniteGameId.Value == _viewModel.SelectedGame.PlayniteGameId.Value;
        }

        private bool CommitOverviewGameSelection(GameSummaryItem item)
        {
            if (item == null || _viewModel == null)
            {
                return false;
            }

            var currentGameId = item.PlayniteGameId;
            var gameChanged = !_lastSelectedOverviewGameId.HasValue ||
                              currentGameId != _lastSelectedOverviewGameId.Value;
            var wasGameSelected = _viewModel.IsGameSelected;

            if (!wasGameSelected)
            {
                PrecomputeToggleWidths(toGameSelected: true);
            }

            _committingOverviewSelection = true;
            try
            {
                _viewModel.SelectedGame = item;
                _lastSelectedOverviewGameId = currentGameId;
            }
            finally
            {
                _committingOverviewSelection = false;
            }

            if (gameChanged)
            {
                ResetAchievementsSortDirection();
                ResetAchievementsScrollPosition();
            }

            return true;
        }

        private bool ClearCommittedOverviewGameSelection()
        {
            if (_viewModel == null)
            {
                return false;
            }

            if (_viewModel.IsGameSelected)
            {
                PrecomputeToggleWidths(toGameSelected: false);
            }

            _committingOverviewSelection = true;
            try
            {
                _viewModel.ClearGameSelection();
                _lastSelectedOverviewGameId = null;
            }
            finally
            {
                _committingOverviewSelection = false;
            }

            return true;
        }

        private IList<UIElement> GetLeftFilterControllerElements()
        {
            return GetVisibleControllerElements(
                LeftSearchTextBox,
                ClearLeftSearchButton,
                ProviderFilterSelectionButton,
                CompletenessFilterSelectionButton,
                PlayStatusFilterSelectionButton);
        }

        private IList<UIElement> GetRightFilterControllerElements()
        {
            return GetVisibleControllerElements(
                RightSearchTextBox,
                ClearRightSearchButton,
                SelectedGameTypeFilterSelectionButton,
                SelectedGameCategoryFilterSelectionButton,
                SelectedGameUnlockedFilterCheckBox,
                SelectedGameLockedFilterCheckBox,
                SelectedGameHiddenFilterCheckBox,
                ClearGameSelectionButton);
        }

        private static IList<UIElement> GetVisibleControllerElements(params UIElement[] elements)
        {
            return elements
                .Where(IsControllerElementAvailable)
                .ToList();
        }

        private static bool IsControllerElementAvailable(UIElement element)
        {
            if (element == null || !element.IsVisible || !element.IsEnabled)
            {
                return false;
            }

            if (element is Button button &&
                ReferenceEquals(button.Style, button.TryFindResource("ClearSearchButtonStyle")))
            {
                return !string.IsNullOrEmpty(button.Tag as string);
            }

            return true;
        }

        private void GameSummaries_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_viewModel == null) return;

            var grid = sender as DataGrid
                       ?? (sender as Controls.GameSummariesGridControl)?.InternalDataGrid
                       ?? GameSummariesGrid;
            if (grid == null) return;

            var hitTestResult = VisualTreeHelper.HitTest(grid, e.GetPosition(grid));
            if (hitTestResult == null) return;

            DependencyObject current = hitTestResult.VisualHit;
            while (current != null && !(current is DataGridRow))
            {
                current = VisualTreeHelper.GetParent(current);
            }

            if (current is DataGridRow row && row.IsSelected)
            {
                grid.SelectedItem = null;
                // Ensure the DataGrid clears any remaining selection state and keyboard focus
                try
                {
                    grid.UnselectAll();
                    Keyboard.ClearFocus();
                }
                catch
                {
                    // Best-effort: swallow any focus clearing errors to avoid breaking UI
                }
                _viewModel.ClearGameSelection();
                _lastSelectedOverviewGameId = null;
                e.Handled = true;
            }
        }

        private void GameSummaries_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_viewModel == null || !(sender is DataGrid grid)) return;

            if (grid.SelectedItem is GameSummaryItem item)
            {
                if (ShouldCommitOverviewSelectionFromSelectionChanged())
                {
                    CommitOverviewGameSelection(item);
                }
            }
            else
            {
                if (ShouldCommitOverviewSelectionFromSelectionChanged())
                {
                    _lastSelectedOverviewGameId = null;
                }
            }
        }

        private bool ShouldCommitOverviewSelectionFromSelectionChanged()
        {
            return !_committingOverviewSelection &&
                   (!IsFullscreenMode() ||
                    Mouse.LeftButton == MouseButtonState.Pressed);
        }

        private bool IsFullscreenMode()
        {
            try
            {
                return _playniteApi?.ApplicationInfo?.Mode == ApplicationMode.Fullscreen;
            }
            catch
            {
                return false;
            }
        }

        private void AchievementRow_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is DataGridRow row && row.DataContext is AchievementDisplayItem item)
            {
                _viewModel?.RevealAchievementCommand?.Execute(item);
            }
        }

        private void DataGridRow_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (TryResolveContextMenuRow(sender, e, out var row))
            {
                e.Handled = true;
                _pendingRightClickRow = row;
            }
        }

        private void DataGridRow_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (TryResolveContextMenuRow(sender, e, out var row))
            {
                e.Handled = true;
                var targetRow = _pendingRightClickRow ?? row;
                _pendingRightClickRow = null;
                Dispatcher.BeginInvoke(
                    new Action(() => OpenContextMenuForRow(targetRow)),
                    DispatcherPriority.ContextIdle);
            }
        }

        private static bool TryResolveContextMenuRow(object sender, MouseButtonEventArgs e, out DataGridRow row)
        {
            row = sender as DataGridRow
                  ?? e?.Source as DataGridRow
                  ?? VisualTreeHelpers.FindVisualParent<DataGridRow>(e?.OriginalSource as DependencyObject);
            return row != null;
        }

        private void DataGridColumnMenu_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!(sender is DataGrid grid)) return;

            var source = e.OriginalSource as DependencyObject;
            if (VisualTreeHelpers.FindVisualParent<DataGridRow>(source) != null)
            {
                return;
            }

            var header = VisualTreeHelpers.FindVisualParent<DataGridColumnHeader>(source);
            if (header?.Column == null) return;

            e.Handled = true;
            var menu = BuildColumnVisibilityMenu(grid, header.Column);
            if (menu == null || menu.Items.Count == 0) return;

            menu.Placement = PlacementMode.Bottom;
            menu.PlacementTarget = header;
            menu.HorizontalOffset = 0;
            menu.VerticalOffset = 0;
            menu.IsOpen = true;
        }

        private void DataGrid_Sorting(object sender, DataGridSortingEventArgs e)
        {
            if (_viewModel == null) return;
            var isAdditive = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control)
                == System.Windows.Input.ModifierKeys.Control;
            var sortDirection = DataGridSortingHelper.HandleSorting(sender, e, clearOtherColumns: !isAdditive);
            if (sortDirection == null) return;
            _viewModel.SortDataGrid((sender as DataGrid), e.Column.SortMemberPath, sortDirection.Value, isAdditive);
            ResetOverviewSortDirection();
        }

        private void GameAchievementsGrid_Sorting(object sender, DataGridSortingEventArgs e)
        {
            if (_viewModel == null) return;
            e.Handled = true;

            var grid = GameAchievementsGrid?.InternalDataGrid;
            if (grid == null) return;

            var isAdditive = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control)
                == System.Windows.Input.ModifierKeys.Control;
            if (isAdditive)
            {
                var additiveDirection = DataGridSortingHelper.HandleSorting(
                    sender,
                    e,
                    grid,
                    clearOtherColumns: false);
                if (additiveDirection == null) return;

                _viewModel.SortDataGrid(
                    grid,
                    e.Column.SortMemberPath,
                    additiveDirection.Value,
                    isAdditive: true);
                ResetAchievementsSortDirection();
                return;
            }

            var sortAction = AchievementSortHelper.ResolveGridSortAction(
                e.Column?.SortMemberPath,
                _viewModel.SelectedGameSortPath,
                _viewModel.SelectedGameSortDirection,
                _settings?.Persisted,
                AchievementSortSurface.OverviewSelectedGame,
                e.Column?.SortDirection);
            if (sortAction.Kind == AchievementGridSortActionKind.None)
            {
                return;
            }

            if (sortAction.Kind == AchievementGridSortActionKind.ResetToDefault)
            {
                _viewModel.ApplyDefaultSelectedGameSort();
                ClearAchievementsSortIndicators();
                return;
            }
            else if (sortAction.Direction.HasValue)
            {
                _viewModel.SortDataGrid(grid, sortAction.SortMemberPath, sortAction.Direction.Value);
            }

            ResetAchievementsSortDirection();
        }

        private void AchievementDataGrid_Sorting(object sender, DataGridSortingEventArgs e)
        {
            if (_viewModel == null) return;
            e.Handled = true;

            var control = sender as Controls.AchievementDataGridControl;
            var grid = control?.InternalDataGrid;
            if (grid == null) return;

            var isAdditive = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control)
                == System.Windows.Input.ModifierKeys.Control;
            if (isAdditive)
            {
                var additiveDirection = DataGridSortingHelper.HandleSorting(
                    sender,
                    e,
                    grid,
                    clearOtherColumns: false);
                if (additiveDirection == null) return;

                _viewModel.SortDataGrid(
                    grid,
                    e.Column.SortMemberPath,
                    additiveDirection.Value,
                    isAdditive: true);

                if (control == RecentAchievementsDataGrid)
                    ResetRecentAchievementsSortDirection();
                else if (control == SidebarAllAchievementsDataGrid)
                    ResetSidebarAllAchievementsSortDirection();
                return;
            }

            var isRecent = control == RecentAchievementsDataGrid;
            var currentSortPath = isRecent
                ? _viewModel.RecentSortPath
                : _viewModel.SidebarAllSortPath;
            var currentSortDirection = isRecent
                ? _viewModel.RecentSortDirection
                : _viewModel.SidebarAllSortDirection;
            var surface = isRecent
                ? AchievementSortSurface.OverviewRecentAchievements
                : AchievementSortSurface.AchievementDataGrid;

            var sortAction = AchievementSortHelper.ResolveGridSortAction(
                e.Column?.SortMemberPath,
                currentSortPath,
                currentSortDirection,
                _settings?.Persisted,
                surface,
                e.Column?.SortDirection);
            if (sortAction.Kind == AchievementGridSortActionKind.None)
            {
                return;
            }

            if (sortAction.Kind == AchievementGridSortActionKind.ResetToDefault)
            {
                if (isRecent)
                    _viewModel.ApplyDefaultRecentSort();
                else
                    _viewModel.ApplyDefaultSidebarAllSort();

                foreach (var column in grid.Columns)
                {
                    column.SortDirection = null;
                    SetColumnSortLevel(grid, column, null);
                }
                control.SetSortIndicator(null, null);
                return;
            }

            if (sortAction.Direction.HasValue)
            {
                _viewModel.SortDataGrid(
                    grid,
                    sortAction.SortMemberPath,
                    sortAction.Direction.Value,
                    isAdditive: false);
            }

            // Update sort level badges to reflect the new multi-column sort state
            if (isRecent)
                ResetRecentAchievementsSortDirection();
            else if (control == SidebarAllAchievementsDataGrid)
                ResetSidebarAllAchievementsSortDirection();
        }

        private void OnProviderPieChartSliceClick(object sender, string providerName)
        {
            _viewModel?.ToggleProviderFilterFromPieChart(providerName);
        }

        private void OnGamesPieChartSliceClick(object sender, string completenessLabel)
        {
            _viewModel?.ToggleCompletenessFilterFromPieChart(completenessLabel);
        }

        private void UpdatePieChartLayout()
        {
            if (_viewModel == null || OverviewPieChartsGrid == null) return;

            var panels = new List<(FrameworkElement Element, bool IsVisible)>
            {
                (GamesPieChartPanel, _viewModel.ShowOverviewGamesPieChart),
                (ProviderPieChartPanel, _viewModel.ShowOverviewProviderPieChart),
                (RarityPieChartPanel, _viewModel.ShowOverviewRarityPieChart),
                (TrophyPieChartPanel, _viewModel.ShowOverviewTrophyPieChart)
            };

            var visibleIndex = 0;
            foreach (var (element, isVisible) in panels)
            {
                if (element == null) continue;

                if (isVisible)
                {
                    element.Visibility = Visibility.Visible;
                    Grid.SetColumn(element, visibleIndex);
                    // Add spacing to all but last visible panel
                    element.Margin = new Thickness(0, 0, 8, 0);
                    visibleIndex++;
                }
                else
                {
                    element.Visibility = Visibility.Collapsed;
                }
            }

            // Remove margin from last visible panel
            if (visibleIndex > 0)
            {
                var lastVisible = panels.Where(p => p.IsVisible).Last().Element;
                lastVisible.Margin = new Thickness(0);
            }

            // Update column definitions: visible get star, hidden get 0
            for (var i = 0; i < OverviewPieChartsGrid.ColumnDefinitions.Count; i++)
            {
                OverviewPieChartsGrid.ColumnDefinitions[i].Width = i < visibleIndex
                    ? new GridLength(1, GridUnitType.Star)
                    : new GridLength(0);
            }

            // Update parent grid column widths based on visible pie count
            // Formula: pie_width = visible_pies * 0.5, bar_width = 1
            // This gives: 1 pie = 33%/67%, 2 pies = 50%/50%, 3 pies = 60%/40%, 4 pies = 67%/33%
            if (ChartsRowGrid != null && visibleIndex > 0)
            {
                var pieWidth = visibleIndex * 0.5;
                ChartsRowGrid.ColumnDefinitions[0].Width = new GridLength(pieWidth, GridUnitType.Star);
                ChartsRowGrid.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);
            }
        }

        #endregion

        #region Sidebar Layout Persistence

        private void ApplyOverviewColumnRatio()
        {
            var ratio = _settings?.Persisted?.OverviewLeftColumnRatio
                ?? PersistedSettings.DefaultOverviewLeftColumnRatio;

            SetOverviewColumnRatio(ratio);
        }

        private void OverviewGridSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(PersistOverviewColumnRatio), DispatcherPriority.Background);
        }

        private void PersistOverviewColumnRatio()
        {
            if (_settings?.Persisted == null || !TryGetOverviewColumnRatio(out var ratio))
            {
                return;
            }

            ratio = NormalizeOverviewColumnRatio(ratio);
            if (Math.Abs(_settings.Persisted.OverviewLeftColumnRatio - ratio) <= OverviewColumnRatioChangeThreshold)
            {
                SetOverviewColumnRatio(ratio);
                return;
            }

            _settings.Persisted.OverviewLeftColumnRatio = ratio;
            SetOverviewColumnRatio(_settings.Persisted.OverviewLeftColumnRatio);
            SaveSettings();
        }

        private bool TryGetOverviewColumnRatio(out double ratio)
        {
            ratio = PersistedSettings.DefaultOverviewLeftColumnRatio;

            var left = OverviewLeftColumn?.ActualWidth ?? 0;
            var right = OverviewRightColumn?.ActualWidth ?? 0;
            if (!ColumnWidthNormalization.IsValidWidth(left) || !ColumnWidthNormalization.IsValidWidth(right))
            {
                return false;
            }

            var combined = left + right;
            if (!ColumnWidthNormalization.IsValidWidth(combined))
            {
                return false;
            }

            ratio = left / combined;
            return IsValidOverviewColumnRatio(ratio);
        }

        private void SetOverviewColumnRatio(double ratio)
        {
            if (OverviewLeftColumn == null || OverviewRightColumn == null)
            {
                return;
            }

            ratio = NormalizeOverviewColumnRatio(ratio);
            OverviewLeftColumn.Width = new GridLength(ratio, GridUnitType.Star);
            OverviewRightColumn.Width = new GridLength(1d - ratio, GridUnitType.Star);
        }

        private static double NormalizeOverviewColumnRatio(double ratio)
        {
            if (!IsValidOverviewColumnRatio(ratio))
            {
                return PersistedSettings.DefaultOverviewLeftColumnRatio;
            }

            return Math.Max(
                PersistedSettings.MinOverviewLeftColumnRatio,
                Math.Min(PersistedSettings.MaxOverviewLeftColumnRatio, ratio));
        }

        private static bool IsValidOverviewColumnRatio(double ratio)
        {
            return !double.IsNaN(ratio) && !double.IsInfinity(ratio) && ratio > 0d && ratio < 1d;
        }

        #endregion

        #region Column Persistence

        private void AttachHandlers(DataGrid grid)
        {
            if (grid == null) return;

            foreach (var column in grid.Columns)
            {
                AttachWidthHandler(grid, column);
            }

            grid.Loaded += Grid_Loaded;
            grid.SizeChanged += Grid_SizeChanged;
            grid.PreviewMouseLeftButtonDown += Grid_PreviewMouseLeftButtonDown;
            grid.PreviewMouseLeftButtonUp += Grid_PreviewMouseLeftButtonUp;
            grid.LostMouseCapture += Grid_LostMouseCapture;
            grid.ColumnDisplayIndexChanged += GameSummariesGrid_ColumnDisplayIndexChanged;
        }

        private void DetachAllHandlers()
        {
            foreach (var pair in _columnWidthChangedHandlers.ToList())
            {
                var descriptor = System.ComponentModel.DependencyPropertyDescriptor
                    .FromProperty(DataGridColumn.WidthProperty, typeof(DataGridColumn));
                descriptor?.RemoveValueChanged(pair.Key, pair.Value);
            }
            _columnWidthChangedHandlers.Clear();

            DetachGridHandlers(GameSummariesGrid);
            DataGridAlignmentBehavior.SetColumnCellAlignmentOverridesProvider(GameSummariesGrid, null);
            DataGridAlignmentBehavior.SetColumnCellVerticalAlignmentOverridesProvider(GameSummariesGrid, null);
            DataGridAlignmentBehavior.SetColumnHeaderHorizontalAlignmentOverridesProvider(GameSummariesGrid, null);
            DetachGridHandlers(RecentAchievementsDataGrid.InternalDataGrid);
            // GameAchievementsGrid is managed by AchievementDataGridControl

            if (_saveTimer != null)
            {
                _saveTimer.Stop();
                _saveTimer.Tick -= SaveTimer_Tick;
                _saveTimer = null;
            }
        }

        private void DetachGridHandlers(DataGrid grid)
        {
            if (grid == null) return;
            grid.Loaded -= Grid_Loaded;
            grid.SizeChanged -= Grid_SizeChanged;
            grid.PreviewMouseLeftButtonDown -= Grid_PreviewMouseLeftButtonDown;
            grid.PreviewMouseLeftButtonUp -= Grid_PreviewMouseLeftButtonUp;
            grid.LostMouseCapture -= Grid_LostMouseCapture;
            grid.ColumnDisplayIndexChanged -= GameSummariesGrid_ColumnDisplayIndexChanged;
        }

        private void AttachWidthHandler(DataGrid grid, DataGridColumn column)
        {
            if (column == null || _columnWidthChangedHandlers.ContainsKey(column)) return;

            var descriptor = System.ComponentModel.DependencyPropertyDescriptor
                .FromProperty(DataGridColumn.WidthProperty, typeof(DataGridColumn));
            if (descriptor == null) return;

            EventHandler handler = (_, __) => OnColumnWidthChanged(grid, column);
            descriptor.AddValueChanged(column, handler);
            _columnWidthChangedHandlers[column] = handler;
        }

        private void OnColumnWidthChanged(DataGrid grid, DataGridColumn column)
        {
            if (_isApplyingWidths || !_isResizeInProgress || column == null || !column.CanUserResize) return;

            var key = ColumnWidthNormalization.GetColumnKey(column);
            if (string.IsNullOrWhiteSpace(key)) return;

            var width = column.ActualWidth;
            if (!ColumnWidthNormalization.IsValidWidth(width)) return;

            // RecentAchievementsDataGrid and GameAchievementsGrid handle their own column widths via ColumnWidthPersistenceService
            if (grid == GameSummariesGrid)
            {
                _lastOverviewResizedKey = key;
                QueueWidthUpdate(_pendingOverviewWidthUpdates, key, width);
            }
        }

        private void QueueWidthUpdate(Dictionary<string, double> map, string key, double width)
        {
            map[key] = ColumnWidthNormalization.RoundPixelWidth(width);
            _saveTimer?.Stop();
            _saveTimer?.Start();
        }

        private void FlushPendingUpdates()
        {
            if (_settings?.Persisted == null) return;

            var changed = false;
            // AchievementDataGridControl handles its own width persistence
            if (_pendingOverviewWidthUpdates.Count > 0)
            {
                changed |= FlushToMap(_settings.Persisted.OverviewGameSummariesColumnWidths, _pendingOverviewWidthUpdates);
            }

            if (changed) SaveSettings();
        }

        private bool FlushToMap(Dictionary<string, double> target, Dictionary<string, double> pending)
        {
            if (target == null || pending.Count == 0) return false;

            var changed = false;
            foreach (var update in pending)
            {
                if (!ColumnWidthNormalization.IsValidWidth(update.Value)) continue;
                if (!target.TryGetValue(update.Key, out var existing) || Math.Abs(existing - update.Value) > 0.1)
                {
                    target[update.Key] = update.Value;
                    changed = true;
                }
            }
            pending.Clear();
            return changed;
        }

        private void Grid_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is DataGrid grid)
            {
                NormalizeGridColumns(grid);
            }
        }

        private void Grid_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!e.WidthChanged || !(sender is DataGrid grid) || !grid.IsVisible || grid.ActualWidth <= 1) return;

            var isRecentAchievementsGrid = grid == RecentAchievementsDataGrid.InternalDataGrid;
            var isVisibilityActivation = e.PreviousSize.Width <= 1;
            if (isRecentAchievementsGrid && isVisibilityActivation && HasPendingToggleWidths()) return;

            grid.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (grid.IsLoaded && !_isResizeInProgress)
                {
                    if (isRecentAchievementsGrid)
                    {
                        NormalizeRecentAchievementColumns(grid);
                    }
                    else
                    {
                        NormalizeGridColumns(grid);
                    }
                }
            }), DispatcherPriority.Render);
        }

        private void Grid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (VisualTreeHelpers.IsColumnResizeThumbHit(e.OriginalSource as DependencyObject))
            {
                _isResizeInProgress = true;
            }
        }

        private void Grid_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            CompleteResizeNormalization(sender as DataGrid);
        }

        private void Grid_LostMouseCapture(object sender, MouseEventArgs e)
        {
            CompleteResizeNormalization(sender as DataGrid);
        }

        private void CompleteResizeNormalization(DataGrid grid)
        {
            if (!_isResizeInProgress) return;
            _isResizeInProgress = false;
            if (grid == null) return;
            grid.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (grid == RecentAchievementsDataGrid.InternalDataGrid)
                {
                    NormalizeRecentAchievementColumns(grid);
                }
                else
                {
                    NormalizeGridColumns(grid);
                }
            }), DispatcherPriority.Background);
        }

        #endregion

        #region Normalization

        private void NormalizeGridColumns(DataGrid grid, bool rescaleAll = false)
        {
            if (grid == null || !grid.IsLoaded) return;

            if (grid == RecentAchievementsDataGrid.InternalDataGrid)
            {
                NormalizeRecentAchievementColumns(grid, rescaleAll);
                return;
            }

            string protectedKey = grid == GameSummariesGrid ? _lastOverviewResizedKey : null;
            var preferredWidths = grid == GameSummariesGrid ? GetOverviewWidths() : null;

            if (ColumnWidthNormalization.TryBuildNormalizedWidths(grid, protectedKey, rescaleAll,
                preferredWidths, 0, out var normalized))
            {
                ColumnWidthNormalization.ApplyWidthsByKey(grid, normalized, ref _isApplyingWidths);
            }
        }

        private void NormalizeRecentAchievementColumns(DataGrid referenceGrid, bool rescaleAll = false)
        {
            // AchievementDataGridControl handles its own column normalization internally
            // This method is kept for compatibility but is now a no-op
        }

        private Dictionary<string, double> CaptureResizableWidths(DataGrid grid, double fallbackWidth)
        {
            var captured = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            if (grid == null || !grid.IsLoaded) return captured;

            var available = ColumnWidthNormalization.GetGridAvailableWidth(grid);
            if (!ColumnWidthNormalization.IsValidWidth(available))
            {
                available = fallbackWidth;
            }
            if (!ColumnWidthNormalization.IsValidWidth(available)) return captured;

            var minColWidth = ColumnWidthNormalization.ResolveResizableMinimumColumnWidth(
                grid.Columns.Where(c => c?.Visibility == Visibility.Visible).ToList(),
                ColumnWidthNormalization.GetContainerRelativeMinimumColumnWidth(available),
                available);

            foreach (var column in grid.Columns.Where(c => c?.Visibility == Visibility.Visible && c.CanUserResize))
            {
                var key = ColumnWidthNormalization.GetColumnKey(column);
                if (string.IsNullOrWhiteSpace(key)) continue;
                captured[key] = Math.Max(minColWidth, ColumnWidthNormalization.GetCurrentWidth(column));
            }

            return captured;
        }

        private void QueueActiveGridNormalization(bool rescaleAll)
        {
            // Only RecentAchievementsDataGrid needs manual normalization
            // GameAchievementsGrid uses AchievementDataGridControl with built-in persistence
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_viewModel == null) return;
                if (_viewModel.IsSelectedGameContentReady) return; // GameAchievementsGrid manages itself

                if (RecentAchievementsDataGrid.InternalDataGrid == null || !RecentAchievementsDataGrid.InternalDataGrid.IsLoaded ||
                    !RecentAchievementsDataGrid.InternalDataGrid.IsVisible || RecentAchievementsDataGrid.InternalDataGrid.ActualWidth <= 1) return;
                NormalizeRecentAchievementColumns(RecentAchievementsDataGrid.InternalDataGrid, rescaleAll);
            }), DispatcherPriority.Loaded);
        }

        #endregion

        #region Toggle Precomputation

        // RecentAchievementsDataGrid and GameAchievementsGrid now use AchievementDataGridControl
        // which handles its own column persistence. Toggle width handling is no longer needed.

        private bool HasPendingToggleWidths() => false;

        private bool TryApplyPendingToggleWidths() => false;

        private void PrecomputeToggleWidths(bool toGameSelected)
        {
            // No-op: AchievementDataGridControl handles its own column widths
        }

        #endregion

        #region Apply/Get Widths

        private void ApplyVisibilityToGrids()
        {
            // RecentAchievementsDataGrid and GameAchievementsGrid use AchievementDataGridControl which handles its own visibility
            ApplyVisibility(GameSummariesGrid, _settings?.Persisted?.OverviewGameSummariesColumnVisibility);
        }

        private void ApplyVisibility(DataGrid grid, Dictionary<string, bool> map)
        {
            if (grid == null || map == null) return;
            foreach (var column in grid.Columns)
            {
                var key = ColumnWidthNormalization.GetColumnKey(column);
                if (!string.IsNullOrWhiteSpace(key) && map.TryGetValue(key, out var isVisible))
                {
                    column.Visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;
                }
            }
        }

        private void ApplyWidthsToGrids()
        {
            // RecentAchievementsDataGrid and GameAchievementsGrid use AchievementDataGridControl which handles its own widths
            EnsureDefaultSeeds();
            ApplyWidths(GameSummariesGrid, GetOverviewWidths());
            NormalizeGridColumns(GameSummariesGrid);
            ApplyColumnOrderToGrid(GameSummariesGrid, _settings?.Persisted?.OverviewGameSummariesColumnOrder);
        }

        private void ApplyColumnOrderToGrid(DataGrid grid, Dictionary<string, int> orderMap)
        {
            if (grid == null || orderMap == null || orderMap.Count == 0) return;

            // Build a list of (column, desired displayIndex) pairs
            var assignments = new List<(DataGridColumn Column, int DesiredIndex)>();
            foreach (var column in grid.Columns)
            {
                var key = ColumnWidthNormalization.GetColumnKey(column);
                if (!string.IsNullOrWhiteSpace(key) && orderMap.TryGetValue(key, out var idx))
                {
                    assignments.Add((column, Math.Min(idx, grid.Columns.Count - 1)));
                }
            }

            if (assignments.Count == 0) return;

            // Apply display indices in ascending order to avoid conflicts
            foreach (var (column, desiredIndex) in assignments.OrderBy(a => a.DesiredIndex))
            {
                try
                {
                    if (column.DisplayIndex != desiredIndex)
                    {
                        column.DisplayIndex = desiredIndex;
                    }
                }
                catch { /* ignore invalid index transitions */ }
            }
        }

        private void GameSummariesGrid_ColumnDisplayIndexChanged(object sender, DataGridColumnEventArgs e)
        {
            if (_isApplyingWidths) return;

            var persisted = _settings?.Persisted;
            if (persisted == null || GameSummariesGrid == null) return;

            var orderMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var column in GameSummariesGrid.Columns)
            {
                var key = ColumnWidthNormalization.GetColumnKey(column);
                if (!string.IsNullOrWhiteSpace(key))
                {
                    orderMap[key] = column.DisplayIndex;
                }
            }

            persisted.OverviewGameSummariesColumnOrder = orderMap;
            _saveTimer?.Stop();
            _saveTimer?.Start();
        }

        private void ApplyWidths(DataGrid grid, Dictionary<string, double> map)
        {
            if (grid == null || map == null) return;

            _isApplyingWidths = true;
            try
            {
                foreach (var column in grid.Columns)
                {
                    if (column == null || !column.CanUserResize) continue;
                    var key = ColumnWidthNormalization.GetColumnKey(column);
                    if (string.IsNullOrWhiteSpace(key)) continue;
                    if (map.TryGetValue(key, out var width) && ColumnWidthNormalization.IsValidWidth(width))
                    {
                        column.Width = new DataGridLength(ColumnWidthNormalization.RoundPixelWidth(width), DataGridLengthUnitType.Pixel);
                    }
                }
            }
            finally
            {
                _isApplyingWidths = false;
            }
        }

        private void ApplyWidthToGridByKey(DataGrid grid, string key, double width)
        {
            if (grid == null || string.IsNullOrWhiteSpace(key) || !ColumnWidthNormalization.IsValidWidth(width)) return;

            _isApplyingWidths = true;
            try
            {
                foreach (var column in grid.Columns)
                {
                    if (column == null || !column.CanUserResize) continue;
                    var colKey = ColumnWidthNormalization.GetColumnKey(column);
                    var roundedWidth = ColumnWidthNormalization.RoundPixelWidth(width);
                    if (ColumnWidthNormalization.KeysEqual(colKey, key) && Math.Abs(column.ActualWidth - roundedWidth) > 0.1)
                    {
                        column.Width = new DataGridLength(roundedWidth, DataGridLengthUnitType.Pixel);
                    }
                }
            }
            finally
            {
                _isApplyingWidths = false;
            }
        }

        private Dictionary<string, double> GetAchievementWidths()
        {
            var merged = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

            // Legacy fallback
            var legacy = _settings?.Persisted?.DataGridColumnWidths;
            if (legacy != null)
            {
                foreach (var pair in legacy)
                {
                    if (ColumnWidthNormalization.IsValidWidth(pair.Value))
                        merged[pair.Key] = pair.Value;
                }
            }

            var current = _settings?.Persisted?.OverviewRecentAchievementColumnWidths;
            if (current != null)
            {
                foreach (var pair in current)
                {
                    if (ColumnWidthNormalization.IsValidWidth(pair.Value))
                        merged[pair.Key] = pair.Value;
                }
            }

            return merged;
        }

        private Dictionary<string, double> GetOverviewWidths()
        {
            return _settings?.Persisted?.OverviewGameSummariesColumnWidths
                ?? new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        }

        private void EnsureDefaultSeeds()
        {
            if (_settings?.Persisted == null) return;
            var changed = false;

            var achievementMap = _settings.Persisted.OverviewRecentAchievementColumnWidths;
            if (achievementMap == null)
            {
                achievementMap = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                _settings.Persisted.OverviewRecentAchievementColumnWidths = achievementMap;
                changed = true;
            }
            changed |= EnsureSeeds(achievementMap, DefaultAchievementWidthSeeds);

            var overviewMap = _settings.Persisted.OverviewGameSummariesColumnWidths;
            if (overviewMap == null)
            {
                overviewMap = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                _settings.Persisted.OverviewGameSummariesColumnWidths = overviewMap;
                changed = true;
            }
            changed |= EnsureSeeds(overviewMap, DefaultOverviewWidthSeeds);

            if (changed) SaveSettings();
        }

        private bool EnsureSeeds(Dictionary<string, double> map, IReadOnlyDictionary<string, double> seeds)
        {
            if (map == null || seeds == null) return false;
            var changed = false;
            foreach (var pair in seeds)
            {
                if (!map.TryGetValue(pair.Key, out var width) || !ColumnWidthNormalization.IsValidWidth(width))
                {
                    map[pair.Key] = pair.Value;
                    changed = true;
                }
            }
            return changed;
        }

        #endregion

        #region Column Visibility Menu

        private ContextMenu BuildColumnVisibilityMenu(DataGrid grid, DataGridColumn contextColumn = null)
        {
            if (grid == GameSummariesGrid && _settings?.Persisted != null)
            {
                var layoutMenu = new DataGridColumnLayoutService(
                    grid,
                    _logger,
                    () => _settings.Persisted.OverviewGameSummariesColumnWidths,
                    map => _settings.Persisted.OverviewGameSummariesColumnWidths = map,
                    () => _settings.Persisted.OverviewGameSummariesColumnVisibility,
                    map => _settings.Persisted.OverviewGameSummariesColumnVisibility = map,
                    SaveSettings,
                    DefaultOverviewWidthSeeds,
                    getOrder: () => _settings.Persisted.OverviewGameSummariesColumnOrder,
                    setOrder: map => _settings.Persisted.OverviewGameSummariesColumnOrder = map,
                    getCellAlignments: () => _settings.Persisted.OverviewGameSummariesColumnAlignments,
                    setCellAlignments: map => _settings.Persisted.OverviewGameSummariesColumnAlignments = map,
                    getDefaultCellAlignment: () => _settings.Persisted.GridCellAlignment,
                    getCellVerticalAlignments: () => _settings.Persisted.OverviewGameSummariesColumnVerticalAlignments,
                    setCellVerticalAlignments: map => _settings.Persisted.OverviewGameSummariesColumnVerticalAlignments = map,
                    getDefaultCellVerticalAlignment: () => _settings.Persisted.GridCellVerticalAlignment,
                    getHeaderHorizontalAlignments: () => _settings.Persisted.OverviewGameSummariesColumnHeaderAlignments,
                    setHeaderHorizontalAlignments: map => _settings.Persisted.OverviewGameSummariesColumnHeaderAlignments = map,
                    getDefaultHeaderHorizontalAlignment: () => _settings.Persisted.GridColumnHeaderAlignment,
                    applyCellAlignments: () => DataGridAlignmentBehavior.Refresh(GameSummariesGrid))
                    .BuildColumnVisibilityMenu(contextColumn);

                if (layoutMenu != null)
                {
                    return layoutMenu;
                }
            }

            var menu = new ContextMenu();
            foreach (var column in grid.Columns)
            {
                var header = ResolveColumnDisplayName(column);
                if (string.IsNullOrWhiteSpace(header)) continue;

                var target = column;
                var item = new MenuItem
                {
                    Header = header,
                    IsCheckable = true,
                    IsChecked = target.Visibility == Visibility.Visible,
                    StaysOpenOnClick = true
                };
                item.Click += (_, __) =>
                {
                    var isVisible = item.IsChecked;
                    target.Visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;
                    OnColumnVisibilityChanged(grid, target, isVisible);
                };
                menu.Items.Add(item);
            }
            return menu;
        }

        private static string ResolveHeaderText(object header)
        {
            switch (header)
            {
                case string text: return text;
                case TextBlock tb: return tb.Text;
                default: return header?.ToString() ?? string.Empty;
            }
        }

        private static string ResolveColumnDisplayName(DataGridColumn column)
        {
            var headerText = ResolveHeaderText(column?.Header);
            if (!string.IsNullOrWhiteSpace(headerText))
            {
                return headerText;
            }

            // Fall back to ColumnKey for columns with blank headers
            return ColumnWidthNormalization.GetColumnKey(column) ?? string.Empty;
        }

        private void OnColumnVisibilityChanged(DataGrid grid, DataGridColumn column, bool isVisible)
        {
            var key = ColumnWidthNormalization.GetColumnKey(column);
            if (string.IsNullOrWhiteSpace(key)) return;

            if (grid == GameSummariesGrid)
            {
                PersistVisibility(_settings?.Persisted?.OverviewGameSummariesColumnVisibility, key, isVisible);
                NormalizeGridColumns(GameSummariesGrid);
            }
            else if (grid == RecentAchievementsDataGrid.InternalDataGrid)
            {
                PersistVisibility(_settings?.Persisted?.DataGridColumnVisibility, key, isVisible);
                // GameAchievementsGrid handles its own visibility via AchievementDataGridControl
                NormalizeRecentAchievementColumns(grid);
            }
            else if (grid == SidebarAllAchievementsDataGrid?.InternalDataGrid)
            {
                PersistVisibility(_settings?.Persisted?.DataGridColumnVisibility, key, isVisible);
                NormalizeRecentAchievementColumns(grid);
            }
        }

        private void ApplyVisibilityToGridByKey(DataGrid grid, string key, bool isVisible)
        {
            if (grid == null || string.IsNullOrWhiteSpace(key)) return;
            foreach (var column in grid.Columns)
            {
                var colKey = ColumnWidthNormalization.GetColumnKey(column);
                if (ColumnWidthNormalization.KeysEqual(colKey, key))
                {
                    column.Visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;
                }
            }
        }

        private void PersistVisibility(Dictionary<string, bool> map, string key, bool isVisible)
        {
            if (map == null || string.IsNullOrWhiteSpace(key) || _settings?.Persisted == null) return;
            if (map.TryGetValue(key, out var existing) && existing == isVisible) return;
            map[key] = isVisible;
            SaveSettings();
        }

        #endregion

        #region Row Context Menu

        private bool OpenContextMenuForRow(DataGridRow row, bool useControllerPlacement = false)
        {
            if (row == null || !row.IsLoaded || row.DataContext == null) return false;

            var menu = BuildRowContextMenu(row.DataContext);
            if (menu == null || menu.Items.Count == 0) return false;

            row.ContextMenu = menu;
            if (useControllerPlacement)
            {
                return FullscreenControllerNavigationService.OpenContextMenu(row, menu);
            }

            menu.PlacementTarget = row;
            menu.IsOpen = true;
            return true;
        }

        private ContextMenu BuildRowContextMenu(object data)
        {
            if (data is GameSummaryItem) return BuildGameMenu(data);
            if (data is AchievementDisplayItem || data is RecentAchievementItem) return BuildAchievementMenu(data);
            return null;
        }

        private ContextMenu BuildGameMenu(object data)
        {
            var menu = new ContextMenu();
            menu.Items.Add(CreateMenuItem("LOCPlayAch_Menu_RefreshGame",
                () => ExecuteCommand(_viewModel?.RefreshSingleGameCommand, data)));
            menu.Items.Add(CreateMenuItem("LOCPlayAch_Menu_OpenGameInLibrary",
                () => ExecuteCommand(_viewModel?.OpenGameInLibraryCommand, data)));
            menu.Items.Add(CreateMenuItem("LOCPlayAch_Menu_ManageAchievements", () => OpenManageAchievements(data)));
            menu.Items.Add(new Separator());
            menu.Items.Add(CreateMenuItem("LOCPlayAch_Menu_ClearData", () => ClearGameData(data)));
            menu.Items.Add(CreateMenuItem("LOCPlayAch_Common_Action_ExcludeFromSummaries", () => ExcludeGameFromSummaries(data)));
            menu.Items.Add(CreateMenuItem("LOCPlayAch_Menu_ExcludeFromRefreshes", () => ExcludeGameFromRefreshes(data, clearDataWhenExcluding: false)));
            menu.Items.Add(CreateMenuItem("LOCPlayAch_Menu_ExcludeFromRefreshesAndClearData", () => ExcludeGameFromRefreshes(data, clearDataWhenExcluding: true)));

            return menu;
        }

        private ContextMenu BuildAchievementMenu(object data)
        {
            var menu = new ContextMenu();
            if (data is RecentAchievementItem)
            {
                menu.Items.Add(CreateMenuItem("LOCPlayAch_Menu_ViewAchievements",
                    () => ExecuteCommand(_viewModel?.OpenGameInOverviewCommand, data)));
            }
            else if (!IsCurrentGame(data))
            {
                menu.Items.Add(CreateMenuItem("LOCPlayAch_Menu_OpenGameInOverview",
                    () => ExecuteCommand(_viewModel?.OpenGameInOverviewCommand, data)));
            }
            menu.Items.Add(CreateMenuItem("LOCPlayAch_Menu_OpenGameInLibrary",
                () => ExecuteCommand(_viewModel?.OpenGameInLibraryCommand, data)));
            AchievementRowOptionsMenuBuilder.AppendAchievementOptions(
                menu,
                data,
                this,
                RefreshView);

            return menu;
        }

        private bool IsCurrentGame(object data)
        {
            if (_viewModel?.SelectedGame?.PlayniteGameId.HasValue != true) return false;
            if (!TryGetGameId(data, out var rowGameId)) return false;
            return rowGameId == _viewModel.SelectedGame.PlayniteGameId.Value;
        }

        private static bool TryGetGameId(object data, out Guid gameId)
        {
            switch (data)
            {
                case GameSummaryItem game when game.PlayniteGameId.HasValue:
                    gameId = game.PlayniteGameId.Value; return true;
                case AchievementDisplayItem ach when ach.PlayniteGameId.HasValue:
                    gameId = ach.PlayniteGameId.Value; return true;
                case RecentAchievementItem recent when recent.PlayniteGameId.HasValue:
                    gameId = recent.PlayniteGameId.Value; return true;
                case Guid id when id != Guid.Empty:
                    gameId = id; return true;
                default:
                    gameId = Guid.Empty; return false;
            }
        }

        private MenuItem CreateMenuItem(string resourceKey, Action onClick)
        {
            var text = TryFindResource(resourceKey) as string ?? resourceKey;
            var item = new MenuItem { Header = text };
            item.Click += (_, __) => onClick?.Invoke();
            return item;
        }

        private static void ExecuteCommand(System.Windows.Input.ICommand command, object parameter)
        {
            if (command != null && command.CanExecute(parameter))
                command.Execute(parameter);
        }

        private void OpenManageAchievements(object data)
        {
            if (TryGetGameId(data, out var gameId))
            {
                PlayniteAchievementsPlugin.Instance?.OpenManageAchievementsView(gameId);
            }
        }

        private void ClearGameData(object data)
        {
            if (!TryGetGameId(data, out var gameId)) return;
            var game = _playniteApi?.Database?.Games?.Get(gameId);
            if (game == null) return;

            var result = _playniteApi?.Dialogs?.ShowMessage(
                string.Format(ResourceProvider.GetString("LOCPlayAch_Menu_ClearData_ConfirmSingle"), game.Name),
                ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                MessageBoxButton.YesNo, MessageBoxImage.Warning) ?? MessageBoxResult.None;

            if (result != MessageBoxResult.Yes) return;

            try
            {
                if (_achievementOverridesService != null)
                {
                    _achievementOverridesService.ClearGameData(game.Id, game.Name);
                }
                else
                {
                    _cacheManager.RemoveGameCache(game.Id);
                }

                _playniteApi?.Dialogs?.ShowMessage(
                    ResourceProvider.GetString("LOCPlayAch_Status_Succeeded"),
                    ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed to clear data for game '{game.Name}' ({game.Id}).");
                _playniteApi?.Dialogs?.ShowMessage(
                    string.Format(ResourceProvider.GetString("LOCPlayAch_Status_Failed"), ex.Message),
                    ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ExcludeGameFromSummaries(object data)
        {
            if (!TryGetGameId(data, out var gameId))
            {
                return;
            }

            _achievementOverridesService?.SetExcludedFromSummaries(gameId, true);
        }

        private void ExcludeGameFromRefreshes(object data, bool clearDataWhenExcluding)
        {
            if (!TryGetGameId(data, out var gameId))
            {
                return;
            }

            var game = _playniteApi?.Database?.Games?.Get(gameId);
            if (game == null)
            {
                return;
            }

            if (clearDataWhenExcluding)
            {
                var result = _playniteApi?.Dialogs?.ShowMessage(
                    string.Format(ResourceProvider.GetString("LOCPlayAch_Menu_Exclude_ConfirmSingle"), game.Name),
                    ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) ?? MessageBoxResult.None;

                if (result != MessageBoxResult.Yes)
                {
                    return;
                }
            }

            _achievementOverridesService?.SetExcludedByUser(
                gameId,
                excluded: true,
                clearCachedDataWhenExcluding: clearDataWhenExcluding);
        }

        #endregion

        #region Sort and Scroll Reset

        private void ResetAchievementsScrollPosition()
        {
            var grid = GameAchievementsGrid?.InternalDataGrid;
            if (grid == null) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                var g = GameAchievementsGrid?.InternalDataGrid;
                if (g == null) return;
                g.SelectedIndex = -1;
                if (g.Items.Count > 0)
                    g.ScrollIntoView(g.Items[0]);
                if (VisualTreeHelpers.FindVisualChild<ScrollViewer>(g) is ScrollViewer sv)
                    sv.ScrollToTop();
            }), DispatcherPriority.Loaded);
        }

        private void ClearAchievementsSortIndicators()
        {
            var grid = GameAchievementsGrid?.InternalDataGrid;
            if (grid != null)
            {
                foreach (var c in grid.Columns) c.SortDirection = null;
            }

            GameAchievementsGrid?.SetSortIndicator(null, null);
        }

        private void ResetAchievementsSortDirection()
        {
            var grid = GameAchievementsGrid?.InternalDataGrid;
            if (grid == null) return;
            foreach (var c in grid.Columns) { c.SortDirection = null; SetColumnSortLevel(grid, c, null); }

            if (_viewModel?.IsGameSelected != true)
            {
                GameAchievementsGrid?.SetSortIndicator(null, null);
                return;
            }

            if (_viewModel.SelectedGameUsesSourceOrder)
            {
                GameAchievementsGrid?.SetSortIndicator(null, null);
                return;
            }

            AchievementSortHelper.ApplySortIndicator(
                _viewModel.SelectedGameSortPath,
                _viewModel.SelectedGameSortDirection,
                _settings?.Persisted,
                AchievementSortSurface.OverviewSelectedGame,
                (sortPath, sortDirection) =>
                {
                    if (!string.IsNullOrWhiteSpace(sortPath))
                    {
                        var primary = grid.Columns.FirstOrDefault(c => c?.SortMemberPath == sortPath);
                        if (primary != null) { primary.SortDirection = sortDirection; SetColumnSortLevel(grid, primary, "1"); }
                    }
                    GameAchievementsGrid?.SetSortIndicator(sortPath, sortDirection);
                });

            var secondaries = _viewModel?.SelectedGameSecondarySorts;
            if (secondaries != null)
            {
                for (var i = 0; i < secondaries.Count; i++)
                {
                    var (path, dir) = secondaries[i];
                    var col = grid.Columns.FirstOrDefault(c => c?.SortMemberPath == path);
                    if (col != null) { col.SortDirection = dir; SetColumnSortLevel(grid, col, (i + 2).ToString()); }
                }
            }
        }

        private static DataGridColumnHeader GetColumnHeader(DataGrid grid, DataGridColumn column)
        {
            if (grid == null || column == null) return null;
            return FindVisualChildren<DataGridColumnHeader>(grid)
                .FirstOrDefault(h => h.Column == column);
        }

        private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent == null) yield break;
            var count = VisualTreeHelper.GetChildrenCount(parent);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T t) yield return t;
                foreach (var grandchild in FindVisualChildren<T>(child))
                    yield return grandchild;
            }
        }

        private void SetColumnSortLevel(DataGrid grid, DataGridColumn column, string level)
        {
            var header = GetColumnHeader(grid, column);
            if (header != null) header.Tag = level;
        }

        private void ResetOverviewSortDirection()
        {
            var grid = GameSummariesGrid;
            if (grid?.Columns == null)
            {
                return;
            }

            foreach (var column in grid.Columns)
            {
                column.SortDirection = null;
                SetColumnSortLevel(grid, column, null);
            }
        }

        private void ResetRecentAchievementsSortDirection()
        {
            if (RecentAchievementsDataGrid == null) return;

            if (_viewModel?.RecentUsesSourceOrder == true)
            {
                RecentAchievementsDataGrid.SetSortIndicator(null, null);
                return;
            }

            AchievementSortHelper.ApplySortIndicator(
                _viewModel?.RecentSortPath,
                _viewModel?.RecentSortDirection,
                _settings?.Persisted,
                AchievementSortSurface.OverviewRecentAchievements,
                (sortPath, sortDirection) => RecentAchievementsDataGrid?.SetSortIndicator(sortPath, sortDirection));
        }

        private void ResetSidebarAllAchievementsSortDirection()
        {
            if (SidebarAllAchievementsDataGrid == null) return;
            var grid = SidebarAllAchievementsDataGrid.InternalDataGrid;
            if (grid == null) return;

            foreach (var c in grid.Columns) { c.SortDirection = null; SetColumnSortLevel(grid, c, null); }

            if (_viewModel?.SidebarAllUsesSourceOrder == true)
            {
                SidebarAllAchievementsDataGrid.SetSortIndicator(null, null);
                return;
            }

            AchievementSortHelper.ApplySortIndicator(
                _viewModel?.SidebarAllSortPath,
                _viewModel?.SidebarAllSortDirection,
                _settings?.Persisted,
                AchievementSortSurface.AchievementDataGrid,
                (sortPath, sortDirection) =>
                {
                    if (!string.IsNullOrWhiteSpace(sortPath))
                    {
                        var primary = grid.Columns.FirstOrDefault(c => c?.SortMemberPath == sortPath);
                        if (primary != null) { primary.SortDirection = sortDirection; SetColumnSortLevel(grid, primary, "1"); }
                    }
                });

            var secondaries = _viewModel?.SidebarAllSecondarySorts;
            if (secondaries != null)
            {
                for (var i = 0; i < secondaries.Count; i++)
                {
                    var (path, dir) = secondaries[i];
                    var col = grid.Columns.FirstOrDefault(c => c?.SortMemberPath == path);
                    if (col != null) { col.SortDirection = dir; SetColumnSortLevel(grid, col, (i + 2).ToString()); }
                }
            }
        }

        private void ResetRecentAchievementsToDefaultSort()
        {
            if (_viewModel == null || RecentAchievementsDataGrid == null) return;

            if (!IsRecentDefaultSortApplied())
            {
                _viewModel.ApplyDefaultRecentSort();
            }

            ResetRecentAchievementsSortDirection();
        }

        private bool IsRecentDefaultSortApplied()
        {
            return string.IsNullOrWhiteSpace(_viewModel?.RecentSortPath);
        }

        #endregion

        private void SaveSettings()
        {
            try
            {
                PlayniteAchievementsPlugin.Instance?.SavePluginSettings(_settings);
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, "Failed to save overview column settings.");
            }
        }
    }
}



