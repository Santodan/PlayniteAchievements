using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Playnite.SDK;

namespace PlayniteAchievements.Views.Helpers
{
    /// <summary>
    /// Provides reusable column width and visibility persistence for DataGrid controls.
    /// Encapsulates debouncing, normalization, and synchronization logic.
    /// </summary>
    public class ColumnWidthPersistenceService : IDisposable
    {
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
        private DispatcherTimer _saveTimer;
        private bool _isApplyingWidths;
        private bool _isResizeInProgress;
        private string _lastResizedColumnKey;
        private bool _shouldRescaleAllOnInitialLoad;
        private bool _isAttached;

        private const double MinimumColumnWidthRatio = 0.1;
        private const double WidthNormalizationSafetyPadding = 1.0;

        /// <summary>
        /// Creates a new ColumnWidthPersistenceService.
        /// </summary>
        /// <param name="grid">The DataGrid to manage.</param>
        /// <param name="logger">Logger for diagnostics.</param>
        /// <param name="getWidths">Function to get the persisted width dictionary.</param>
        /// <param name="setWidths">Function to set the persisted width dictionary.</param>
        /// <param name="getVisibility">Function to get the persisted visibility dictionary.</param>
        /// <param name="setVisibility">Function to set the persisted visibility dictionary.</param>
        /// <param name="saveSettings">Action to save settings to disk.</param>
        /// <param name="defaultWidthSeeds">Default column widths for new installations.</param>
        public ColumnWidthPersistenceService(
            DataGrid grid,
            ILogger logger,
            Func<Dictionary<string, double>> getWidths,
            Action<Dictionary<string, double>> setWidths,
            Func<Dictionary<string, bool>> getVisibility,
            Action<Dictionary<string, bool>> setVisibility,
            Action saveSettings,
            IReadOnlyDictionary<string, double> defaultWidthSeeds = null)
        {
            _grid = grid ?? throw new ArgumentNullException(nameof(grid));
            _logger = logger;
            _getWidths = getWidths;
            _setWidths = setWidths;
            _getVisibility = getVisibility;
            _setVisibility = setVisibility;
            _saveSettings = saveSettings;
            _defaultWidthSeeds = defaultWidthSeeds ?? new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
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
            InitializeTimer();
            EnsureDefaultSeeds();
            AttachWidthChangeHandlers();
            AttachNormalizationHandlers();
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
            DetachNormalizationHandlers();

            if (_saveTimer != null)
            {
                _saveTimer.Stop();
                _saveTimer.Tick -= SaveTimer_Tick;
                _saveTimer = null;
            }

            _isAttached = false;
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
            ApplyPersistedWidths();
        }

        /// <summary>
        /// Normalizes columns to fill the container width.
        /// </summary>
        public void NormalizeToContainer(bool rescaleAll = false)
        {
            NormalizeColumnsToContainer(rescaleAll);
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

        private void AttachNormalizationHandlers()
        {
            if (_grid == null)
            {
                return;
            }

            _grid.Loaded -= Grid_Loaded;
            _grid.Loaded += Grid_Loaded;
            _grid.SizeChanged -= Grid_SizeChanged;
            _grid.SizeChanged += Grid_SizeChanged;
            _grid.PreviewMouseLeftButtonDown -= Grid_PreviewMouseLeftButtonDown;
            _grid.PreviewMouseLeftButtonDown += Grid_PreviewMouseLeftButtonDown;
            _grid.PreviewMouseLeftButtonUp -= Grid_PreviewMouseLeftButtonUp;
            _grid.PreviewMouseLeftButtonUp += Grid_PreviewMouseLeftButtonUp;
            _grid.LostMouseCapture -= Grid_LostMouseCapture;
            _grid.LostMouseCapture += Grid_LostMouseCapture;
        }

        private void DetachNormalizationHandlers()
        {
            if (_grid == null)
            {
                return;
            }

            _grid.Loaded -= Grid_Loaded;
            _grid.SizeChanged -= Grid_SizeChanged;
            _grid.PreviewMouseLeftButtonDown -= Grid_PreviewMouseLeftButtonDown;
            _grid.PreviewMouseLeftButtonUp -= Grid_PreviewMouseLeftButtonUp;
            _grid.LostMouseCapture -= Grid_LostMouseCapture;
        }

        private void Grid_Loaded(object sender, RoutedEventArgs e)
        {
            var shouldRescaleAll = _shouldRescaleAllOnInitialLoad;
            NormalizeColumnsToContainer(shouldRescaleAll);
            if (shouldRescaleAll && IsValidWidth(GetGridAvailableWidth()))
            {
                _shouldRescaleAllOnInitialLoad = false;
            }
        }

        private void Grid_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!e.WidthChanged || _grid == null || !_grid.IsVisible || _grid.ActualWidth <= 1)
            {
                return;
            }

            var isVisibilityActivation = e.PreviousSize.Width <= 1;
            var shouldRescaleAll = _shouldRescaleAllOnInitialLoad || !isVisibilityActivation;

            _grid.Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    if (_grid.IsLoaded && !_isResizeInProgress)
                    {
                        NormalizeColumnsToContainer(shouldRescaleAll);
                        if (shouldRescaleAll && _shouldRescaleAllOnInitialLoad && IsValidWidth(GetGridAvailableWidth()))
                        {
                            _shouldRescaleAllOnInitialLoad = false;
                        }
                    }
                }),
                DispatcherPriority.Render);
        }

        private void Grid_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (VisualTreeHelpers.IsColumnResizeThumbHit(e.OriginalSource as DependencyObject))
            {
                _isResizeInProgress = true;
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
            _grid?.Dispatcher.BeginInvoke(new Action(() => NormalizeColumnsToContainer()), DispatcherPriority.Background);
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

            var width = column.ActualWidth;
            if (!IsValidWidth(width))
            {
                return;
            }

            _lastResizedColumnKey = key;
            QueueWidthUpdate(key, width);
        }

        private void QueueWidthUpdate(string key, double width)
        {
            if (string.IsNullOrWhiteSpace(key) || !IsValidWidth(width))
            {
                return;
            }

            _pendingWidthUpdates[key] = Math.Round(width, 2);
            _saveTimer?.Stop();
            _saveTimer?.Start();
        }

        private void SaveTimer_Tick(object sender, EventArgs e)
        {
            _saveTimer?.Stop();
            var shouldNormalize = _pendingWidthUpdates.Count > 0;
            FlushPendingUpdates();

            if (shouldNormalize && !_isResizeInProgress)
            {
                NormalizeColumnsToContainer();
            }
        }

        private void FlushPendingUpdates()
        {
            var map = _getWidths?.Invoke();
            if (map == null || _pendingWidthUpdates.Count == 0)
            {
                return;
            }

            var changed = false;
            foreach (var update in _pendingWidthUpdates)
            {
                if (!IsValidWidth(update.Value))
                {
                    continue;
                }

                if (!map.TryGetValue(update.Key, out var existing) || Math.Abs(existing - update.Value) > 0.1)
                {
                    map[update.Key] = update.Value;
                    changed = true;
                }
            }

            _pendingWidthUpdates.Clear();

            if (changed)
            {
                _setWidths?.Invoke(map);
                _saveSettings?.Invoke();
            }
        }

        private void EnsureDefaultSeeds()
        {
            var map = _getWidths?.Invoke();
            if (map == null)
            {
                map = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                _setWidths?.Invoke(map);
            }

            var changed = false;
            foreach (var pair in _defaultWidthSeeds)
            {
                if (!map.TryGetValue(pair.Key, out var width) || !IsValidWidth(width))
                {
                    map[pair.Key] = pair.Value;
                    changed = true;
                }
            }

            if (changed)
            {
                _setWidths?.Invoke(map);
                _shouldRescaleAllOnInitialLoad = true;
                _saveSettings?.Invoke();
            }
        }

        private void ApplyPersistedVisibility()
        {
            var map = _getVisibility?.Invoke();
            if (_grid == null)
            {
                return;
            }

            if (map == null || map.Count == 0)
            {
                NormalizeColumnsToContainer();
                return;
            }

            foreach (var column in _grid.Columns)
            {
                var key = GetColumnKey(column);
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                if (map.TryGetValue(key, out var isVisible))
                {
                    column.Visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;
                }
            }

            NormalizeColumnsToContainer();
        }

        private void ApplyPersistedWidths()
        {
            EnsureDefaultSeeds();

            var map = _getWidths?.Invoke();
            if (_grid == null)
            {
                return;
            }

            if (map == null || map.Count == 0)
            {
                NormalizeColumnsToContainer();
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

                    if (map.TryGetValue(key, out var width) && IsValidWidth(width))
                    {
                        column.Width = new DataGridLength(width, DataGridLengthUnitType.Pixel);
                    }
                }
            }
            finally
            {
                _isApplyingWidths = false;
            }

            NormalizeColumnsToContainer();
        }

        private void NormalizeColumnsToContainer(bool rescaleAll = false)
        {
            if (_grid == null || !_grid.IsLoaded)
            {
                return;
            }

            if (TryBuildNormalizedWidths(_lastResizedColumnKey, rescaleAll, out var normalized))
            {
                ApplyWidthsByKey(normalized);
            }
        }

        private bool TryBuildNormalizedWidths(string protectedKey, bool rescaleAll, out Dictionary<string, double> normalized)
        {
            normalized = null;
            if (_grid == null || _grid.Columns == null || _grid.Columns.Count == 0)
            {
                return false;
            }

            var visibleColumns = _grid.Columns
                .Where(c => c != null && c.Visibility == Visibility.Visible)
                .ToList();
            if (visibleColumns.Count == 0)
            {
                return false;
            }

            var availableWidth = GetGridAvailableWidth();
            if (!IsValidWidth(availableWidth))
            {
                return false;
            }

            var minimumWidth = ResolveMinimumColumnWidth(visibleColumns, availableWidth);
            var minimumWidths = ApplyColumnMinimumWidths(visibleColumns, minimumWidth);

            var keyColumns = visibleColumns
                .Select(c => new
                {
                    Column = c,
                    Key = GetColumnKey(c),
                    MinWidth = GetColumnMinWidth(minimumWidths, c, minimumWidth),
                    IsResizable = c.CanUserResize
                })
                .Where(e => !string.IsNullOrWhiteSpace(e.Key) && e.IsResizable)
                .Select(e => new
                {
                    e.Column,
                    e.Key,
                    e.MinWidth,
                    SeedWidth = ResolveSeedWidth(e.Key, e.Column, e.MinWidth)
                })
                .ToList();

            if (keyColumns.Count == 0)
            {
                return false;
            }

            var fixedWidth = visibleColumns
                .Where(c => string.IsNullOrWhiteSpace(GetColumnKey(c)) || !c.CanUserResize)
                .Sum(c => Math.Max(GetColumnMinWidth(minimumWidths, c, minimumWidth), GetCurrentWidth(c)));

            var targetWidth = Math.Max(0, availableWidth - fixedWidth - WidthNormalizationSafetyPadding);
            if (targetWidth <= 0)
            {
                return false;
            }

            var keys = keyColumns.Select(e => e.Key).ToList();
            var floorWidths = keyColumns.Select(e => e.MinWidth).ToList();
            var widths = keyColumns.Select(e => Math.Max(e.MinWidth, e.SeedWidth)).ToList();

            var totalWidth = widths.Sum();
            var delta = targetWidth - totalWidth;

            if (Math.Abs(delta) > 0.2)
            {
                if (rescaleAll)
                {
                    RescaleProportionally(widths, floorWidths, targetWidth);
                }
                else
                {
                    DistributeDelta(widths, floorWidths, keys, protectedKey, delta, targetWidth);
                }
            }

            normalized = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < keys.Count; i++)
            {
                normalized[keys[i]] = Math.Max(floorWidths[i], widths[i]);
            }

            return true;
        }

        private void ApplyWidthsByKey(Dictionary<string, double> widthsByKey)
        {
            if (_grid == null || widthsByKey == null || widthsByKey.Count == 0)
            {
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

                    if (!widthsByKey.TryGetValue(key, out var width) || !IsValidWidth(width))
                    {
                        continue;
                    }

                    if (Math.Abs(GetCurrentWidth(column) - width) <= 0.2)
                    {
                        continue;
                    }

                    column.Width = new DataGridLength(width, DataGridLengthUnitType.Pixel);
                }
            }
            finally
            {
                _isApplyingWidths = false;
            }
        }

        private double GetGridAvailableWidth()
        {
            var width = _grid?.ActualWidth ?? 0;
            if (!IsValidWidth(width))
            {
                return 0;
            }

            var scrollViewer = VisualTreeHelpers.FindVisualChild<ScrollViewer>(_grid);
            var viewportWidth = scrollViewer?.ViewportWidth ?? 0;
            if (IsValidWidth(viewportWidth))
            {
                return Math.Max(0, viewportWidth);
            }

            var chrome = _grid.BorderThickness.Left + _grid.BorderThickness.Right + _grid.Padding.Left + _grid.Padding.Right + 2;
            width -= chrome;

            if (scrollViewer != null)
            {
                var scrollBarWidth = scrollViewer.ActualWidth - scrollViewer.ViewportWidth;
                if (IsValidWidth(scrollBarWidth))
                {
                    width -= scrollBarWidth;
                }
                else if (scrollViewer.ComputedVerticalScrollBarVisibility == Visibility.Visible ||
                         scrollViewer.VerticalScrollBarVisibility == ScrollBarVisibility.Visible)
                {
                    width -= SystemParameters.VerticalScrollBarWidth;
                }
            }

            return Math.Max(0, width);
        }

        private double ResolveSeedWidth(string key, DataGridColumn column, double fallbackMin)
        {
            var map = _getWidths?.Invoke();
            if (!string.IsNullOrWhiteSpace(key) && map != null && map.TryGetValue(key, out var preferred) && IsValidWidth(preferred))
            {
                return preferred;
            }

            var current = GetCurrentWidth(column);
            return IsValidWidth(current) ? current : fallbackMin;
        }

        private double ResolveMinimumColumnWidth(IReadOnlyList<DataGridColumn> columns, double availableWidth)
        {
            var preferred = Math.Max(1, Math.Round(availableWidth * MinimumColumnWidthRatio, 2));

            if (!IsValidWidth(availableWidth) || columns == null || columns.Count == 0)
            {
                return Math.Max(1, preferred);
            }

            var resizable = columns.Where(c => c != null && c.CanUserResize && !string.IsNullOrWhiteSpace(GetColumnKey(c))).ToList();
            if (resizable.Count == 0)
            {
                return Math.Max(1, preferred);
            }

            var fixedWidth = columns.Where(c => c == null || !c.CanUserResize || string.IsNullOrWhiteSpace(GetColumnKey(c))).Sum(GetCurrentWidth);
            var availableForResizable = Math.Max(1, availableWidth - fixedWidth - WidthNormalizationSafetyPadding);
            var maxFittable = Math.Max(1, availableForResizable / resizable.Count);
            return Math.Max(1, Math.Min(preferred, maxFittable));
        }

        private Dictionary<DataGridColumn, double> ApplyColumnMinimumWidths(IReadOnlyList<DataGridColumn> columns, double minimumWidth)
        {
            var result = new Dictionary<DataGridColumn, double>();
            if (columns == null || !IsValidWidth(minimumWidth))
            {
                return result;
            }

            foreach (var column in columns)
            {
                if (column == null)
                {
                    continue;
                }

                var resolved = ResolveColumnMinWidth(column, minimumWidth);
                result[column] = resolved;

                if (Math.Abs(column.MinWidth - resolved) > 0.2)
                {
                    column.MinWidth = resolved;
                }

                if (!column.CanUserResize)
                {
                    var maxResolved = ResolveColumnMaxWidth(column, resolved);
                    if (Math.Abs(column.MaxWidth - maxResolved) > 0.2)
                    {
                        column.MaxWidth = maxResolved;
                    }
                }
            }

            return result;
        }

        private double ResolveColumnMinWidth(DataGridColumn column, double fallback)
        {
            if (column != null && !column.CanUserResize)
            {
                if (IsValidWidth(column.MinWidth))
                {
                    return column.MinWidth;
                }

                var current = GetCurrentWidth(column);
                if (IsValidWidth(current))
                {
                    return current;
                }
            }

            return fallback;
        }

        private double ResolveColumnMaxWidth(DataGridColumn column, double fallback)
        {
            if (column != null && IsValidWidth(column.MaxWidth))
            {
                return column.MaxWidth;
            }

            var current = GetCurrentWidth(column);
            return IsValidWidth(current) ? current : fallback;
        }

        private static double GetColumnMinWidth(Dictionary<DataGridColumn, double> map, DataGridColumn column, double fallback)
        {
            if (map != null && column != null && map.TryGetValue(column, out var resolved) && IsValidWidth(resolved))
            {
                return resolved;
            }

            return fallback;
        }

        private static double GetCurrentWidth(DataGridColumn column)
        {
            if (column == null)
            {
                return 0;
            }

            if (IsValidWidth(column.ActualWidth))
            {
                return column.ActualWidth;
            }

            var display = column.Width.DisplayValue;
            return IsValidWidth(display) ? display : 0;
        }

        private static void RescaleProportionally(IList<double> widths, IReadOnlyList<double> floors, double target)
        {
            if (widths == null || floors == null || widths.Count == 0 || widths.Count != floors.Count || !IsValidWidth(target))
            {
                return;
            }

            var weights = widths.Select(w => Math.Max(1, w)).ToList();
            var remainingTarget = target;
            var remainingWeight = weights.Sum();
            var remainingMin = floors.Sum();

            for (var i = 0; i < widths.Count; i++)
            {
                var floor = floors[i];
                remainingMin -= floor;
                var next = i == widths.Count - 1 ? remainingTarget : remainingTarget * (weights[i] / remainingWeight);
                next = Math.Max(floor, next);
                var maxForCurrent = remainingTarget - remainingMin;
                if (next > maxForCurrent)
                {
                    next = maxForCurrent;
                }

                widths[i] = next;
                remainingTarget -= next;
                remainingWeight -= weights[i];
            }
        }

        private static void DistributeDelta(IList<double> widths, IReadOnlyList<double> floors, IReadOnlyList<string> keys, string protectedKey, double delta, double target)
        {
            var absorberOrder = BuildAbsorberOrder(keys, protectedKey);
            if (absorberOrder.Count == 0)
            {
                absorberOrder.Add(keys.Count - 1);
            }

            if (delta > 0)
            {
                widths[absorberOrder[0]] += delta;
            }
            else
            {
                foreach (var index in absorberOrder)
                {
                    var capacity = widths[index] - floors[index];
                    if (capacity <= 0)
                    {
                        continue;
                    }

                    var take = Math.Min(capacity, -delta);
                    widths[index] -= take;
                    delta += take;
                    if (delta >= -0.2)
                    {
                        break;
                    }
                }

                if (delta < -0.2)
                {
                    var fallback = absorberOrder[0];
                    var before = widths[fallback];
                    widths[fallback] = Math.Max(floors[fallback], widths[fallback] + delta);
                    delta += widths[fallback] - before;
                }

                if (delta < -0.2)
                {
                    var protectedIndex = -1;
                    for (var i = 0; i < keys.Count; i++)
                    {
                        if (string.Equals(keys[i], protectedKey, StringComparison.OrdinalIgnoreCase))
                        {
                            protectedIndex = i;
                            break;
                        }
                    }

                    if (protectedIndex >= 0)
                    {
                        var capacity = widths[protectedIndex] - floors[protectedIndex];
                        if (capacity > 0)
                        {
                            var take = Math.Min(capacity, -delta);
                            widths[protectedIndex] -= take;
                            delta += take;
                        }
                    }
                }

                if (delta < -0.2)
                {
                    RescaleProportionally(widths, floors, target);
                }
            }
        }

        private static List<int> BuildAbsorberOrder(IReadOnlyList<string> keys, string protectedKey)
        {
            var order = new List<int>();
            if (keys == null || keys.Count == 0)
            {
                return order;
            }

            var preferredIndex = -1;
            for (var i = keys.Count - 1; i >= 0; i--)
            {
                if (!string.Equals(keys[i], protectedKey, StringComparison.OrdinalIgnoreCase))
                {
                    preferredIndex = i;
                    break;
                }
            }

            if (preferredIndex >= 0)
            {
                order.Add(preferredIndex);
            }

            for (var i = keys.Count - 1; i >= 0; i--)
            {
                if (i == preferredIndex || string.Equals(keys[i], protectedKey, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                order.Add(i);
            }

            if (order.Count == 0)
            {
                order.Add(keys.Count - 1);
            }

            return order;
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
        }

        /// <summary>
        /// Builds a column visibility context menu for the grid.
        /// </summary>
        public ContextMenu BuildColumnVisibilityMenu()
        {
            if (_grid == null)
            {
                return null;
            }

            var menu = new ContextMenu();

            foreach (var column in _grid.Columns)
            {
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
                };

                menu.Items.Add(item);
            }

            return menu;
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
