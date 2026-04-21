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
using PlayniteAchievements.Models;
using PlayniteAchievements.Services;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.Views.Helpers;

namespace PlayniteAchievements.Views
{
    public partial class SidebarControl : UserControl, IDisposable
    {
        private readonly SidebarViewModel _viewModel;
        private readonly ILogger _logger;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly RefreshRuntime _refreshService;
        private readonly ICacheManager _cacheManager;
        private readonly Action _persistSettingsForUi;
        private readonly AchievementOverridesService _achievementOverridesService;
        private readonly AchievementDataService _achievementDataService;
        private readonly IPlayniteAPI _playniteApi;
        private bool _isActive;
        private Guid? _lastSelectedOverviewGameId;
        private DataGridRow _pendingRightClickRow;

        // Column persistence state (for GamesOverviewDataGrid only - AchievementDataGridControl handles its own)
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
                ["TotalAchievements"] = 180
            };

        public SidebarControl()
        {
            InitializeComponent();
            InitSaveTimer();
        }

        public SidebarControl(
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
            InitSaveTimer();

            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _settings = settings;
            _refreshService = refreshRuntime ?? throw new ArgumentNullException(nameof(refreshRuntime));
            _cacheManager = cacheManager ?? throw new ArgumentNullException(nameof(cacheManager));
            _persistSettingsForUi = persistSettingsForUi ?? throw new ArgumentNullException(nameof(persistSettingsForUi));
            _achievementOverridesService = achievementOverridesService ?? throw new ArgumentNullException(nameof(achievementOverridesService));
            _achievementDataService = achievementDataService ?? throw new ArgumentNullException(nameof(achievementDataService));
            _playniteApi = api ?? throw new ArgumentNullException(nameof(api));

            _viewModel = new SidebarViewModel(
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
                ApplyWidthsToGrids();
                RecentAchievementsDataGrid?.Refresh();
                GameAchievementsGrid?.Refresh();
            }), DispatcherPriority.Loaded);
        }

        public void Deactivate()
        {
            if (!_isActive) return;
            RecentAchievementsDataGrid?.FlushLayoutPersistence();
            GameAchievementsGrid?.FlushLayoutPersistence();
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
                _viewModel?.Dispose();
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "SidebarControl dispose failed.");
            }
        }

        #region Event Handlers

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            EnsureDefaultSeeds();
            AttachHandlers(GamesOverviewDataGrid);
            // RecentAchievementsDataGrid is now AchievementDataGridControl, uses built-in persistence
            // GameAchievementsGrid uses AchievementDataGridControl with built-in persistence
            ApplyVisibilityToGrids();
            ApplyWidthsToGrids();
            ResetAchievementsSortDirection();
            UpdatePieChartLayout();
        }

        private void Plugin_SettingsSaved(object sender, EventArgs e)
        {
            ResetAchievementsSortDirection();
        }

        private void ViewModel_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (_viewModel == null || e == null) return;

            if (e.PropertyName == nameof(SidebarViewModel.SelectedGameHasCustomAchievementOrder))
            {
                ResetAchievementsSortDirection();
                return;
            }

            if (e.PropertyName == nameof(SidebarViewModel.ShowSidebarGamesPieChart)
                || e.PropertyName == nameof(SidebarViewModel.ShowSidebarProviderPieChart)
                || e.PropertyName == nameof(SidebarViewModel.ShowSidebarRarityPieChart)
                || e.PropertyName == nameof(SidebarViewModel.ShowSidebarTrophyPieChart)
                || e.PropertyName == nameof(SidebarViewModel.ShowSidebarPieCharts)
                || e.PropertyName == nameof(SidebarViewModel.ShowSidebarBarCharts))
            {
                UpdatePieChartLayout();
            }

            if (e.PropertyName != nameof(SidebarViewModel.IsGameSelected)) return;

            // Defer grid operations to Render priority to batch with visibility change
            // This prevents flicker by ensuring all layout happens in one render pass
            Dispatcher.BeginInvoke(new Action(() =>
            {
                ResetAchievementsSortDirection();

                if (!_viewModel.IsGameSelected)
                {
                    ResetRecentAchievementsToDefaultSort();
                }

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
                NormalizeGridColumns(GamesOverviewDataGrid);
            }
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
            menu.IsOpen = true;
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

        private void GamesOverview_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_viewModel == null || !(sender is DataGrid grid)) return;

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
                if (_viewModel.IsGameSelected)
                {
                    PrecomputeToggleWidths(toGameSelected: false);
                }
                _viewModel.ClearGameSelection();
                _lastSelectedOverviewGameId = null;
                e.Handled = true;
            }
        }

        private void GamesOverview_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_viewModel == null || !(sender is DataGrid grid)) return;

            if (grid.SelectedItem is GameOverviewItem item)
            {
                var currentGameId = item.PlayniteGameId;
                var gameChanged = !_lastSelectedOverviewGameId.HasValue ||
                                  currentGameId != _lastSelectedOverviewGameId.Value;
                var wasGameSelected = _viewModel.IsGameSelected;

                if (!wasGameSelected)
                {
                    PrecomputeToggleWidths(toGameSelected: true);
                }

                _viewModel.SelectedGame = item;
                _lastSelectedOverviewGameId = currentGameId;

                if (gameChanged)
                {
                    ResetAchievementsSortDirection();
                    ResetAchievementsScrollPosition();
                }
            }
            else
            {
                _lastSelectedOverviewGameId = null;
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
            if (sender is DataGridRow row)
            {
                e.Handled = true;
                _pendingRightClickRow = row;
            }
        }

        private void DataGridRow_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is DataGridRow row)
            {
                e.Handled = true;
                var targetRow = _pendingRightClickRow ?? row;
                _pendingRightClickRow = null;
                OpenContextMenuForRow(targetRow);
            }
        }

        private void DataGridColumnMenu_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!(sender is DataGrid grid)) return;

            var source = e.OriginalSource as DependencyObject;
            if (VisualTreeHelpers.FindVisualParent<DataGridRow>(source) != null) return;

            e.Handled = true;
            var menu = BuildColumnVisibilityMenu(grid);
            if (menu == null || menu.Items.Count == 0) return;

            menu.Placement = PlacementMode.RelativePoint;
            menu.PlacementTarget = grid;
            var pos = e.GetPosition(grid);
            menu.HorizontalOffset = pos.X;
            menu.VerticalOffset = pos.Y;
            menu.IsOpen = true;
        }

        private void DataGrid_Sorting(object sender, DataGridSortingEventArgs e)
        {
            if (_viewModel == null) return;
            var sortDirection = DataGridSortingHelper.HandleSorting(sender, e);
            if (sortDirection == null) return;
            _viewModel.SortDataGrid((sender as DataGrid), e.Column.SortMemberPath, sortDirection.Value);
        }

        private void GameAchievementsGrid_Sorting(object sender, DataGridSortingEventArgs e)
        {
            if (_viewModel == null) return;
            e.Handled = true;

            var grid = GameAchievementsGrid?.InternalDataGrid;
            if (grid == null) return;

            // Toggle sort direction
            var currentDirection = e.Column.SortDirection;
            var newDirection = currentDirection == ListSortDirection.Ascending
                ? ListSortDirection.Descending
                : ListSortDirection.Ascending;

            // Update sort indicator via the control
            GameAchievementsGrid?.SetSortIndicator(e.Column.SortMemberPath, newDirection);

            // Perform the actual sorting
            _viewModel.SortDataGrid(grid, e.Column.SortMemberPath, newDirection);
        }

        private void AchievementDataGrid_Sorting(object sender, DataGridSortingEventArgs e)
        {
            if (_viewModel == null) return;
            e.Handled = true;

            var control = sender as Controls.AchievementDataGridControl;
            var grid = control?.InternalDataGrid;
            if (grid == null) return;

            // Toggle sort direction
            var currentDirection = e.Column.SortDirection;
            var newDirection = currentDirection == ListSortDirection.Ascending
                ? ListSortDirection.Descending
                : ListSortDirection.Ascending;

            // Update sort indicator via the control
            control?.SetSortIndicator(e.Column.SortMemberPath, newDirection);

            // Perform the actual sorting
            _viewModel.SortDataGrid(grid, e.Column.SortMemberPath, newDirection);
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
            if (_viewModel == null || SidebarPieChartsGrid == null) return;

            var panels = new List<(FrameworkElement Element, bool IsVisible)>
            {
                (GamesPieChartPanel, _viewModel.ShowSidebarGamesPieChart),
                (ProviderPieChartPanel, _viewModel.ShowSidebarProviderPieChart),
                (RarityPieChartPanel, _viewModel.ShowSidebarRarityPieChart),
                (TrophyPieChartPanel, _viewModel.ShowSidebarTrophyPieChart)
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
            for (var i = 0; i < SidebarPieChartsGrid.ColumnDefinitions.Count; i++)
            {
                SidebarPieChartsGrid.ColumnDefinitions[i].Width = i < visibleIndex
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

            DetachGridHandlers(GamesOverviewDataGrid);
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
            if (grid == GamesOverviewDataGrid)
            {
                _lastOverviewResizedKey = key;
                QueueWidthUpdate(_pendingOverviewWidthUpdates, key, width);
            }
        }

        private void QueueWidthUpdate(Dictionary<string, double> map, string key, double width)
        {
            map[key] = Math.Round(width, 2);
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
                changed |= FlushToMap(_settings.Persisted.GamesOverviewColumnWidths, _pendingOverviewWidthUpdates);
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

            string protectedKey = grid == GamesOverviewDataGrid ? _lastOverviewResizedKey : null;
            var preferredWidths = grid == GamesOverviewDataGrid ? GetOverviewWidths() : null;

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
                if (_viewModel.IsGameSelected) return; // GameAchievementsGrid manages itself

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
            ApplyVisibility(GamesOverviewDataGrid, _settings?.Persisted?.GamesOverviewColumnVisibility);
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
            ApplyWidths(GamesOverviewDataGrid, GetOverviewWidths());
            NormalizeGridColumns(GamesOverviewDataGrid);
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
                        column.Width = new DataGridLength(width, DataGridLengthUnitType.Pixel);
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
                    if (ColumnWidthNormalization.KeysEqual(colKey, key) && Math.Abs(column.ActualWidth - width) > 0.1)
                    {
                        column.Width = new DataGridLength(width, DataGridLengthUnitType.Pixel);
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

            var current = _settings?.Persisted?.SidebarAchievementColumnWidths;
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
            return _settings?.Persisted?.GamesOverviewColumnWidths
                ?? new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        }

        private void EnsureDefaultSeeds()
        {
            if (_settings?.Persisted == null) return;
            var changed = false;

            var achievementMap = _settings.Persisted.SidebarAchievementColumnWidths;
            if (achievementMap == null)
            {
                achievementMap = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                _settings.Persisted.SidebarAchievementColumnWidths = achievementMap;
                changed = true;
            }
            changed |= EnsureSeeds(achievementMap, DefaultAchievementWidthSeeds);

            var overviewMap = _settings.Persisted.GamesOverviewColumnWidths;
            if (overviewMap == null)
            {
                overviewMap = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                _settings.Persisted.GamesOverviewColumnWidths = overviewMap;
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

        private ContextMenu BuildColumnVisibilityMenu(DataGrid grid)
        {
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

            if (grid == GamesOverviewDataGrid)
            {
                PersistVisibility(_settings?.Persisted?.GamesOverviewColumnVisibility, key, isVisible);
                NormalizeGridColumns(GamesOverviewDataGrid);
            }
            else if (grid == RecentAchievementsDataGrid.InternalDataGrid)
            {
                PersistVisibility(_settings?.Persisted?.DataGridColumnVisibility, key, isVisible);
                // GameAchievementsGrid handles its own visibility via AchievementDataGridControl
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

        private void OpenContextMenuForRow(DataGridRow row)
        {
            if (row == null || !row.IsLoaded || row.DataContext == null) return;

            var menu = BuildRowContextMenu(row.DataContext);
            if (menu == null || menu.Items.Count == 0) return;

            row.ContextMenu = menu;
            menu.PlacementTarget = row;
            menu.IsOpen = true;
        }

        private ContextMenu BuildRowContextMenu(object data)
        {
            if (data is GameOverviewItem) return BuildGameMenu(data);
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
            menu.Items.Add(CreateMenuItem("LOCPlayAch_Menu_GameOptions", () => OpenGameOptions(data)));
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
                    () => ExecuteCommand(_viewModel?.OpenGameInSidebarCommand, data)));
            }
            else if (!IsCurrentGame(data))
            {
                menu.Items.Add(CreateMenuItem("LOCPlayAch_Menu_OpenGameInSidebar",
                    () => ExecuteCommand(_viewModel?.OpenGameInSidebarCommand, data)));
            }
            menu.Items.Add(CreateMenuItem("LOCPlayAch_Menu_OpenGameInLibrary",
                () => ExecuteCommand(_viewModel?.OpenGameInLibraryCommand, data)));
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
                case GameOverviewItem game when game.PlayniteGameId.HasValue:
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

        private void OpenGameOptions(object data)
        {
            if (TryGetGameId(data, out var gameId))
            {
                PlayniteAchievementsPlugin.Instance?.OpenGameOptionsView(gameId);
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
                AchievementSortSurface.SidebarSelectedGame,
                (sortPath, sortDirection) => GameAchievementsGrid?.SetSortIndicator(sortPath, sortDirection));
        }

        private void ResetRecentAchievementsSortDirection()
        {
            if (RecentAchievementsDataGrid == null) return;
            foreach (var c in RecentAchievementsDataGrid.InternalDataGrid.Columns) c.SortDirection = null;
            var unlockCol = RecentAchievementsDataGrid.InternalDataGrid.Columns.FirstOrDefault(c => c.SortMemberPath == "UnlockTime");
            if (unlockCol != null) unlockCol.SortDirection = ListSortDirection.Descending;
        }

        private void ResetRecentAchievementsToDefaultSort()
        {
            if (_viewModel == null || RecentAchievementsDataGrid == null) return;
            if (IsRecentDefaultSortApplied()) return;
            _viewModel.SortDataGrid(RecentAchievementsDataGrid.InternalDataGrid, "UnlockTime", ListSortDirection.Descending);
            ResetRecentAchievementsSortDirection();
        }

        private bool IsRecentDefaultSortApplied()
        {
            if (RecentAchievementsDataGrid?.InternalDataGrid.Columns == null) return false;
            var unlockCol = RecentAchievementsDataGrid.InternalDataGrid.Columns.FirstOrDefault(c => c?.SortMemberPath == "UnlockTime");
            if (unlockCol?.SortDirection != ListSortDirection.Descending) return false;
            return RecentAchievementsDataGrid.InternalDataGrid.Columns.All(c => c == unlockCol || c.SortDirection == null);
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
                _logger?.Warn(ex, "Failed to save sidebar column settings.");
            }
        }
    }
}



