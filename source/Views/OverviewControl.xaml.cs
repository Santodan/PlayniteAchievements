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

        public OverviewControl()
        {
            InitializeComponent();
        }

        public OverviewControl(
            IPlayniteAPI api,
            ILogger logger,
            RefreshRuntime refreshRuntime,
            ICacheManager cacheManager,
            Action persistSettingsForUi,
            AchievementOverridesService achievementOverridesService,
            AchievementDataService achievementDataService,
            RefreshEntryPoint refreshEntryPoint,
            PlayniteAchievementsSettings settings)
        {
            InitializeComponent();

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

        public void Activate()
        {
            if (_isActive) return;
            _isActive = true;
            _viewModel?.SetActive(true);
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
                GameSummariesGridControl?.Dispose();
                RecentAchievementsDataGrid?.Dispose();
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
            GameAchievementsGrid?.Refresh();
            ResetOverviewSortDirection();
            ResetAchievementsSortDirection();
            UpdatePieChartLayout();
        }

        private void Plugin_SettingsSaved(object sender, EventArgs e)
        {
            ResetOverviewSortDirection();
            ResetAchievementsSortDirection();
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

            // Defer sort updates to Render priority so they batch with the panel visibility change.
            // The selected-game grid normalizes from its visibility/size events; refreshing here
            // reapplies persisted widths and produces a second visible layout pass.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                ResetAchievementsSortDirection();

                if (_viewModel.IsGameSelected)
                {
                    return;
                }

                ResetRecentAchievementsToDefaultSort();
                RecentAchievementsDataGrid?.Refresh();
            }), DispatcherPriority.Render);
        }

        private void ClearLeftSearch_Click(object sender, RoutedEventArgs e) => _viewModel?.ClearLeftSearch();
        private void ClearRightSearch_Click(object sender, RoutedEventArgs e) => _viewModel?.ClearRightSearch();

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
            foreach (var mode in modes.Where(mode => mode != null && !string.IsNullOrWhiteSpace(mode.Key)))
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
                menu.Items.Add(item);
            }

            if (menu.Items.Count == 0)
            {
                return;
            }

            OpenSelectorContextMenu(button, menu);
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
            var focusedGrid = GetFocusedOverviewGrid();
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
            var focusedGrid = GetFocusedOverviewGrid();
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
            var focusedGrid = GetFocusedOverviewGrid();
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
            var focusedGrid = GetFocusedOverviewGrid();
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
            var focusedGrid = GetFocusedOverviewGrid();
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

            var focusedGrid = GetFocusedOverviewGrid();
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
                return GameSummariesGridControl?.OpenColumnVisibilityMenuForController() == true;
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
                return GameSummariesGridControl?.ActivateFocusedColumnHeaderForController() == true;
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

        private DataGrid GetFocusedOverviewGrid()
        {
            var focused = Keyboard.FocusedElement as DependencyObject;
            var focusedGrid = VisualTreeHelpers.FindVisualParent<DataGrid>(focused)
                              ?? focused as DataGrid;

            if (IsOverviewGrid(focusedGrid))
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

        private bool IsOverviewGrid(DataGrid grid)
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
                OpenContextMenuForRow(targetRow);
            }
        }

        private static bool TryResolveContextMenuRow(object sender, MouseButtonEventArgs e, out DataGridRow row)
        {
            row = sender as DataGridRow
                  ?? e?.Source as DataGridRow
                  ?? VisualTreeHelpers.FindVisualParent<DataGridRow>(e?.OriginalSource as DependencyObject);
            return row != null;
        }

        private void DataGrid_Sorting(object sender, DataGridSortingEventArgs e)
        {
            if (_viewModel == null) return;
            e.Handled = true;

            var grid = sender as DataGrid;
            if (grid == null) return;

            var sortAction = GameSummariesSortHelper.ResolveGridSortAction(
                e.Column?.SortMemberPath,
                _viewModel.OverviewSortPath,
                _viewModel.OverviewSortDirection,
                _settings?.Persisted);
            if (sortAction.Kind == GameSummariesGridSortActionKind.None)
            {
                return;
            }

            if (sortAction.Kind == GameSummariesGridSortActionKind.ResetToDefault)
            {
                _viewModel.ApplyDefaultOverviewSort();
            }
            else if (sortAction.Direction.HasValue)
            {
                _viewModel.SortDataGrid(grid, sortAction.SortMemberPath, sortAction.Direction.Value);
            }

            ResetOverviewSortDirection();
        }

        private void GameAchievementsGrid_Sorting(object sender, DataGridSortingEventArgs e)
        {
            if (_viewModel == null) return;
            e.Handled = true;

            var grid = GameAchievementsGrid?.InternalDataGrid;
            if (grid == null) return;

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

            var sortAction = AchievementSortHelper.ResolveGridSortAction(
                e.Column?.SortMemberPath,
                _viewModel.RecentSortPath,
                _viewModel.RecentSortDirection,
                _settings?.Persisted,
                AchievementSortSurface.OverviewRecentAchievements,
                e.Column?.SortDirection);
            if (sortAction.Kind == AchievementGridSortActionKind.None)
            {
                return;
            }

            if (sortAction.Kind == AchievementGridSortActionKind.ResetToDefault)
            {
                _viewModel.ApplyDefaultRecentSort();
            }
            else if (sortAction.Direction.HasValue)
            {
                _viewModel.SortDataGrid(grid, sortAction.SortMemberPath, sortAction.Direction.Value);
            }

            ResetRecentAchievementsSortDirection();
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

        #region Overview Layout Persistence

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
            menu.Items.Add(new Separator());
            menu.Items.Add(CreateMenuItem("LOCPlayAch_Menu_ManageAchievements", () => OpenManageAchievements(data)));
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
            foreach (var c in grid.Columns) c.SortDirection = null;

            if (_viewModel?.IsGameSelected != true)
            {
                GameAchievementsGrid?.SetSortIndicator(null, null);
                return;
            }

            AchievementSortHelper.ApplySortIndicator(
                _viewModel.SelectedGameSortPath,
                _viewModel.SelectedGameSortDirection,
                _settings?.Persisted,
                AchievementSortSurface.OverviewSelectedGame,
                (sortPath, sortDirection) => GameAchievementsGrid?.SetSortIndicator(sortPath, sortDirection));
        }

        private void ResetOverviewSortDirection()
        {
            if (GameSummariesGridControl == null)
            {
                return;
            }

            GameSummariesSortHelper.ApplySortIndicator(
                _viewModel?.OverviewSortPath,
                _viewModel?.OverviewSortDirection,
                _settings?.Persisted,
                (sortPath, sortDirection) => GameSummariesGridControl.SetSortIndicator(sortPath, sortDirection));
        }

        private void ResetRecentAchievementsSortDirection()
        {
            AchievementSortHelper.ApplySortIndicator(
                _viewModel?.RecentSortPath,
                _viewModel?.RecentSortDirection,
                _settings?.Persisted,
                AchievementSortSurface.OverviewRecentAchievements,
                (sortPath, sortDirection) => RecentAchievementsDataGrid?.SetSortIndicator(sortPath, sortDirection));
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



