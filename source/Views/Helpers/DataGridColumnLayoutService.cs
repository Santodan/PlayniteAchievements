using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Playnite.SDK;
using PlayniteAchievements.Models.Settings;

namespace PlayniteAchievements.Views.Helpers
{
    /// <summary>
    /// Provides reusable column layout persistence for DataGrid controls.
    /// Encapsulates debouncing, normalization, and synchronization logic.
    /// </summary>
    public class DataGridColumnLayoutService : IDisposable
    {
        private const int InitialNormalizationMaxAttempts = 8;
        private const double LayoutWidthChangeThreshold = 0.2d;

        private readonly DataGrid _grid;
        private readonly ILogger _logger;
        private readonly Func<Dictionary<string, double>> _getWidths;
        private readonly Action<Dictionary<string, double>> _setWidths;
        private readonly Func<Dictionary<string, bool>> _getVisibility;
        private readonly Action<Dictionary<string, bool>> _setVisibility;
        private readonly Action _saveSettings;
        private readonly IReadOnlyDictionary<string, double> _defaultWidthSeeds;
        private readonly Dictionary<DataGridColumn, EventHandler> _columnWidthChangedHandlers = new Dictionary<DataGridColumn, EventHandler>();
        private readonly Dictionary<string, double> _pendingWidthUpdates = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, double> _resizeObservedWidths = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, double> _normalizedWidthOverrides = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        private readonly Func<Dictionary<string, int>> _getOrder;
        private readonly Action<Dictionary<string, int>> _setOrder;
        private readonly Func<Dictionary<string, GridAlignment>> _getCellAlignments;
        private readonly Action<Dictionary<string, GridAlignment>> _setCellAlignments;
        private readonly Func<GridAlignment> _getDefaultCellAlignment;
        private readonly Func<Dictionary<string, GridVerticalAlignment>> _getCellVerticalAlignments;
        private readonly Action<Dictionary<string, GridVerticalAlignment>> _setCellVerticalAlignments;
        private readonly Func<GridVerticalAlignment> _getDefaultCellVerticalAlignment;
        private readonly Func<Dictionary<string, GridAlignment>> _getHeaderHorizontalAlignments;
        private readonly Action<Dictionary<string, GridAlignment>> _setHeaderHorizontalAlignments;
        private readonly Func<GridAlignment> _getDefaultHeaderHorizontalAlignment;
        private readonly Action _applyCellAlignments;
        private readonly Func<string, double, bool> _isRuntimeDefaultWidth;
        private DispatcherTimer _saveTimer;
        private bool _isApplyingWidths;
        private bool _isApplyingOrder;
        private bool _isResizeInProgress;
        private bool _isColumnOrderSaveQueued;
        private string _lastResizedColumnKey;
        private string _lastResizeAbsorberColumnKey;
        private string _resizeBoundaryLeftColumnKey;
        private string _resizeBoundaryRightColumnKey;
        private bool _isAttached;
        private bool _normalizationQueued;
        private bool _queuedNormalizationRescaleAll;
        private bool _initialNormalizationActive;
        private bool _initialNormalizationCompleted;
        private int _initialNormalizationAttempts;
        private bool _hasSuccessfulNormalization;
        private bool _isInitialRenderSuppressed;
        private object _initialOpacityLocalValue = DependencyProperty.UnsetValue;
        private object _initialHitTestVisibleLocalValue = DependencyProperty.UnsetValue;
        private ScrollViewer _normalizationScrollViewer;
        private bool _scrollViewerAttachQueued;
        private double _lastObservedScrollViewerWidth;
        private DispatcherOperation _queuedNormalizationOperation;

        /// <summary>
        /// Column keys that should be excluded from the visibility toggle menu.
        /// Set before calling Attach() to exclude specific columns from user toggle.
        /// </summary>
        public ISet<string> ExcludedVisibilityKeys { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Column keys that should not expose per-column cell alignment controls.
        /// </summary>
        public ISet<string> ExcludedCellAlignmentKeys { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Column keys that should not expose per-column header alignment controls.
        /// </summary>
        public ISet<string> ExcludedHeaderAlignmentKeys { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Column keys that should be forced to collapsed visibility.
        /// These columns will be collapsed during ApplyPersistedVisibility to prevent flicker.
        /// </summary>
        public ISet<string> ForcedCollapsedKeys { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// When true, the grid is briefly rendered transparent until the first successful
        /// column normalization pass completes.
        /// </summary>
        public bool DelayInitialRenderUntilNormalized { get; set; }

        /// <summary>
        /// Creates a new DataGridColumnLayoutService.
        /// </summary>
        /// <param name="grid">The DataGrid to manage.</param>
        /// <param name="logger">Logger for diagnostics.</param>
        /// <param name="getWidths">Function to get the persisted width dictionary.</param>
        /// <param name="setWidths">Function to set the persisted width dictionary.</param>
        /// <param name="getVisibility">Function to get the persisted visibility dictionary.</param>
        /// <param name="setVisibility">Function to set the persisted visibility dictionary.</param>
        /// <param name="saveSettings">Action to save settings to disk.</param>
        /// <param name="defaultWidthSeeds">Default column widths for new installations.</param>
        /// <param name="isRuntimeDefaultWidth">Optional predicate for legacy seed widths that should not count as user customization.</param>
        public DataGridColumnLayoutService(
            DataGrid grid,
            ILogger logger,
            Func<Dictionary<string, double>> getWidths,
            Action<Dictionary<string, double>> setWidths,
            Func<Dictionary<string, bool>> getVisibility,
            Action<Dictionary<string, bool>> setVisibility,
            Action saveSettings,
            IReadOnlyDictionary<string, double> defaultWidthSeeds = null,
            Func<Dictionary<string, int>> getOrder = null,
            Action<Dictionary<string, int>> setOrder = null,
            Func<Dictionary<string, GridAlignment>> getCellAlignments = null,
            Action<Dictionary<string, GridAlignment>> setCellAlignments = null,
            Func<GridAlignment> getDefaultCellAlignment = null,
            Func<Dictionary<string, GridVerticalAlignment>> getCellVerticalAlignments = null,
            Action<Dictionary<string, GridVerticalAlignment>> setCellVerticalAlignments = null,
            Func<GridVerticalAlignment> getDefaultCellVerticalAlignment = null,
            Func<Dictionary<string, GridAlignment>> getHeaderHorizontalAlignments = null,
            Action<Dictionary<string, GridAlignment>> setHeaderHorizontalAlignments = null,
            Func<GridAlignment> getDefaultHeaderHorizontalAlignment = null,
            Action applyCellAlignments = null,
            Func<string, double, bool> isRuntimeDefaultWidth = null)
        {
            _grid = grid ?? throw new ArgumentNullException(nameof(grid));
            _logger = logger;
            _getWidths = getWidths;
            _setWidths = setWidths;
            _getVisibility = getVisibility;
            _setVisibility = setVisibility;
            _saveSettings = saveSettings;
            _defaultWidthSeeds = defaultWidthSeeds ?? new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            _getOrder = getOrder;
            _setOrder = setOrder;
            _getCellAlignments = getCellAlignments;
            _setCellAlignments = setCellAlignments;
            _getDefaultCellAlignment = getDefaultCellAlignment;
            _getCellVerticalAlignments = getCellVerticalAlignments;
            _setCellVerticalAlignments = setCellVerticalAlignments;
            _getDefaultCellVerticalAlignment = getDefaultCellVerticalAlignment;
            _getHeaderHorizontalAlignments = getHeaderHorizontalAlignments;
            _setHeaderHorizontalAlignments = setHeaderHorizontalAlignments;
            _getDefaultHeaderHorizontalAlignment = getDefaultHeaderHorizontalAlignment;
            _applyCellAlignments = applyCellAlignments;
            _isRuntimeDefaultWidth = isRuntimeDefaultWidth;
        }

        /// <summary>
        /// Attaches handlers and applies persisted settings.
        /// </summary>
        public void Attach()
        {
            if (_isAttached || _grid == null)
            {
                return;
            }

            _isAttached = true;
            BeginInitialRenderSuppression();
            InitializeTimer();
            AttachWidthChangeHandlers();
            AttachNormalizationHandlers();
            QueueScrollViewerAttach(DispatcherPriority.Loaded);
            ApplyPersistedOrder();
            ApplyPersistedVisibility();
            ApplyPersistedWidths();
        }

        /// <summary>
        /// Detaches handlers and flushes pending updates.
        /// </summary>
        public void Detach()
        {
            if (!_isAttached)
            {
                return;
            }

            FlushPendingUpdates();

            foreach (var pair in _columnWidthChangedHandlers.ToList())
            {
                TryDetachWidthChangedHandler(pair.Key, pair.Value);
            }

            _columnWidthChangedHandlers.Clear();
            _pendingWidthUpdates.Clear();
            _resizeObservedWidths.Clear();
            _normalizedWidthOverrides.Clear();
            DetachNormalizationHandlers();
            DetachScrollViewerHandlers();
            CancelQueuedNormalization();

            if (_saveTimer != null)
            {
                _saveTimer.Stop();
                _saveTimer.Tick -= SaveTimer_Tick;
                _saveTimer = null;
            }

            _isAttached = false;
            _normalizationQueued = false;
            _queuedNormalizationRescaleAll = false;
            _initialNormalizationActive = false;
            _initialNormalizationCompleted = false;
            _initialNormalizationAttempts = 0;
            _hasSuccessfulNormalization = false;
            _scrollViewerAttachQueued = false;
            _lastObservedScrollViewerWidth = 0;
            _lastResizedColumnKey = null;
            _lastResizeAbsorberColumnKey = null;
            _resizeBoundaryLeftColumnKey = null;
            _resizeBoundaryRightColumnKey = null;
            RestoreInitialRenderSuppression();
        }

        /// <summary>
        /// Refreshes persisted settings (call when settings change externally).
        /// </summary>
        public void Refresh()
        {
            if (!_isAttached)
            {
                return;
            }

            ApplyPersistedVisibility();
            ApplyPersistedOrder();
            ApplyPersistedWidths();
        }

        /// <summary>
        /// Normalizes columns to fill the container width.
        /// </summary>
        public void NormalizeToContainer(bool rescaleAll = false)
        {
            ApplyCurrentLayoutMode(rescaleAll);
        }

        private void InitializeTimer()
        {
            _saveTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(350)
            };
            _saveTimer.Tick += SaveTimer_Tick;
        }

        private void AttachWidthChangeHandlers()
        {
            if (_grid == null)
            {
                return;
            }

            foreach (var column in _grid.Columns)
            {
                AttachWidthChangedHandler(column);
            }
        }

        private void AttachWidthChangedHandler(DataGridColumn column)
        {
            if (column == null || _columnWidthChangedHandlers.ContainsKey(column))
            {
                return;
            }

            var descriptor = DependencyPropertyDescriptor.FromProperty(DataGridColumn.WidthProperty, typeof(DataGridColumn));
            if (descriptor == null)
            {
                return;
            }

            EventHandler handler = (_, __) => OnColumnWidthChanged(column);
            descriptor.AddValueChanged(column, handler);
            _columnWidthChangedHandlers[column] = handler;
        }

        private void TryDetachWidthChangedHandler(DataGridColumn column, EventHandler handler)
        {
            if (column == null || handler == null)
            {
                return;
            }

            var descriptor = DependencyPropertyDescriptor.FromProperty(DataGridColumn.WidthProperty, typeof(DataGridColumn));
            descriptor?.RemoveValueChanged(column, handler);
        }

        private void BeginInitialRenderSuppression()
        {
            _initialNormalizationActive = DelayInitialRenderUntilNormalized;
            _initialNormalizationCompleted = !DelayInitialRenderUntilNormalized;
            _initialNormalizationAttempts = 0;

            if (!DelayInitialRenderUntilNormalized || _grid == null || _isInitialRenderSuppressed)
            {
                return;
            }

            _initialOpacityLocalValue = _grid.ReadLocalValue(UIElement.OpacityProperty);
            _initialHitTestVisibleLocalValue = _grid.ReadLocalValue(UIElement.IsHitTestVisibleProperty);
            _grid.Opacity = 0d;
            _grid.IsHitTestVisible = false;
            _isInitialRenderSuppressed = true;
        }

        private void CompleteInitialNormalization()
        {
            if (!_initialNormalizationActive && !_isInitialRenderSuppressed)
            {
                return;
            }

            _initialNormalizationActive = false;
            _initialNormalizationCompleted = true;
            _initialNormalizationAttempts = 0;
            RestoreInitialRenderSuppression();
        }

        private void RevealInitialRenderAfterRetryLimit()
        {
            if (!_initialNormalizationActive && !_isInitialRenderSuppressed)
            {
                return;
            }

            _logger?.Warn("Initial DataGrid column normalization did not complete before the retry limit. Revealing the current layout.");
            _initialNormalizationActive = false;
            _initialNormalizationCompleted = false;
            _initialNormalizationAttempts = 0;
            RestoreInitialRenderSuppression();
        }

        private void RestoreInitialRenderSuppression()
        {
            if (!_isInitialRenderSuppressed || _grid == null)
            {
                return;
            }

            RestoreLocalValue(_grid, UIElement.OpacityProperty, _initialOpacityLocalValue);
            RestoreLocalValue(_grid, UIElement.IsHitTestVisibleProperty, _initialHitTestVisibleLocalValue);
            _initialOpacityLocalValue = DependencyProperty.UnsetValue;
            _initialHitTestVisibleLocalValue = DependencyProperty.UnsetValue;
            _isInitialRenderSuppressed = false;
        }

        private static void RestoreLocalValue(DependencyObject target, DependencyProperty property, object value)
        {
            if (target == null || property == null)
            {
                return;
            }

            if (value == DependencyProperty.UnsetValue)
            {
                target.ClearValue(property);
                return;
            }

            target.SetValue(property, value);
        }

        private bool AttachScrollViewerHandlers()
        {
            if (_grid == null)
            {
                return false;
            }

            var scrollViewer = VisualTreeHelpers.FindVisualChild<ScrollViewer>(_grid);
            if (scrollViewer == null)
            {
                return false;
            }

            if (ReferenceEquals(scrollViewer, _normalizationScrollViewer))
            {
                return true;
            }

            DetachScrollViewerHandlers();
            _normalizationScrollViewer = scrollViewer;
            _lastObservedScrollViewerWidth = ResolveScrollViewerObservedWidth(scrollViewer);
            scrollViewer.ScrollChanged += ScrollViewer_ScrollChanged;
            scrollViewer.SizeChanged += ScrollViewer_SizeChanged;
            _grid.LayoutUpdated -= Grid_LayoutUpdated;
            return true;
        }

        private void QueueScrollViewerAttach(DispatcherPriority priority)
        {
            if (_grid == null || _scrollViewerAttachQueued)
            {
                return;
            }

            _scrollViewerAttachQueued = true;
            _grid.Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    _scrollViewerAttachQueued = false;
                    if (!_isAttached || _grid == null)
                    {
                        return;
                    }

                    AttachScrollViewerHandlers();
                }),
                priority);
        }

        private void DetachScrollViewerHandlers()
        {
            if (_normalizationScrollViewer != null)
            {
                _normalizationScrollViewer.ScrollChanged -= ScrollViewer_ScrollChanged;
                _normalizationScrollViewer.SizeChanged -= ScrollViewer_SizeChanged;
                _normalizationScrollViewer = null;
            }

            _lastObservedScrollViewerWidth = 0;
        }

        private void ScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (Math.Abs(e.ViewportWidthChange) <= LayoutWidthChangeThreshold &&
                Math.Abs(e.ExtentWidthChange) <= LayoutWidthChangeThreshold)
            {
                return;
            }

            NormalizeAfterScrollViewerWidthChange(sender as ScrollViewer);
        }

        private void ScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!e.WidthChanged)
            {
                return;
            }

            NormalizeAfterScrollViewerWidthChange(sender as ScrollViewer);
        }

        private void NormalizeAfterScrollViewerWidthChange(ScrollViewer scrollViewer)
        {
            if (!_isAttached ||
                _grid == null ||
                !_grid.IsVisible ||
                _isResizeInProgress ||
                scrollViewer == null)
            {
                return;
            }

            var width = ResolveScrollViewerObservedWidth(scrollViewer);
            if (!IsValidWidth(width))
            {
                return;
            }

            if (!_initialNormalizationActive &&
                IsValidWidth(_lastObservedScrollViewerWidth) &&
                Math.Abs(width - _lastObservedScrollViewerWidth) <= LayoutWidthChangeThreshold)
            {
                return;
            }

            _lastObservedScrollViewerWidth = width;
            ApplyViewportLayoutChange(DispatcherPriority.Render);
        }

        private static double ResolveScrollViewerObservedWidth(ScrollViewer scrollViewer)
        {
            if (scrollViewer == null)
            {
                return 0;
            }

            return IsValidWidth(scrollViewer.ViewportWidth)
                ? scrollViewer.ViewportWidth
                : scrollViewer.ActualWidth;
        }

        private void AttachNormalizationHandlers()
        {
            if (_grid == null)
            {
                return;
            }

            _grid.Loaded -= Grid_Loaded;
            _grid.Loaded += Grid_Loaded;
            _grid.LayoutUpdated -= Grid_LayoutUpdated;
            _grid.LayoutUpdated += Grid_LayoutUpdated;
            _grid.IsVisibleChanged -= Grid_IsVisibleChanged;
            _grid.IsVisibleChanged += Grid_IsVisibleChanged;
            _grid.SizeChanged -= Grid_SizeChanged;
            _grid.SizeChanged += Grid_SizeChanged;
            _grid.PreviewMouseLeftButtonDown -= Grid_PreviewMouseLeftButtonDown;
            _grid.PreviewMouseLeftButtonDown += Grid_PreviewMouseLeftButtonDown;
            _grid.PreviewMouseLeftButtonUp -= Grid_PreviewMouseLeftButtonUp;
            _grid.PreviewMouseLeftButtonUp += Grid_PreviewMouseLeftButtonUp;
            _grid.LostMouseCapture -= Grid_LostMouseCapture;
            _grid.LostMouseCapture += Grid_LostMouseCapture;
            _grid.ColumnReordered -= Grid_ColumnReordered;
            _grid.ColumnReordered += Grid_ColumnReordered;
        }

        private void DetachNormalizationHandlers()
        {
            if (_grid == null)
            {
                return;
            }

            _grid.Loaded -= Grid_Loaded;
            _grid.LayoutUpdated -= Grid_LayoutUpdated;
            _grid.IsVisibleChanged -= Grid_IsVisibleChanged;
            _grid.SizeChanged -= Grid_SizeChanged;
            _grid.PreviewMouseLeftButtonDown -= Grid_PreviewMouseLeftButtonDown;
            _grid.PreviewMouseLeftButtonUp -= Grid_PreviewMouseLeftButtonUp;
            _grid.LostMouseCapture -= Grid_LostMouseCapture;
            _grid.ColumnReordered -= Grid_ColumnReordered;
        }

        private void Grid_Loaded(object sender, RoutedEventArgs e)
        {
            AttachScrollViewerHandlers();
            ApplyCurrentLayoutMode(rescaleAll: true);
        }

        private void Grid_LayoutUpdated(object sender, EventArgs e)
        {
            if (_grid == null)
            {
                return;
            }

            if (AttachScrollViewerHandlers())
            {
                _grid.LayoutUpdated -= Grid_LayoutUpdated;
            }
        }

        private void Grid_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.NewValue is bool isVisible && isVisible)
            {
                _lastObservedScrollViewerWidth = 0;
                QueueScrollViewerAttach(DispatcherPriority.Loaded);
                ApplyCurrentLayoutMode(rescaleAll: true, priority: DispatcherPriority.Loaded);
                return;
            }

            _lastObservedScrollViewerWidth = 0;
        }

        private void Grid_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!e.WidthChanged || _grid == null || !_grid.IsVisible || _grid.ActualWidth <= 1)
            {
                return;
            }

            QueueScrollViewerAttach(DispatcherPriority.Render);
            ApplyViewportLayoutChange(DispatcherPriority.Render);
        }

        private void Grid_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (VisualTreeHelpers.TryFindColumnResizeThumb(e.OriginalSource as DependencyObject, out var resizeThumb))
            {
                CancelQueuedNormalization();
                _isResizeInProgress = true;
                CaptureResizeBoundary(resizeThumb);
                CaptureResizeObservedWidths();
            }
        }

        private void Grid_PreviewMouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            CompleteResizeNormalization();
        }

        private void Grid_LostMouseCapture(object sender, System.Windows.Input.MouseEventArgs e)
        {
            CompleteResizeNormalization();
        }

        private void CompleteResizeNormalization()
        {
            if (!_isResizeInProgress)
            {
                return;
            }

            _isResizeInProgress = false;
            _grid?.Dispatcher.BeginInvoke(new Action(PersistPendingResizeWidths), DispatcherPriority.Background);
        }

        private void OnColumnWidthChanged(DataGridColumn column)
        {
            if (_isApplyingWidths || !_isResizeInProgress || column == null || !column.CanUserResize)
            {
                return;
            }

            var key = GetColumnKey(column);
            if (string.IsNullOrWhiteSpace(key))
            {
                return;
            }

            var width = GetInteractiveColumnWidth(column);
            if (!IsValidWidth(width))
            {
                return;
            }

            var previousWidth = _resizeObservedWidths.TryGetValue(key, out var observedWidth)
                ? observedWidth
                : width;
            var delta = width - previousWidth;
            _resizeObservedWidths[key] = width;
            _lastResizedColumnKey = key;
            _lastResizeAbsorberColumnKey = ResolveResizeAbsorberColumnKey(key);
            QueueWidthUpdate(key, width);

            if (Math.Abs(delta) > 0.2)
            {
                ApplyLiveNeighborResize(key, delta);
            }
        }

        private void Grid_ColumnReordered(object sender, DataGridColumnEventArgs e)
        {
            if (_isApplyingOrder || _grid == null || _getOrder == null || _setOrder == null)
            {
                return;
            }

            QueueColumnOrderSave();
        }

        private static double GetInteractiveColumnWidth(DataGridColumn column)
        {
            if (column == null)
            {
                return 0;
            }

            var displayWidth = column.Width.DisplayValue;
            return IsValidWidth(displayWidth)
                ? displayWidth
                : ColumnWidthNormalization.GetCurrentWidth(column);
        }

        private void QueueColumnOrderSave()
        {
            if (_isColumnOrderSaveQueued || _grid == null)
            {
                return;
            }

            _isColumnOrderSaveQueued = true;
            _grid.Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    _isColumnOrderSaveQueued = false;
                    PersistCurrentColumnOrder();
                }),
                DispatcherPriority.Background);
        }

        private void PersistCurrentColumnOrder()
        {
            if (_grid == null || _getOrder == null || _setOrder == null || _isApplyingOrder)
            {
                return;
            }

            var map = _getOrder.Invoke();
            if (map == null)
            {
                map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            }

            var changed = false;
            foreach (var column in _grid.Columns)
            {
                var key = GetColumnKey(column);
                if (string.IsNullOrWhiteSpace(key) || column.DisplayIndex < 0)
                {
                    continue;
                }

                if (!map.TryGetValue(key, out var existing) || existing != column.DisplayIndex)
                {
                    map[key] = column.DisplayIndex;
                    changed = true;
                }
            }

            if (changed)
            {
                _setOrder.Invoke(map);
                _saveSettings?.Invoke();
            }
        }

        private void QueueWidthUpdate(string key, double width)
        {
            if (string.IsNullOrWhiteSpace(key) || !IsValidWidth(width))
            {
                return;
            }

            _pendingWidthUpdates[key] = ColumnWidthNormalization.RoundPixelWidth(width);
            _saveTimer?.Stop();
            _saveTimer?.Start();
        }

        private void SaveTimer_Tick(object sender, EventArgs e)
        {
            _saveTimer?.Stop();
            if (_pendingWidthUpdates.Count == 0)
            {
                return;
            }

            if (_isResizeInProgress)
            {
                _saveTimer?.Start();
                return;
            }

            PersistPendingResizeWidths();
        }

        private void FlushPendingUpdates()
        {
            PersistPendingResizeWidths();
        }

        private void PersistPendingResizeWidths()
        {
            if (_pendingWidthUpdates.Count == 0)
            {
                ClearResizeTracking();
                return;
            }

            _saveTimer?.Stop();

            Dictionary<string, double> normalized;
            if (TryBuildNormalizedWidths(_lastResizedColumnKey, false, out normalized))
            {
                ApplyWidthsByKey(normalized);
                StoreNormalizedWidthOverrides(normalized);
                PersistVisibleWidths(normalized);
                _pendingWidthUpdates.Clear();
                ClearResizeTracking();
                return;
            }

            PersistVisibleWidths(_pendingWidthUpdates);
            _pendingWidthUpdates.Clear();
            ClearResizeTracking();
        }

        private void ApplyPersistedVisibility()
        {
            var map = _getVisibility?.Invoke();
            if (_grid == null)
            {
                return;
            }

            foreach (var column in _grid.Columns)
            {
                var key = GetColumnKey(column);
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                // Force collapse columns that should never be visible in this context
                if (ForcedCollapsedKeys.Contains(key))
                {
                    column.Visibility = Visibility.Collapsed;
                    continue;
                }

                // Skip columns that are excluded from visibility persistence
                if (ExcludedVisibilityKeys.Contains(key))
                {
                    continue;
                }

                if (map != null && map.TryGetValue(key, out var isVisible))
                {
                    column.Visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;
                }
            }

        }

        private void ApplyPersistedOrder()
        {
            var map = _getOrder?.Invoke();
            if (_grid == null || map == null || map.Count == 0)
            {
                return;
            }

            var orderedColumns = _grid.Columns
                .Where(column => column != null)
                .Select(column => new
                {
                    Column = column,
                    Key = GetColumnKey(column),
                    OriginalDisplayIndex = column.DisplayIndex
                })
                .Select(entry => new
                {
                    entry.Column,
                    entry.OriginalDisplayIndex,
                    SavedDisplayIndex = !string.IsNullOrWhiteSpace(entry.Key) && map.TryGetValue(entry.Key, out var displayIndex)
                        ? Math.Max(0, displayIndex)
                        : int.MaxValue
                })
                .OrderBy(entry => entry.SavedDisplayIndex)
                .ThenBy(entry => entry.OriginalDisplayIndex)
                .ToList();

            if (orderedColumns.Count == 0)
            {
                return;
            }

            _isApplyingOrder = true;
            try
            {
                for (var index = 0; index < orderedColumns.Count; index++)
                {
                    var column = orderedColumns[index].Column;
                    if (column.DisplayIndex != index)
                    {
                        column.DisplayIndex = index;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, "Failed to apply persisted column order.");
            }
            finally
            {
                _isApplyingOrder = false;
            }
        }

        private void ApplyPersistedWidths()
        {
            if (_grid == null)
            {
                return;
            }

            _normalizedWidthOverrides.Clear();
            var preferredWidths = BuildEffectivePreferredWidths(includePending: false);
            if (preferredWidths.Count == 0)
            {
                ApplyDefaultWidthsOrQueue();
                return;
            }

            _isApplyingWidths = true;
            try
            {
                foreach (var column in _grid.Columns)
                {
                    if (column == null || !column.CanUserResize)
                    {
                        continue;
                    }

                    var key = GetColumnKey(column);
                    if (string.IsNullOrWhiteSpace(key))
                    {
                        continue;
                    }

                    if (preferredWidths.TryGetValue(key, out var width) && IsValidWidth(width))
                    {
                        column.Width = new DataGridLength(ColumnWidthNormalization.RoundPixelWidth(width), DataGridLengthUnitType.Pixel);
                    }
                }
            }
            finally
            {
                _isApplyingWidths = false;
            }

            NormalizeOrQueue(true);
        }

        private bool NormalizeColumnsToContainer(bool rescaleAll = false)
        {
            if (_grid == null || !_grid.IsLoaded || !_grid.IsVisible)
            {
                return false;
            }

            AttachScrollViewerHandlers();
            if (TryBuildNormalizedWidths(_lastResizedColumnKey, rescaleAll, out var normalized))
            {
                ApplyWidthsByKey(normalized);
                StoreNormalizedWidthOverrides(normalized);
                _hasSuccessfulNormalization = true;
                CompleteInitialNormalization();
                return true;
            }

            return false;
        }

        private void ApplyDefaultWidthsOrQueue(DispatcherPriority priority = DispatcherPriority.Render)
        {
            if (_grid == null || (_grid.IsLoaded && !_grid.IsVisible))
            {
                return;
            }

            if (NormalizeColumnsToContainer(rescaleAll: true))
            {
                return;
            }

            ApplyEqualStarFallbackWidths();
            QueueNormalization(rescaleAll: true, priority);
        }

        private void ApplyEqualStarFallbackWidths()
        {
            if (_grid == null)
            {
                return;
            }

            _isApplyingWidths = true;
            try
            {
                foreach (var column in GetVisibleResizableColumns())
                {
                    column.Width = new DataGridLength(1, DataGridLengthUnitType.Star);
                }
            }
            finally
            {
                _isApplyingWidths = false;
            }
        }

        private bool TryBuildNormalizedWidths(
            string protectedKey,
            bool rescaleAll,
            out Dictionary<string, double> normalized)
        {
            var effectiveProtectedKey = protectedKey;
            var effectiveAbsorberKey = _lastResizeAbsorberColumnKey;

            return ColumnWidthNormalization.TryBuildNormalizedWidths(
                _grid,
                effectiveProtectedKey,
                effectiveAbsorberKey,
                rescaleAll,
                BuildNormalizationPreferredWidths(includePending: true),
                fallbackAvailableWidth: 0,
                useEqualWidthForMissing: true,
                out normalized);
        }

        private string ResolveDefaultProtectedColumnKey(out string absorberKey)
        {
            absorberKey = null;
            var columns = GetVisibleResizableColumns();
            if (columns.Count == 0)
            {
                return null;
            }

            absorberKey = GetColumnKey(columns[columns.Count - 1]);
            for (var i = 0; i < columns.Count; i++)
            {
                var key = GetColumnKey(columns[i]);
                if (!string.IsNullOrWhiteSpace(key) &&
                    !ColumnWidthNormalization.KeysEqual(key, absorberKey))
                {
                    return key;
                }
            }

            return absorberKey;
        }

        private void CaptureResizeBoundary(Thumb resizeThumb)
        {
            _resizeBoundaryLeftColumnKey = null;
            _resizeBoundaryRightColumnKey = null;

            var header = VisualTreeHelpers.FindVisualParent<DataGridColumnHeader>(resizeThumb);
            var column = header?.Column;
            if (column == null)
            {
                return;
            }

            if (string.Equals(resizeThumb.Name, "PART_RightHeaderGripper", StringComparison.Ordinal))
            {
                _resizeBoundaryLeftColumnKey = GetColumnKey(column);
                _resizeBoundaryRightColumnKey = FindNeighborColumnKey(column, direction: 1);
                return;
            }

            if (string.Equals(resizeThumb.Name, "PART_LeftHeaderGripper", StringComparison.Ordinal))
            {
                _resizeBoundaryLeftColumnKey = FindNeighborColumnKey(column, direction: -1);
                _resizeBoundaryRightColumnKey = GetColumnKey(column);
            }
        }

        private void CaptureResizeObservedWidths()
        {
            _resizeObservedWidths.Clear();
            if (_grid == null)
            {
                return;
            }

            foreach (var column in GetVisibleResizableColumns())
            {
                var key = GetColumnKey(column);
                var width = ColumnWidthNormalization.GetCurrentWidth(column);
                if (!string.IsNullOrWhiteSpace(key) && IsValidWidth(width))
                {
                    _resizeObservedWidths[key] = width;
                }
            }
        }

        private void ClearResizeTracking()
        {
            _resizeObservedWidths.Clear();
            _lastResizedColumnKey = null;
            _lastResizeAbsorberColumnKey = null;
            _resizeBoundaryLeftColumnKey = null;
            _resizeBoundaryRightColumnKey = null;
        }

        private void ApplyLiveNeighborResize(string resizedColumnKey, double resizedDelta)
        {
            if (_grid == null || string.IsNullOrWhiteSpace(resizedColumnKey) || !IsValidWidth(Math.Abs(resizedDelta)))
            {
                return;
            }

            var columns = GetVisibleResizableColumns();
            if (columns.Count == 0)
            {
                return;
            }

            var keys = columns.Select(GetColumnKey).ToList();
            var absorberOrder = ColumnWidthNormalization.BuildAbsorberOrder(
                keys,
                resizedColumnKey,
                _lastResizeAbsorberColumnKey);
            if (absorberOrder.Count == 0)
            {
                return;
            }

            _isApplyingWidths = true;
            try
            {
                if (resizedDelta > 0)
                {
                    var remaining = resizedDelta;
                    foreach (var index in absorberOrder)
                    {
                        if (remaining <= 0.2)
                        {
                            break;
                        }

                        var column = columns[index];
                        var currentWidth = ColumnWidthNormalization.GetCurrentWidth(column);
                        var minimumWidth = ColumnWidthNormalization.ResolveColumnMinimumWidth(column, Math.Max(1, column.MinWidth));
                        var capacity = Math.Max(0, currentWidth - minimumWidth);
                        if (capacity <= 0)
                        {
                            continue;
                        }

                        var take = Math.Min(capacity, remaining);
                        SetLiveColumnWidth(column, currentWidth - take);
                        remaining -= take;
                    }
                }
                else
                {
                    var column = columns[absorberOrder[0]];
                    var currentWidth = ColumnWidthNormalization.GetCurrentWidth(column);
                    SetLiveColumnWidth(column, currentWidth - resizedDelta);
                }
            }
            finally
            {
                _isApplyingWidths = false;
            }
        }

        private void SetLiveColumnWidth(DataGridColumn column, double width)
        {
            var key = GetColumnKey(column);
            if (string.IsNullOrWhiteSpace(key) || !IsValidWidth(width))
            {
                return;
            }

            var rounded = ColumnWidthNormalization.RoundPixelWidth(width);
            column.Width = new DataGridLength(rounded, DataGridLengthUnitType.Pixel);
            _resizeObservedWidths[key] = rounded;
            QueueWidthUpdate(key, rounded);
        }

        private string ResolveResizeAbsorberColumnKey(string resizedColumnKey)
        {
            if (string.IsNullOrWhiteSpace(resizedColumnKey))
            {
                return null;
            }

            if (ColumnWidthNormalization.KeysEqual(resizedColumnKey, _resizeBoundaryLeftColumnKey))
            {
                return _resizeBoundaryRightColumnKey;
            }

            if (ColumnWidthNormalization.KeysEqual(resizedColumnKey, _resizeBoundaryRightColumnKey))
            {
                return _resizeBoundaryLeftColumnKey;
            }

            return FindNeighborColumnKey(resizedColumnKey, direction: 1)
                   ?? FindNeighborColumnKey(resizedColumnKey, direction: -1);
        }

        private string FindNeighborColumnKey(DataGridColumn column, int direction)
        {
            if (column == null)
            {
                return null;
            }

            return FindNeighborColumnKey(GetColumnKey(column), direction);
        }

        private string FindNeighborColumnKey(string columnKey, int direction)
        {
            if (_grid == null || string.IsNullOrWhiteSpace(columnKey) || direction == 0)
            {
                return null;
            }

            var columns = _grid.Columns
                .Where(c => c != null &&
                            c.Visibility == Visibility.Visible &&
                            c.CanUserResize &&
                            !string.IsNullOrWhiteSpace(GetColumnKey(c)))
                .OrderBy(c => c.DisplayIndex)
                .ToList();
            var index = columns.FindIndex(c => ColumnWidthNormalization.KeysEqual(GetColumnKey(c), columnKey));
            if (index < 0)
            {
                return null;
            }

            var nextIndex = index + Math.Sign(direction);
            return nextIndex >= 0 && nextIndex < columns.Count
                ? GetColumnKey(columns[nextIndex])
                : null;
        }

        private List<DataGridColumn> GetVisibleResizableColumns()
        {
            if (_grid == null)
            {
                return new List<DataGridColumn>();
            }

            return _grid.Columns
                .Where(c => c != null &&
                            c.Visibility == Visibility.Visible &&
                            c.CanUserResize &&
                            !string.IsNullOrWhiteSpace(GetColumnKey(c)))
                .OrderBy(c => c.DisplayIndex)
                .ToList();
        }

        private void ApplyWidthsByKey(Dictionary<string, double> widthsByKey)
        {
            if (_grid == null || widthsByKey == null || widthsByKey.Count == 0)
            {
                return;
            }

            ColumnWidthNormalization.ApplyWidthsByKey(_grid, widthsByKey, ref _isApplyingWidths);
        }

        private void NormalizeOrQueue(bool rescaleAll, DispatcherPriority priority = DispatcherPriority.Render)
        {
            if (!NormalizeColumnsToContainer(rescaleAll))
            {
                QueueNormalization(rescaleAll, priority);
            }
        }

        private void ApplyCurrentLayoutMode(bool rescaleAll, DispatcherPriority priority = DispatcherPriority.Render)
        {
            if (_grid == null || (_grid.IsLoaded && !_grid.IsVisible))
            {
                return;
            }

            if (!HasUserPersistedWidths())
            {
                ApplyDefaultWidthsOrQueue(priority);
                return;
            }

            NormalizeOrQueue(ShouldRescaleAll(rescaleAll), priority);
        }

        private void ApplyViewportLayoutChange(DispatcherPriority priority)
        {
            if (_grid == null || (_grid.IsLoaded && !_grid.IsVisible))
            {
                return;
            }

            if (!_hasSuccessfulNormalization || _initialNormalizationActive || !HasUserPersistedWidths())
            {
                ApplyCurrentLayoutMode(rescaleAll: true, priority);
                return;
            }

            QueueNormalization(rescaleAll: true, DispatcherPriority.Background);
        }

        private bool ShouldRescaleAll(bool requestedRescaleAll)
        {
            return requestedRescaleAll;
        }

        private void QueueNormalization(bool rescaleAll, DispatcherPriority priority = DispatcherPriority.Render)
        {
            if (_grid == null)
            {
                return;
            }

            _queuedNormalizationRescaleAll |= rescaleAll;
            if (_normalizationQueued)
            {
                return;
            }

            _normalizationQueued = true;
            _queuedNormalizationOperation = _grid.Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    _queuedNormalizationOperation = null;
                    _normalizationQueued = false;
                    var shouldRescaleAll = ShouldRescaleAll(_queuedNormalizationRescaleAll);
                    _queuedNormalizationRescaleAll = false;

                    if (!_isAttached ||
                        _grid == null ||
                        (_grid.IsLoaded && !_grid.IsVisible) ||
                        _isResizeInProgress)
                    {
                        return;
                    }

                    if (!NormalizeColumnsToContainer(shouldRescaleAll))
                    {
                        QueueInitialNormalizationRetry(shouldRescaleAll);
                    }
                }),
                priority);
        }

        private void CancelQueuedNormalization()
        {
            if (_queuedNormalizationOperation != null &&
                _queuedNormalizationOperation.Status == DispatcherOperationStatus.Pending)
            {
                _queuedNormalizationOperation.Abort();
            }

            _queuedNormalizationOperation = null;
            _normalizationQueued = false;
            _queuedNormalizationRescaleAll = false;
        }

        private void QueueInitialNormalizationRetry(bool rescaleAll)
        {
            if (!_initialNormalizationActive ||
                _initialNormalizationCompleted ||
                !_isAttached ||
                _grid == null)
            {
                return;
            }

            if (_initialNormalizationAttempts >= InitialNormalizationMaxAttempts)
            {
                RevealInitialRenderAfterRetryLimit();
                return;
            }

            _initialNormalizationAttempts++;
            var priority = ResolveInitialNormalizationRetryPriority();
            QueueScrollViewerAttach(priority);
            QueueNormalization(rescaleAll, priority);
        }

        private DispatcherPriority ResolveInitialNormalizationRetryPriority()
        {
            if (_initialNormalizationAttempts <= 2)
            {
                return DispatcherPriority.Loaded;
            }

            if (_initialNormalizationAttempts <= 5)
            {
                return DispatcherPriority.Render;
            }

            return DispatcherPriority.ApplicationIdle;
        }

        private Dictionary<string, double> BuildEffectivePreferredWidths(bool includePending)
        {
            return BuildPreferredWidths(includePending, includeDefaultSeeds: false);
        }

        private Dictionary<string, double> BuildNormalizationPreferredWidths(bool includePending)
        {
            var result = BuildPreferredWidths(includePending: false, includeDefaultSeeds: true);

            foreach (var pair in _normalizedWidthOverrides)
            {
                if (!string.IsNullOrWhiteSpace(pair.Key) && IsValidWidth(pair.Value))
                {
                    result[pair.Key] = ColumnWidthNormalization.RoundPixelWidth(pair.Value);
                }
            }

            if (_defaultWidthSeeds != null)
            {
                foreach (var pair in _defaultWidthSeeds)
                {
                    var key = (pair.Key ?? string.Empty).Trim();
                    if (string.IsNullOrWhiteSpace(key) ||
                        !IsValidWidth(pair.Value) ||
                        result.ContainsKey(key))
                    {
                        continue;
                    }

                    result[key] = ColumnWidthNormalization.RoundPixelWidth(pair.Value);
                }
            }

            if (includePending)
            {
                foreach (var pair in _pendingWidthUpdates)
                {
                    if (!string.IsNullOrWhiteSpace(pair.Key) && IsValidWidth(pair.Value))
                    {
                        result[pair.Key] = ColumnWidthNormalization.RoundPixelWidth(pair.Value);
                    }
                }
            }

            return result;
        }

        private void StoreNormalizedWidthOverrides(IReadOnlyDictionary<string, double> widthsByKey)
        {
            _normalizedWidthOverrides.Clear();
            if (widthsByKey == null)
            {
                return;
            }

            foreach (var pair in widthsByKey)
            {
                if (!string.IsNullOrWhiteSpace(pair.Key) && IsValidWidth(pair.Value))
                {
                    _normalizedWidthOverrides[pair.Key] = ColumnWidthNormalization.RoundPixelWidth(pair.Value);
                }
            }
        }

        private Dictionary<string, double> BuildPreferredWidths(bool includePending, bool includeDefaultSeeds)
        {
            var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var map = _getWidths?.Invoke();

            if (map != null)
            {
                foreach (var pair in map)
                {
                    var key = (pair.Key ?? string.Empty).Trim();
                    if (string.IsNullOrWhiteSpace(key) ||
                        !IsValidWidth(pair.Value) ||
                        (!includeDefaultSeeds && IsDefaultSeedWidth(key, pair.Value)))
                    {
                        continue;
                    }

                    result[key] = ColumnWidthNormalization.RoundPixelWidth(pair.Value);
                }
            }

            if (includePending)
            {
                foreach (var pair in _pendingWidthUpdates)
                {
                    if (!string.IsNullOrWhiteSpace(pair.Key) && IsValidWidth(pair.Value))
                    {
                        result[pair.Key] = ColumnWidthNormalization.RoundPixelWidth(pair.Value);
                    }
                }
            }

            return result;
        }

        private bool HasUserPersistedWidths()
        {
            return BuildEffectivePreferredWidths(includePending: false).Count > 0;
        }

        private bool IsDefaultSeedWidth(string key, double width)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return false;
            }

            if (_defaultWidthSeeds != null &&
                _defaultWidthSeeds.TryGetValue(key, out var seed) &&
                IsValidWidth(seed) &&
                Math.Abs(ColumnWidthNormalization.RoundPixelWidth(width) - ColumnWidthNormalization.RoundPixelWidth(seed)) <= 0.2)
            {
                return true;
            }

            return _isRuntimeDefaultWidth?.Invoke(key, width) == true;
        }

        private void PersistVisibleWidths(IReadOnlyDictionary<string, double> widthsByKey)
        {
            if (widthsByKey == null || widthsByKey.Count == 0)
            {
                return;
            }

            var map = _getWidths?.Invoke();
            if (map == null)
            {
                map = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            }

            var changed = false;
            foreach (var pair in widthsByKey)
            {
                if (string.IsNullOrWhiteSpace(pair.Key) || !IsValidWidth(pair.Value))
                {
                    continue;
                }

                var rounded = ColumnWidthNormalization.RoundPixelWidth(pair.Value);
                if (!map.TryGetValue(pair.Key, out var existing) ||
                    Math.Abs(existing - rounded) > 0.1)
                {
                    map[pair.Key] = rounded;
                    changed = true;
                }
            }

            if (changed)
            {
                _setWidths?.Invoke(map);
                _saveSettings?.Invoke();
            }
        }

        /// <summary>
        /// Handles column visibility menu interactions.
        /// </summary>
        public void OnColumnVisibilityChanged(DataGridColumn column, bool isVisible)
        {
            var key = GetColumnKey(column);
            if (string.IsNullOrWhiteSpace(key))
            {
                return;
            }

            var map = _getVisibility?.Invoke();
            if (map != null)
            {
                if (map.TryGetValue(key, out var existing) && existing == isVisible)
                {
                    return;
                }

                map[key] = isVisible;
                _saveSettings?.Invoke();
            }

            ApplyPersistedVisibility();
            ApplyPersistedWidths();
        }

        /// <summary>
        /// Builds a column menu for the grid.
        /// Columns in ExcludedVisibilityKeys are not included in the visibility section.
        /// </summary>
        public ContextMenu BuildColumnVisibilityMenu(DataGridColumn contextColumn = null)
        {
            if (_grid == null)
            {
                return null;
            }

            var menu = new ContextMenu();
            var hasAlignmentSection = AddAlignmentSection(menu, contextColumn);
            var separatorIndex = menu.Items.Count;
            var hasVisibilitySection = AddVisibilitySection(menu);
            if (hasAlignmentSection && hasVisibilitySection)
            {
                menu.Items.Insert(separatorIndex, new Separator { Margin = new Thickness(8, 8, 8, 0) });
            }

            if (!hasAlignmentSection && !hasVisibilitySection)
            {
                return null;
            }

            ContextMenuStyleHelper.ApplyAchievementContextMenuStyle(_grid, menu);
            return menu;
        }

        private bool AddAlignmentSection(ContextMenu menu, DataGridColumn contextColumn)
        {
            if (menu == null || !CanShowAlignmentSection(contextColumn))
            {
                return false;
            }

            var headerText = ResolveColumnDisplayName(contextColumn);
            if (string.IsNullOrWhiteSpace(headerText))
            {
                return false;
            }

            var rowItem = CreateAlignmentButtonRowItem(contextColumn);
            if (rowItem == null)
            {
                return false;
            }

            menu.Items.Add(CreateSectionHeader(headerText));
            menu.Items.Add(rowItem);
            return true;
        }

        private MenuItem CreateAlignmentButtonRowItem(DataGridColumn contextColumn)
        {
            MenuItem item = null;
            Action refreshRow = null;

            refreshRow = () =>
            {
                if (item != null)
                {
                    item.Header = CreateAlignmentButtonRow(contextColumn, refreshRow);
                }
            };

            item = new MenuItem
            {
                StaysOpenOnClick = true,
                Focusable = false,
                Cursor = Cursors.Arrow,
                Style = CreateCenteredCompactMenuItemStyle(new Thickness(8, 0, 8, 0))
            };

            item.Header = CreateAlignmentButtonRow(contextColumn, refreshRow);
            KeyboardNavigation.SetIsTabStop(item, false);
            item.PreviewGotKeyboardFocus += (_, e) =>
            {
                if (e.NewFocus is DependencyObject focused &&
                    item.Header is DependencyObject header &&
                    IsDescendantOf(focused, header))
                {
                    return;
                }

                if (TryFocusFirstAlignmentButton(item))
                {
                    e.Handled = true;
                }
            };

            return item.Header == null ? null : item;
        }

        private FrameworkElement CreateAlignmentButtonRow(DataGridColumn contextColumn, Action refreshRow)
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0)
            };

            var key = GetColumnKey(contextColumn);
            if (string.IsNullOrWhiteSpace(key))
            {
                return null;
            }

            if (CanShowHeaderHorizontalAlignment(contextColumn))
            {
                var hasOverride = TryGetColumnHeaderHorizontalAlignmentOverride(key, out var overrideAlignment);
                var effectiveAlignment = hasOverride ? overrideAlignment : GetDefaultHeaderHorizontalAlignment();
                var description = CreateHorizontalAlignmentDescription(
                    "Header horizontal alignment",
                    effectiveAlignment,
                    hasOverride);

                row.Children.Add(CreateAlignmentButton(
                    CreateHeaderAlignmentIcon(effectiveAlignment, !hasOverride),
                    description,
                    !hasOverride,
                    () =>
                    {
                        CycleColumnHeaderHorizontalAlignment(contextColumn);
                        refreshRow?.Invoke();
                    }));
            }

            if (CanShowCellHorizontalAlignment(contextColumn))
            {
                var hasOverride = TryGetColumnCellAlignmentOverride(key, out var overrideAlignment);
                var effectiveAlignment = hasOverride ? overrideAlignment : GetDefaultCellAlignment();
                var description = CreateHorizontalAlignmentDescription(
                    "Cell horizontal alignment",
                    effectiveAlignment,
                    hasOverride);

                row.Children.Add(CreateAlignmentButton(
                    CreateAlignmentIcon(effectiveAlignment, !hasOverride),
                    description,
                    !hasOverride,
                    () =>
                    {
                        CycleColumnCellAlignment(contextColumn);
                        refreshRow?.Invoke();
                    }));
            }

            if (CanShowCellVerticalAlignment(contextColumn))
            {
                var hasOverride = TryGetColumnCellVerticalAlignmentOverride(key, out var overrideAlignment);
                var effectiveAlignment = hasOverride ? overrideAlignment : GetDefaultCellVerticalAlignment();
                var description = CreateVerticalAlignmentDescription(
                    "Cell vertical alignment",
                    effectiveAlignment,
                    hasOverride);

                row.Children.Add(CreateAlignmentButton(
                    CreateVerticalAlignmentIcon(effectiveAlignment, !hasOverride),
                    description,
                    !hasOverride,
                    () =>
                    {
                        CycleColumnCellVerticalAlignment(contextColumn);
                        refreshRow?.Invoke();
                    }));
            }

            if (row.Children.Count == 0)
            {
                return null;
            }

            if (row.Children[row.Children.Count - 1] is FrameworkElement lastButton)
            {
                lastButton.Margin = new Thickness(0);
            }

            return row;
        }

        private static Button CreateAlignmentButton(
            FrameworkElement icon,
            string description,
            bool isDefault,
            Action onClick)
        {
            if (icon != null)
            {
                icon.HorizontalAlignment = HorizontalAlignment.Center;
                icon.VerticalAlignment = VerticalAlignment.Center;
            }

            var button = new Button
            {
                Width = 34,
                Height = 28,
                MinWidth = 34,
                MinHeight = 28,
                Margin = new Thickness(0, 0, 4, 0),
                Padding = new Thickness(4),
                BorderThickness = isDefault ? new Thickness(1) : new Thickness(1.6),
                Cursor = Cursors.Hand,
                ToolTip = description,
                Content = icon,
                Opacity = 1,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Style = CreateCompactAlignmentButtonStyle()
            };

            SetTextBrushOpacity(button, Control.BorderBrushProperty, isDefault ? 0.42 : 0.92);
            SetTextBrushOpacity(button, Control.BackgroundProperty, isDefault ? 0.05 : 0.10);
            button.SetResourceReference(Control.ForegroundProperty, "TextBrush");
            AutomationProperties.SetName(button, description);

            button.Click += (_, e) =>
            {
                e.Handled = true;
                onClick?.Invoke();
            };

            return button;
        }

        private static bool TryFocusFirstAlignmentButton(MenuItem item)
        {
            if (!(item?.Header is DependencyObject header))
            {
                return false;
            }

            var button = FindDescendant<Button>(header);
            return button != null && button.Focus();
        }

        private static bool IsDescendantOf(DependencyObject current, DependencyObject ancestor)
        {
            while (current != null)
            {
                if (ReferenceEquals(current, ancestor))
                {
                    return true;
                }

                current = VisualTreeHelpers.GetParentForHitTesting(current) ??
                          (current as FrameworkElement)?.Parent;
            }

            return false;
        }

        private static T FindDescendant<T>(DependencyObject current)
            where T : DependencyObject
        {
            if (current == null)
            {
                return null;
            }

            if (current is T typed)
            {
                return typed;
            }

            var childCount = VisualTreeHelper.GetChildrenCount(current);
            for (var i = 0; i < childCount; i++)
            {
                var child = VisualTreeHelper.GetChild(current, i);
                var nested = FindDescendant<T>(child);
                if (nested != null)
                {
                    return nested;
                }
            }

            return null;
        }

        private bool AddVisibilitySection(ContextMenu menu)
        {
            if (menu == null)
            {
                return false;
            }

            var visibilityItems = new List<MenuItem>();

            // Guarantee at least one visible column: keep the sole checked item disabled so it cannot
            // be unchecked. Recomputed on build and after each toggle (the menu stays open across
            // clicks via StaysOpenOnClick).
            Action refreshLastColumnGuard = () =>
            {
                var lockLastColumn = visibilityItems.Count(i => i.IsChecked) <= 1;
                foreach (var visibilityItem in visibilityItems)
                {
                    visibilityItem.IsEnabled = !(lockLastColumn && visibilityItem.IsChecked);
                }
            };

            foreach (var column in _grid.Columns
                         .Where(c => c != null)
                         .OrderBy(c => c.DisplayIndex))
            {
                var key = GetColumnKey(column);
                if (!string.IsNullOrWhiteSpace(key) && ExcludedVisibilityKeys.Contains(key))
                {
                    continue;
                }

                var headerText = ResolveColumnDisplayName(column);
                if (string.IsNullOrWhiteSpace(headerText))
                {
                    continue;
                }

                var targetColumn = column;
                var item = new MenuItem
                {
                    Header = headerText,
                    IsCheckable = true,
                    IsChecked = targetColumn.Visibility == Visibility.Visible,
                    StaysOpenOnClick = true
                };

                item.Click += (_, __) =>
                {
                    var isVisible = item.IsChecked;
                    targetColumn.Visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;
                    OnColumnVisibilityChanged(targetColumn, isVisible);
                    refreshLastColumnGuard();
                };

                visibilityItems.Add(item);
            }

            if (visibilityItems.Count == 0)
            {
                return false;
            }

            refreshLastColumnGuard();

            menu.Items.Add(CreateSectionHeader(ResourceProvider.GetString("LOCColumns")));
            foreach (var item in visibilityItems)
            {
                menu.Items.Add(item);
            }

            return true;
        }

        private bool CanShowAlignmentSection(DataGridColumn column)
        {
            return CanShowCellHorizontalAlignment(column) ||
                   CanShowCellVerticalAlignment(column) ||
                   CanShowHeaderHorizontalAlignment(column);
        }

        private bool CanShowCellHorizontalAlignment(DataGridColumn column)
        {
            return CanShowCellAlignmentControl(column) &&
                   _getCellAlignments != null &&
                   _setCellAlignments != null;
        }

        private bool CanShowCellVerticalAlignment(DataGridColumn column)
        {
            return CanShowCellAlignmentControl(column) &&
                   _getCellVerticalAlignments != null &&
                   _setCellVerticalAlignments != null;
        }

        private bool CanShowHeaderHorizontalAlignment(DataGridColumn column)
        {
            if (column == null || _getHeaderHorizontalAlignments == null || _setHeaderHorizontalAlignments == null)
            {
                return false;
            }

            var key = GetColumnKey(column);
            return !string.IsNullOrWhiteSpace(key) &&
                   !ExcludedCellAlignmentKeys.Contains(key) &&
                   !ExcludedHeaderAlignmentKeys.Contains(key);
        }

        private bool CanShowCellAlignmentControl(DataGridColumn column)
        {
            if (column == null)
            {
                return false;
            }

            var key = GetColumnKey(column);
            return !string.IsNullOrWhiteSpace(key) && !ExcludedCellAlignmentKeys.Contains(key);
        }

        private void CycleColumnCellAlignment(DataGridColumn column)
        {
            var key = GetColumnKey(column);
            if (string.IsNullOrWhiteSpace(key))
            {
                return;
            }

            var map = _getCellAlignments?.Invoke() ??
                      new Dictionary<string, GridAlignment>(StringComparer.OrdinalIgnoreCase);
            var hasOverride = map.TryGetValue(key, out var current);
            var next = GetNextHorizontalAlignmentOverride(hasOverride, current);

            if (next.HasValue)
            {
                map[key] = next.Value;
            }
            else
            {
                map.Remove(key);
            }

            _setCellAlignments?.Invoke(map);
            _saveSettings?.Invoke();
            _applyCellAlignments?.Invoke();
        }

        private void CycleColumnCellVerticalAlignment(DataGridColumn column)
        {
            var key = GetColumnKey(column);
            if (string.IsNullOrWhiteSpace(key))
            {
                return;
            }

            var map = _getCellVerticalAlignments?.Invoke() ??
                      new Dictionary<string, GridVerticalAlignment>(StringComparer.OrdinalIgnoreCase);
            var hasOverride = map.TryGetValue(key, out var current);
            var next = GetNextVerticalAlignmentOverride(hasOverride, current);

            if (next.HasValue)
            {
                map[key] = next.Value;
            }
            else
            {
                map.Remove(key);
            }

            _setCellVerticalAlignments?.Invoke(map);
            _saveSettings?.Invoke();
            _applyCellAlignments?.Invoke();
        }

        private void CycleColumnHeaderHorizontalAlignment(DataGridColumn column)
        {
            var key = GetColumnKey(column);
            if (string.IsNullOrWhiteSpace(key))
            {
                return;
            }

            var map = _getHeaderHorizontalAlignments?.Invoke() ??
                      new Dictionary<string, GridAlignment>(StringComparer.OrdinalIgnoreCase);
            var hasOverride = map.TryGetValue(key, out var current);
            var next = GetNextHorizontalAlignmentOverride(hasOverride, current);

            if (next.HasValue)
            {
                map[key] = next.Value;
            }
            else
            {
                map.Remove(key);
            }

            _setHeaderHorizontalAlignments?.Invoke(map);
            _saveSettings?.Invoke();
            _applyCellAlignments?.Invoke();
        }

        private bool TryGetColumnCellAlignmentOverride(string key, out GridAlignment alignment)
        {
            alignment = GridAlignment.Left;
            var map = _getCellAlignments?.Invoke();
            return !string.IsNullOrWhiteSpace(key) &&
                   map != null &&
                   map.TryGetValue(key, out alignment);
        }

        private bool TryGetColumnCellVerticalAlignmentOverride(string key, out GridVerticalAlignment alignment)
        {
            alignment = GridVerticalAlignment.Center;
            var map = _getCellVerticalAlignments?.Invoke();
            return !string.IsNullOrWhiteSpace(key) &&
                   map != null &&
                   map.TryGetValue(key, out alignment);
        }

        private bool TryGetColumnHeaderHorizontalAlignmentOverride(string key, out GridAlignment alignment)
        {
            alignment = GridAlignment.Center;
            var map = _getHeaderHorizontalAlignments?.Invoke();
            return !string.IsNullOrWhiteSpace(key) &&
                   map != null &&
                   map.TryGetValue(key, out alignment);
        }

        private GridAlignment GetDefaultCellAlignment()
        {
            return _getDefaultCellAlignment?.Invoke() ?? GridAlignment.Left;
        }

        private GridVerticalAlignment GetDefaultCellVerticalAlignment()
        {
            return _getDefaultCellVerticalAlignment?.Invoke() ?? GridVerticalAlignment.Center;
        }

        private GridAlignment GetDefaultHeaderHorizontalAlignment()
        {
            return _getDefaultHeaderHorizontalAlignment?.Invoke() ?? GridAlignment.Center;
        }

        private static GridAlignment? GetNextHorizontalAlignmentOverride(bool hasOverride, GridAlignment current)
        {
            if (!hasOverride)
            {
                return GridAlignment.Left;
            }

            switch (current)
            {
                case GridAlignment.Left:
                    return GridAlignment.Center;
                case GridAlignment.Center:
                    return GridAlignment.Right;
                default:
                    return null;
            }
        }

        private static GridVerticalAlignment? GetNextVerticalAlignmentOverride(bool hasOverride, GridVerticalAlignment current)
        {
            if (!hasOverride)
            {
                return GridVerticalAlignment.Top;
            }

            switch (current)
            {
                case GridVerticalAlignment.Top:
                    return GridVerticalAlignment.Center;
                case GridVerticalAlignment.Center:
                    return GridVerticalAlignment.Bottom;
                default:
                    return null;
            }
        }

        private static string CreateHorizontalAlignmentDescription(
            string target,
            GridAlignment effectiveAlignment,
            bool hasOverride)
        {
            var alignmentText = AlignmentToText(effectiveAlignment);
            return hasOverride
                ? $"{target}: {alignmentText}. Click to change."
                : $"{target}: Default ({alignmentText}). Click to change.";
        }

        private static string CreateVerticalAlignmentDescription(
            string target,
            GridVerticalAlignment effectiveAlignment,
            bool hasOverride)
        {
            var alignmentText = AlignmentToText(effectiveAlignment);
            return hasOverride
                ? $"{target}: {alignmentText}. Click to change."
                : $"{target}: Default ({alignmentText}). Click to change.";
        }

        private static string AlignmentToText(GridAlignment alignment)
        {
            switch (alignment)
            {
                case GridAlignment.Center:
                    return "Center";
                case GridAlignment.Right:
                    return "Right";
                case GridAlignment.Left:
                default:
                    return "Left";
            }
        }

        private static string AlignmentToText(GridVerticalAlignment alignment)
        {
            switch (alignment)
            {
                case GridVerticalAlignment.Top:
                    return "Top";
                case GridVerticalAlignment.Bottom:
                    return "Bottom";
                case GridVerticalAlignment.Center:
                default:
                    return "Center";
            }
        }

        private static MenuItem CreateSectionHeader(string text)
        {
            var textBlock = new TextBlock
            {
                Text = text,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            textBlock.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");

            return new MenuItem
            {
                Header = textBlock,
                Focusable = false,
                IsHitTestVisible = false,
                Style = CreateCompactMenuItemStyle(new Thickness(8, 6, 8, 6))
            };
        }

        private static Style CreateCenteredCompactMenuItemStyle(Thickness margin)
        {
            var style = new Style(typeof(MenuItem));
            style.Setters.Add(new Setter(FrameworkElement.MarginProperty, margin));
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
            style.Setters.Add(new Setter(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch));
            style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Center));
            style.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));
            style.Setters.Add(new Setter(Control.TemplateProperty, CreateCenteredCompactMenuItemTemplate()));
            return style;
        }

        private static ControlTemplate CreateCenteredCompactMenuItemTemplate()
        {
            var template = new ControlTemplate(typeof(MenuItem));

            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            border.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
            border.SetValue(UIElement.SnapsToDevicePixelsProperty, true);

            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.ContentSourceProperty, "Header");
            presenter.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
            presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(presenter);

            template.VisualTree = border;
            return template;
        }

        private static void SetTextBrushOpacity(DependencyObject target, DependencyProperty property, double opacity)
        {
            if (target == null || property == null)
            {
                return;
            }

            var brush = Application.Current?.TryFindResource("TextBrush") as Brush;
            if (brush != null)
            {
                brush = brush.CloneCurrentValue();
                brush.Opacity = opacity;
                if (brush.CanFreeze)
                {
                    brush.Freeze();
                }

                target.SetValue(property, brush);
                return;
            }

            target.SetValue(property, new SolidColorBrush(Color.FromArgb(
                (byte)Math.Max(0, Math.Min(255, opacity * 255)),
                255,
                255,
                255)));
        }

        private static Style CreateCompactAlignmentButtonStyle()
        {
            var style = new Style(typeof(Button));
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(4)));
            style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Center));
            style.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));
            style.Setters.Add(new Setter(Control.TemplateProperty, CreateCompactAlignmentButtonTemplate()));
            return style;
        }

        private static ControlTemplate CreateCompactAlignmentButtonTemplate()
        {
            var template = new ControlTemplate(typeof(Button));

            var border = new FrameworkElementFactory(typeof(Border));
            border.Name = "Chrome";
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
            border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
            border.SetValue(UIElement.OpacityProperty, 0.96);
            border.SetValue(UIElement.SnapsToDevicePixelsProperty, true);

            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.ContentProperty, new TemplateBindingExtension(ContentControl.ContentProperty));
            presenter.SetValue(FrameworkElement.MarginProperty, new TemplateBindingExtension(Control.PaddingProperty));
            presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, new TemplateBindingExtension(Control.HorizontalContentAlignmentProperty));
            presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, new TemplateBindingExtension(Control.VerticalContentAlignmentProperty));
            border.AppendChild(presenter);

            template.VisualTree = border;

            var mouseOverTrigger = new Trigger
            {
                Property = UIElement.IsMouseOverProperty,
                Value = true
            };
            mouseOverTrigger.Setters.Add(new Setter(Border.OpacityProperty, 1.0, "Chrome"));
            template.Triggers.Add(mouseOverTrigger);

            var focusTrigger = new Trigger
            {
                Property = UIElement.IsKeyboardFocusedProperty,
                Value = true
            };
            focusTrigger.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(1.6), "Chrome"));
            template.Triggers.Add(focusTrigger);

            var pressedTrigger = new Trigger
            {
                Property = ButtonBase.IsPressedProperty,
                Value = true
            };
            pressedTrigger.Setters.Add(new Setter(Border.OpacityProperty, 0.86, "Chrome"));
            template.Triggers.Add(pressedTrigger);

            return template;
        }

        private static Style CreateCompactMenuItemStyle(Thickness margin)
        {
            var style = new Style(typeof(MenuItem));
            style.Setters.Add(new Setter(FrameworkElement.MarginProperty, margin));
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
            style.Setters.Add(new Setter(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left));
            style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Left));
            style.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));
            style.Setters.Add(new Setter(Control.TemplateProperty, CreateCompactMenuItemTemplate()));
            return style;
        }

        private static ControlTemplate CreateCompactMenuItemTemplate()
        {
            var template = new ControlTemplate(typeof(MenuItem));
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            border.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
            border.SetValue(UIElement.SnapsToDevicePixelsProperty, true);

            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.ContentSourceProperty, "Header");
            presenter.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
            presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
            presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(presenter);

            template.VisualTree = border;
            return template;
        }

        private static FrameworkElement CreateHeaderAlignmentIcon(GridAlignment alignment, bool isDefault)
        {
            var grid = new Grid
            {
                Width = 20,
                Height = 14,
                Opacity = isDefault ? 0.62 : 1,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            // Header alignment intentionally shows only the header row's top line.
            AddAlignmentLine(grid, alignment, 2, 15);
            return grid;
        }

        private static FrameworkElement CreateAlignmentIcon(GridAlignment alignment, bool isDefault)
        {
            var grid = new Grid
            {
                Width = 20,
                Height = 14,
                Opacity = isDefault ? 0.62 : 1,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            AddAlignmentLine(grid, alignment, 0, 14);
            AddAlignmentLine(grid, alignment, 4, 9);
            AddAlignmentLine(grid, alignment, 8, 12);
            AddAlignmentLine(grid, alignment, 12, 7);

            return grid;
        }

        private static FrameworkElement CreateVerticalAlignmentIcon(GridVerticalAlignment alignment, bool isDefault)
        {
            var grid = new Grid
            {
                Width = 20,
                Height = 16,
                Opacity = isDefault ? 0.62 : 1,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            double[] widths;
            double top;

            switch (alignment)
            {
                case GridVerticalAlignment.Top:
                    widths = new[] { 16.0, 12.0, 8.0 };
                    top = 1.5;
                    break;

                case GridVerticalAlignment.Bottom:
                    widths = new[] { 8.0, 12.0, 16.0 };
                    top = 5.5;
                    break;

                case GridVerticalAlignment.Center:
                default:
                    widths = new[] { 8.0, 16.0, 8.0 };
                    top = 3.5;
                    break;
            }

            AddVerticalPyramidLine(grid, top, widths[0]);
            AddVerticalPyramidLine(grid, top + 4.0, widths[1]);
            AddVerticalPyramidLine(grid, top + 8.0, widths[2]);

            return grid;
        }

        private static void AddVerticalPyramidLine(Grid grid, double top, double width)
        {
            if (grid == null)
            {
                return;
            }

            var line = new Border
            {
                Width = width,
                Height = 1.4,
                CornerRadius = new CornerRadius(0.7),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, top, 0, 0)
            };

            SetTextBrushOpacity(line, Border.BackgroundProperty, 0.88);
            grid.Children.Add(line);
        }

        private static void AddAlignmentLine(Grid grid, GridAlignment alignment, double top, double width)
        {
            var line = new Border
            {
                Width = width,
                Height = 1.4,
                CornerRadius = new CornerRadius(0.7),
                HorizontalAlignment = ToHorizontalAlignment(alignment),
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, top, 0, 0)
            };
            SetTextBrushOpacity(line, Border.BackgroundProperty, 0.88);
            grid.Children.Add(line);
        }


        private static HorizontalAlignment ToHorizontalAlignment(GridAlignment alignment)
        {
            switch (alignment)
            {
                case GridAlignment.Center:
                    return HorizontalAlignment.Center;
                case GridAlignment.Right:
                    return HorizontalAlignment.Right;
                case GridAlignment.Left:
                default:
                    return HorizontalAlignment.Left;
            }
        }

        private static VerticalAlignment ToVerticalAlignment(GridVerticalAlignment alignment)
        {
            switch (alignment)
            {
                case GridVerticalAlignment.Top:
                    return VerticalAlignment.Top;
                case GridVerticalAlignment.Bottom:
                    return VerticalAlignment.Bottom;
                case GridVerticalAlignment.Center:
                default:
                    return VerticalAlignment.Center;
            }
        }

        private static string ResolveHeaderText(object header)
        {
            switch (header)
            {
                case string text:
                    return text;
                case TextBlock textBlock:
                    return textBlock.Text;
                default:
                    return header?.ToString() ?? string.Empty;
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
            return ColumnVisibilityHelper.GetColumnKey(column) ?? string.Empty;
        }

        private static string GetColumnKey(DataGridColumn column)
        {
            if (column == null)
            {
                return null;
            }

            var key = ColumnVisibilityHelper.GetColumnKey(column);
            if (!string.IsNullOrWhiteSpace(key))
            {
                return key;
            }

            if (!string.IsNullOrWhiteSpace(column.SortMemberPath))
            {
                return column.SortMemberPath;
            }

            return null;
        }

        private static bool IsValidWidth(double width)
        {
            return !double.IsNaN(width) && !double.IsInfinity(width) && width > 0;
        }

        public void Dispose()
        {
            Detach();
        }
    }
}
