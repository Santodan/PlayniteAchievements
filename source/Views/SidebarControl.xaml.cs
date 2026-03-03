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
        private readonly AchievementService _achievementService;
        private readonly IPlayniteAPI _playniteApi;
        private bool _isActive;
        private Guid? _lastSelectedOverviewGameId;
        private DataGridRow _pendingRightClickRow;

        // Column persistence state
        private readonly Dictionary<DataGridColumn, EventHandler> _columnWidthChangedHandlers = new Dictionary<DataGridColumn, EventHandler>();
        private readonly Dictionary<string, double> _pendingAchievementWidthUpdates = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, double> _pendingOverviewWidthUpdates = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        private DispatcherTimer _saveTimer;
        private bool _isApplyingWidths;
        private bool _isResizeInProgress;
        private string _lastAchievementResizedKey;
        private string _lastOverviewResizedKey;
        private Dictionary<string, double> _pendingRecentToggleWidths;
        private Dictionary<string, double> _pendingGameToggleWidths;

        private static readonly IReadOnlyDictionary<string, double> DefaultAchievementWidthSeeds =
            new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                ["Achievement"] = 520,
                ["UnlockDate"] = 230,
                ["Rarity"] = 170,
                ["Points"] = 120
            };

        private static readonly IReadOnlyDictionary<string, double> DefaultOverviewWidthSeeds =
            new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                ["OverviewGameName"] = 500,
                ["OverviewLastPlayed"] = 240,
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
            AchievementService achievementService,
            RefreshCoordinator refreshCoordinator,
            PlayniteAchievementsSettings settings)
        {
            InitializeComponent();
            InitSaveTimer();

            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _settings = settings;
            _achievementService = achievementService ?? throw new ArgumentNullException(nameof(achievementService));
            _playniteApi = api ?? throw new ArgumentNullException(nameof(api));

            _viewModel = new SidebarViewModel(
                achievementService,
                refreshCoordinator ?? throw new ArgumentNullException(nameof(refreshCoordinator)),
                api,
                logger,
                settings);
            DataContext = _viewModel;
            _viewModel.PropertyChanged += ViewModel_PropertyChanged;
            _viewModel.SetActive(false);
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
            AttachHandlers(RecentAchievementsDataGrid);
            AttachHandlers(GameAchievementsDataGrid);
            ApplyVisibilityToGrids();
            ApplyWidthsToGrids();
        }

        private void ViewModel_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (_viewModel == null || e.PropertyName != nameof(SidebarViewModel.IsGameSelected)) return;

            if (!_viewModel.IsGameSelected)
            {
                ResetRecentAchievementsToDefaultSort();
            }

            if (TryApplyPendingToggleWidths()) return;
            QueueActiveGridNormalization(rescaleAll: false);
        }

        private void SaveTimer_Tick(object sender, EventArgs e)
        {
            _saveTimer?.Stop();
            var shouldNormalizeAchievements = _pendingAchievementWidthUpdates.Count > 0;
            var shouldNormalizeOverview = _pendingOverviewWidthUpdates.Count > 0;
            FlushPendingUpdates();

            if (_isResizeInProgress) return;

            if (shouldNormalizeAchievements)
            {
                NormalizeSharedAchievementColumns(RecentAchievementsDataGrid);
            }

            if (shouldNormalizeOverview)
            {
                NormalizeGridColumns(GamesOverviewDataGrid);
            }
        }

        private void ClearLeftSearch_Click(object sender, RoutedEventArgs e) => _viewModel?.ClearLeftSearch();
        private void ClearRightSearch_Click(object sender, RoutedEventArgs e) => _viewModel?.ClearRightSearch();

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

        private void OnProviderPieChartSliceClick(object sender, string providerName)
        {
            _viewModel?.ToggleProviderFilterFromPieChart(providerName);
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
            DetachGridHandlers(RecentAchievementsDataGrid);
            DetachGridHandlers(GameAchievementsDataGrid);

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

            if (grid == RecentAchievementsDataGrid || grid == GameAchievementsDataGrid)
            {
                _lastAchievementResizedKey = key;
                QueueWidthUpdate(_pendingAchievementWidthUpdates, key, width);
                ApplyWidthToGridByKey(RecentAchievementsDataGrid, key, width);
                ApplyWidthToGridByKey(GameAchievementsDataGrid, key, width);
            }
            else if (grid == GamesOverviewDataGrid)
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
            if (_pendingAchievementWidthUpdates.Count > 0)
            {
                changed |= FlushToMap(_settings.Persisted.SidebarAchievementColumnWidths, _pendingAchievementWidthUpdates);
            }
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

            var isSharedGrid = grid == RecentAchievementsDataGrid || grid == GameAchievementsDataGrid;
            var isVisibilityActivation = e.PreviousSize.Width <= 1;
            if (isSharedGrid && isVisibilityActivation && HasPendingToggleWidths()) return;

            grid.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (grid.IsLoaded && !_isResizeInProgress)
                {
                    if (isSharedGrid)
                    {
                        NormalizeSharedAchievementColumns(grid);
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
                if (grid == RecentAchievementsDataGrid || grid == GameAchievementsDataGrid)
                {
                    NormalizeSharedAchievementColumns(grid);
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

            if (grid == RecentAchievementsDataGrid || grid == GameAchievementsDataGrid)
            {
                NormalizeSharedAchievementColumns(grid, rescaleAll);
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

        private void NormalizeSharedAchievementColumns(DataGrid referenceGrid, bool rescaleAll = false)
        {
            if (!TryBuildSharedWidthPlans(referenceGrid, rescaleAll, out var recentPlan, out var gamePlan)) return;
            ApplyWidthPlans(recentPlan, gamePlan);
        }

        private bool TryBuildSharedWidthPlans(DataGrid referenceGrid, bool rescaleAll,
            out Dictionary<string, double> recentPlan, out Dictionary<string, double> gamePlan)
        {
            recentPlan = null;
            gamePlan = null;

            if (referenceGrid == null || !referenceGrid.IsLoaded) return false;

            var fallbackWidth = ColumnWidthNormalization.GetGridAvailableWidth(referenceGrid);
            var preferredWidths = CaptureResizableWidths(referenceGrid, fallbackWidth);
            if (preferredWidths.Count == 0)
            {
                preferredWidths = GetAchievementWidths();
            }

            var hasRecent = ColumnWidthNormalization.TryBuildNormalizedWidths(
                RecentAchievementsDataGrid, _lastAchievementResizedKey, rescaleAll,
                preferredWidths, fallbackWidth, out recentPlan);
            var hasGame = ColumnWidthNormalization.TryBuildNormalizedWidths(
                GameAchievementsDataGrid, _lastAchievementResizedKey, rescaleAll,
                preferredWidths, fallbackWidth, out gamePlan);

            if (!hasRecent && !hasGame)
            {
                if (ColumnWidthNormalization.TryBuildNormalizedWidths(referenceGrid, _lastAchievementResizedKey,
                    rescaleAll, preferredWidths, fallbackWidth, out var fallback))
                {
                    if (referenceGrid == RecentAchievementsDataGrid)
                    {
                        recentPlan = fallback;
                        hasRecent = true;
                    }
                    else if (referenceGrid == GameAchievementsDataGrid)
                    {
                        gamePlan = fallback;
                        hasGame = true;
                    }
                }
            }

            return hasRecent || hasGame;
        }

        private void ApplyWidthPlans(Dictionary<string, double> recentPlan, Dictionary<string, double> gamePlan)
        {
            if (recentPlan != null && recentPlan.Count > 0 &&
                RecentAchievementsDataGrid != null && RecentAchievementsDataGrid.IsLoaded)
            {
                ColumnWidthNormalization.ApplyWidthsByKey(RecentAchievementsDataGrid, recentPlan, ref _isApplyingWidths);
            }

            if (gamePlan != null && gamePlan.Count > 0 &&
                GameAchievementsDataGrid != null && GameAchievementsDataGrid.IsLoaded)
            {
                ColumnWidthNormalization.ApplyWidthsByKey(GameAchievementsDataGrid, gamePlan, ref _isApplyingWidths);
            }
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
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_viewModel == null) return;
                var activeGrid = _viewModel.IsGameSelected ? GameAchievementsDataGrid : RecentAchievementsDataGrid;
                if (activeGrid == null || !activeGrid.IsLoaded || !activeGrid.IsVisible || activeGrid.ActualWidth <= 1) return;
                NormalizeSharedAchievementColumns(activeGrid, rescaleAll);
            }), DispatcherPriority.Loaded);
        }

        #endregion

        #region Toggle Precomputation

        private bool HasPendingToggleWidths()
        {
            return (_pendingRecentToggleWidths != null && _pendingRecentToggleWidths.Count > 0) ||
                   (_pendingGameToggleWidths != null && _pendingGameToggleWidths.Count > 0);
        }

        private bool TryApplyPendingToggleWidths()
        {
            if (!HasPendingToggleWidths()) return false;

            var hasActivePlan = _viewModel?.IsGameSelected == true
                ? _pendingGameToggleWidths != null && _pendingGameToggleWidths.Count > 0
                : _pendingRecentToggleWidths != null && _pendingRecentToggleWidths.Count > 0;

            ApplyWidthPlans(_pendingRecentToggleWidths, _pendingGameToggleWidths);
            _pendingRecentToggleWidths = null;
            _pendingGameToggleWidths = null;
            return hasActivePlan;
        }

        private void PrecomputeToggleWidths(bool toGameSelected)
        {
            _pendingRecentToggleWidths = null;
            _pendingGameToggleWidths = null;

            var sourceGrid = toGameSelected ? RecentAchievementsDataGrid : GameAchievementsDataGrid;
            if (sourceGrid == null || !sourceGrid.IsLoaded)
            {
                sourceGrid = toGameSelected ? GameAchievementsDataGrid : RecentAchievementsDataGrid;
            }
            if (sourceGrid == null || !sourceGrid.IsLoaded) return;

            if (TryBuildSharedWidthPlans(sourceGrid, rescaleAll: true, out var recentPlan, out var gamePlan))
            {
                _pendingRecentToggleWidths = recentPlan;
                _pendingGameToggleWidths = gamePlan;
                ApplyWidthPlans(recentPlan, gamePlan);
            }
        }

        #endregion

        #region Apply/Get Widths

        private void ApplyVisibilityToGrids()
        {
            ApplyVisibility(RecentAchievementsDataGrid, _settings?.Persisted?.DataGridColumnVisibility);
            ApplyVisibility(GameAchievementsDataGrid, _settings?.Persisted?.DataGridColumnVisibility);
            ApplyVisibility(GamesOverviewDataGrid, _settings?.Persisted?.GamesOverviewColumnVisibility);
            NormalizeSharedAchievementColumns(RecentAchievementsDataGrid);
            NormalizeGridColumns(GamesOverviewDataGrid);
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
            EnsureDefaultSeeds();
            var achievementWidths = GetAchievementWidths();
            ApplyWidths(RecentAchievementsDataGrid, achievementWidths);
            ApplyWidths(GameAchievementsDataGrid, achievementWidths);
            ApplyWidths(GamesOverviewDataGrid, GetOverviewWidths());
            NormalizeSharedAchievementColumns(RecentAchievementsDataGrid);
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
                var header = ResolveHeaderText(column?.Header);
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

        private void OnColumnVisibilityChanged(DataGrid grid, DataGridColumn column, bool isVisible)
        {
            var key = ColumnWidthNormalization.GetColumnKey(column);
            if (string.IsNullOrWhiteSpace(key)) return;

            if (grid == GamesOverviewDataGrid)
            {
                PersistVisibility(_settings?.Persisted?.GamesOverviewColumnVisibility, key, isVisible);
                NormalizeGridColumns(GamesOverviewDataGrid);
            }
            else if (grid == RecentAchievementsDataGrid || grid == GameAchievementsDataGrid)
            {
                PersistVisibility(_settings?.Persisted?.DataGridColumnVisibility, key, isVisible);
                ApplyVisibilityToGridByKey(RecentAchievementsDataGrid, key, isVisible);
                ApplyVisibilityToGridByKey(GameAchievementsDataGrid, key, isVisible);
                NormalizeSharedAchievementColumns(grid);
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
            menu.Items.Add(CreateMenuItem("LOCPlayAch_Menu_GameOptions", () => OpenGameOptions(data)));
            menu.Items.Add(CreateMenuItem("LOCPlayAch_Menu_OpenGameInLibrary",
                () => ExecuteCommand(_viewModel?.OpenGameInLibraryCommand, data)));
            menu.Items.Add(CreateMenuItem("LOCPlayAch_Menu_ClearData", () => ClearGameData(data)));

            return menu;
        }

        private ContextMenu BuildAchievementMenu(object data)
        {
            var menu = new ContextMenu();
            if (!IsCurrentGame(data))
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
                _achievementService.RemoveGameCache(game.Id);
                _playniteApi?.Dialogs?.ShowMessage(
                    string.Format(ResourceProvider.GetString("LOCPlayAch_Menu_ClearData_SuccessSingle"), game.Name),
                    ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed to clear data for game '{game.Name}' ({game.Id}).");
                _playniteApi?.Dialogs?.ShowMessage(
                    string.Format(ResourceProvider.GetString("LOCPlayAch_Menu_ClearData_Failed"), ex.Message),
                    ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #endregion

        #region Sort and Scroll Reset

        private void ResetAchievementsScrollPosition()
        {
            if (GameAchievementsDataGrid == null) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (GameAchievementsDataGrid == null) return;
                GameAchievementsDataGrid.SelectedIndex = -1;
                if (GameAchievementsDataGrid.Items.Count > 0)
                    GameAchievementsDataGrid.ScrollIntoView(GameAchievementsDataGrid.Items[0]);
                if (VisualTreeHelpers.FindVisualChild<ScrollViewer>(GameAchievementsDataGrid) is ScrollViewer sv)
                    sv.ScrollToTop();
            }), DispatcherPriority.Loaded);
        }

        private void ResetAchievementsSortDirection()
        {
            if (GameAchievementsDataGrid == null) return;
            foreach (var c in GameAchievementsDataGrid.Columns) c.SortDirection = null;
            var unlockCol = GameAchievementsDataGrid.Columns.FirstOrDefault(c => c.SortMemberPath == "UnlockTime");
            if (unlockCol != null) unlockCol.SortDirection = ListSortDirection.Descending;
        }

        private void ResetRecentAchievementsSortDirection()
        {
            if (RecentAchievementsDataGrid == null) return;
            foreach (var c in RecentAchievementsDataGrid.Columns) c.SortDirection = null;
            var unlockCol = RecentAchievementsDataGrid.Columns.FirstOrDefault(c => c.SortMemberPath == "UnlockTime");
            if (unlockCol != null) unlockCol.SortDirection = ListSortDirection.Descending;
        }

        private void ResetRecentAchievementsToDefaultSort()
        {
            if (_viewModel == null || RecentAchievementsDataGrid == null) return;
            if (IsRecentDefaultSortApplied()) return;
            _viewModel.SortDataGrid(RecentAchievementsDataGrid, "UnlockTime", ListSortDirection.Descending);
            ResetRecentAchievementsSortDirection();
        }

        private bool IsRecentDefaultSortApplied()
        {
            if (RecentAchievementsDataGrid?.Columns == null) return false;
            var unlockCol = RecentAchievementsDataGrid.Columns.FirstOrDefault(c => c?.SortMemberPath == "UnlockTime");
            if (unlockCol?.SortDirection != ListSortDirection.Descending) return false;
            return RecentAchievementsDataGrid.Columns.All(c => c == unlockCol || c.SortDirection == null);
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



