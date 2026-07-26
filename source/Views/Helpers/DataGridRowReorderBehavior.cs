using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace PlayniteAchievements.Views.Helpers
{
    /// <summary>
    /// Host-supplied configuration for <see cref="DataGridRowReorderBehavior"/>. The behavior owns
    /// the drag mechanics; the host supplies the drag payload format, the visual elements for the
    /// drop indicator and drag-count popup, and callbacks that translate dropped keys into a
    /// reorder of the underlying collection.
    /// </summary>
    public sealed class DataGridRowReorderOptions
    {
        /// <summary>Clipboard format identifying this grid's drag payload (a list of string keys).</summary>
        public string DragDataFormat { get; set; }

        /// <summary>Horizontal insert line overlaying the grid; positioned and toggled by the behavior.</summary>
        public Border DropIndicator { get; set; }

        /// <summary>Popup following the cursor while dragging.</summary>
        public Popup DragCountPopup { get; set; }

        /// <summary>Text element inside <see cref="DragCountPopup"/> showing the dragged item count.</summary>
        public TextBlock DragCountText { get; set; }

        /// <summary>Returns true when a row data context participates in reordering.</summary>
        public Func<object, bool> IsReorderableItem { get; set; }

        /// <summary>Maps the dragged items (in source order) to their string keys for the drag payload.</summary>
        public Func<IReadOnlyList<object>, List<string>> ExtractDragKeys { get; set; }

        /// <summary>Moves the keyed items before or after the target item; returns true when a move occurred.</summary>
        public Func<List<string>, object, bool, bool> MoveItemsRelativeToTarget { get; set; }

        /// <summary>Moves the keyed items to the end of the collection; returns true when a move occurred.</summary>
        public Func<List<string>, bool> MoveItemsToEnd { get; set; }

        /// <summary>Optional: restores grid selection for the moved keys after a successful drop.</summary>
        public Action<IReadOnlyList<string>> RestoreSelection { get; set; }

        /// <summary>Optional: invoked when a reorderable row is pressed outside the drag-handle column.</summary>
        public Action<object, MouseButtonEventArgs> RowPressOutsideDragHandle { get; set; }

        /// <summary>Optional: invoked after every drag operation completes (dropped or cancelled).</summary>
        public Action DragCompleted { get; set; }
    }

    /// <summary>
    /// Attached behavior implementing drag-to-reorder for DataGrid rows: drag initiation from the
    /// first display column using the system drag threshold, a drop insert-line indicator, a
    /// drag-count popup that follows the cursor, and edge-pressure auto-scroll while dragging.
    /// Attach by setting <see cref="OptionsProperty"/> from code-behind after InitializeComponent.
    /// </summary>
    public static class DataGridRowReorderBehavior
    {
        public static readonly DependencyProperty OptionsProperty =
            DependencyProperty.RegisterAttached(
                "Options",
                typeof(DataGridRowReorderOptions),
                typeof(DataGridRowReorderBehavior),
                new PropertyMetadata(null, OnOptionsChanged));

        private static readonly DependencyProperty StateProperty =
            DependencyProperty.RegisterAttached(
                "State",
                typeof(ReorderState),
                typeof(DataGridRowReorderBehavior),
                new PropertyMetadata(null));

        public static DataGridRowReorderOptions GetOptions(DependencyObject obj)
        {
            return (DataGridRowReorderOptions)obj.GetValue(OptionsProperty);
        }

        public static void SetOptions(DependencyObject obj, DataGridRowReorderOptions value)
        {
            obj.SetValue(OptionsProperty, value);
        }

        /// <summary>True while a drag started from this grid is in progress.</summary>
        public static bool GetIsDragging(DataGrid grid)
        {
            return (grid?.GetValue(StateProperty) as ReorderState)?.IsDragging == true;
        }

        /// <summary>
        /// Hides the drop indicator and drag-count popup and clears any pending drag start point.
        /// Call when the grid's rows are rebuilt outside of a drag operation.
        /// </summary>
        public static void CancelPendingDrag(DataGrid grid)
        {
            (grid?.GetValue(StateProperty) as ReorderState)?.CancelPendingDrag();
        }

        private static void OnOptionsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (!(d is DataGrid grid))
            {
                return;
            }

            if (grid.GetValue(StateProperty) is ReorderState existing)
            {
                existing.Detach();
                grid.SetValue(StateProperty, null);
            }

            if (e.NewValue is DataGridRowReorderOptions options)
            {
                ValidateOptions(options);
                var state = new ReorderState(grid, options);
                state.Attach();
                grid.SetValue(StateProperty, state);
            }
        }

        private static void ValidateOptions(DataGridRowReorderOptions options)
        {
            if (string.IsNullOrEmpty(options.DragDataFormat) ||
                options.DropIndicator == null ||
                options.DragCountPopup == null ||
                options.DragCountText == null ||
                options.IsReorderableItem == null ||
                options.ExtractDragKeys == null ||
                options.MoveItemsRelativeToTarget == null ||
                options.MoveItemsToEnd == null)
            {
                throw new InvalidOperationException(
                    "DataGridRowReorderOptions requires DragDataFormat, DropIndicator, DragCountPopup, " +
                    "DragCountText, IsReorderableItem, ExtractDragKeys, MoveItemsRelativeToTarget and MoveItemsToEnd.");
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        private sealed class ReorderState
        {
            private const double AutoScrollEdgeThreshold = 64;
            private const double AutoScrollMinStep = 2;
            private const double AutoScrollVariableStep = 14;
            private const double AutoScrollMaxFactor = 3;

            private readonly DataGrid _grid;
            private readonly DataGridRowReorderOptions _options;
            private readonly DispatcherTimer _autoScrollTimer;

            private Point _dragStartPoint;
            private bool _hasDragStartPoint;
            private object _dragAnchorItem;
            private ScrollViewer _scrollViewer;
            private bool _isDragging;
            private int _dragItemCount;

            public ReorderState(DataGrid grid, DataGridRowReorderOptions options)
            {
                _grid = grid;
                _options = options;
                _autoScrollTimer = new DispatcherTimer(DispatcherPriority.Input)
                {
                    Interval = TimeSpan.FromMilliseconds(16)
                };
                _autoScrollTimer.Tick += AutoScrollTimer_Tick;
            }

            public bool IsDragging => _isDragging;

            public void Attach()
            {
                _grid.PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
                _grid.PreviewMouseLeftButtonUp += OnPreviewMouseLeftButtonUp;
                _grid.PreviewMouseMove += OnPreviewMouseMove;
                _grid.DragOver += OnDragOver;
                _grid.DragLeave += OnDragLeave;
                _grid.GiveFeedback += OnGiveFeedback;
                _grid.QueryContinueDrag += OnQueryContinueDrag;
                _grid.Drop += OnDrop;
            }

            public void Detach()
            {
                StopAutoScroll();
                _grid.PreviewMouseLeftButtonDown -= OnPreviewMouseLeftButtonDown;
                _grid.PreviewMouseLeftButtonUp -= OnPreviewMouseLeftButtonUp;
                _grid.PreviewMouseMove -= OnPreviewMouseMove;
                _grid.DragOver -= OnDragOver;
                _grid.DragLeave -= OnDragLeave;
                _grid.GiveFeedback -= OnGiveFeedback;
                _grid.QueryContinueDrag -= OnQueryContinueDrag;
                _grid.Drop -= OnDrop;
            }

            public void CancelPendingDrag()
            {
                HideDropIndicator();
                HideDragCountPopup();
                _hasDragStartPoint = false;
                _dragAnchorItem = null;
            }

            private void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
            {
                _hasDragStartPoint = false;
                _dragAnchorItem = null;

                var row = VisualTreeHelpers.FindVisualParent<DataGridRow>(e.OriginalSource as DependencyObject);
                var clickedItem = row?.DataContext;
                if (clickedItem == null || !_options.IsReorderableItem(clickedItem))
                {
                    return;
                }

                if (!IsDragColumnHit(e.OriginalSource as DependencyObject))
                {
                    _options.RowPressOutsideDragHandle?.Invoke(clickedItem, e);
                    return;
                }

                _dragStartPoint = e.GetPosition(_grid);
                _hasDragStartPoint = true;
                _dragAnchorItem = clickedItem;

                var hasSelectionModifier = (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0;
                if (row.IsSelected &&
                    _grid.SelectedItems.Count > 1 &&
                    !hasSelectionModifier)
                {
                    // Preserve existing multi-selection when drag starts on an already-selected row.
                    e.Handled = true;
                }
            }

            private void OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
            {
                _hasDragStartPoint = false;
                _dragAnchorItem = null;
            }

            private void OnPreviewMouseMove(object sender, MouseEventArgs e)
            {
                if (!_hasDragStartPoint || e.LeftButton != MouseButtonState.Pressed)
                {
                    return;
                }

                var currentPos = e.GetPosition(_grid);
                var delta = currentPos - _dragStartPoint;
                if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
                    Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
                {
                    return;
                }

                var clickedItem = _dragAnchorItem;
                if (clickedItem == null)
                {
                    var row = VisualTreeHelpers.FindVisualParent<DataGridRow>(e.OriginalSource as DependencyObject);
                    var candidate = row?.DataContext;
                    clickedItem = candidate != null && _options.IsReorderableItem(candidate) ? candidate : null;
                }

                if (clickedItem == null)
                {
                    return;
                }

                var selectedRows = _grid.SelectedItems
                    .Cast<object>()
                    .Where(_options.IsReorderableItem)
                    .ToList();
                if (!selectedRows.Contains(clickedItem))
                {
                    selectedRows = new List<object> { clickedItem };
                }

                var orderedRows = selectedRows
                    .OrderBy(item => _grid.Items.IndexOf(item))
                    .ToList();

                var draggedKeys = _options.ExtractDragKeys(orderedRows);
                if (draggedKeys == null || draggedKeys.Count == 0)
                {
                    return;
                }

                var dragData = new DataObject(_options.DragDataFormat, draggedKeys);
                _hasDragStartPoint = false;
                _dragAnchorItem = null;
                _dragItemCount = draggedKeys.Count;
                _isDragging = true;
                ShowDragCountPopup();
                StartAutoScroll();

                try
                {
                    DragDrop.DoDragDrop(_grid, dragData, DragDropEffects.Move);
                }
                finally
                {
                    StopAutoScroll();
                    _isDragging = false;
                    _dragItemCount = 0;
                    HideDragCountPopup();
                    HideDropIndicator();
                    _options.DragCompleted?.Invoke();
                }
            }

            private void OnDragOver(object sender, DragEventArgs e)
            {
                var hasValidDragData = e.Data.GetDataPresent(_options.DragDataFormat);
                e.Effects = hasValidDragData ? DragDropEffects.Move : DragDropEffects.None;
                if (hasValidDragData)
                {
                    EnsureScrollViewer();
                    UpdateDropIndicator(e);
                }
                else
                {
                    HideDropIndicator();
                }

                e.Handled = true;
            }

            private void OnDragLeave(object sender, DragEventArgs e)
            {
                var pointerPosition = Mouse.GetPosition(_grid);
                if (!IsPointWithinGrid(pointerPosition))
                {
                    HideDropIndicator();
                }
            }

            private void OnGiveFeedback(object sender, GiveFeedbackEventArgs e)
            {
                if (!_isDragging)
                {
                    return;
                }

                UpdateDragCountPopupPosition();
                e.UseDefaultCursors = true;
                e.Handled = true;
            }

            private void OnQueryContinueDrag(object sender, QueryContinueDragEventArgs e)
            {
                if (e.Action == DragAction.Continue)
                {
                    ApplyAutoScrollFromCursor();
                    return;
                }

                _isDragging = false;
                _dragItemCount = 0;
                _hasDragStartPoint = false;
                _dragAnchorItem = null;
                StopAutoScroll();
                HideDragCountPopup();
                HideDropIndicator();
            }

            private void OnDrop(object sender, DragEventArgs e)
            {
                HideDropIndicator();
                if (!e.Data.GetDataPresent(_options.DragDataFormat))
                {
                    HideDragCountPopup();
                    return;
                }

                var draggedKeys = (e.Data.GetData(_options.DragDataFormat) as IEnumerable<string>)?.ToList();
                if (draggedKeys == null || draggedKeys.Count == 0)
                {
                    HideDragCountPopup();
                    return;
                }

                var row = VisualTreeHelpers.FindVisualParent<DataGridRow>(e.OriginalSource as DependencyObject);
                bool moved;
                var targetItem = row?.DataContext;
                if (targetItem != null && _options.IsReorderableItem(targetItem))
                {
                    var pos = e.GetPosition(row);
                    var insertAfter = pos.Y > row.ActualHeight / 2.0;
                    moved = _options.MoveItemsRelativeToTarget(draggedKeys, targetItem, insertAfter);
                }
                else
                {
                    moved = _options.MoveItemsToEnd(draggedKeys);
                }

                if (!moved)
                {
                    HideDragCountPopup();
                    return;
                }

                _options.RestoreSelection?.Invoke(draggedKeys);
                _isDragging = false;
                _dragItemCount = 0;
                StopAutoScroll();
                HideDragCountPopup();
                e.Handled = true;
            }

            private static bool IsDragColumnHit(DependencyObject source)
            {
                var cell = VisualTreeHelpers.FindVisualParent<DataGridCell>(source);
                return cell?.Column?.DisplayIndex == 0;
            }

            private void EnsureScrollViewer()
            {
                if (_scrollViewer != null)
                {
                    return;
                }

                _scrollViewer = VisualTreeHelpers.FindVisualChild<ScrollViewer>(_grid);
            }

            private void StartAutoScroll()
            {
                EnsureScrollViewer();
                if (!_autoScrollTimer.IsEnabled)
                {
                    _autoScrollTimer.Start();
                }
            }

            private void StopAutoScroll()
            {
                if (_autoScrollTimer.IsEnabled)
                {
                    _autoScrollTimer.Stop();
                }
            }

            private void AutoScrollTimer_Tick(object sender, EventArgs e)
            {
                ApplyAutoScrollFromCursor();
            }

            private void ApplyAutoScrollFromCursor()
            {
                if (!_isDragging || _scrollViewer == null)
                {
                    return;
                }

                if (!TryGetCursorPositionInGrid(out var cursorPosition))
                {
                    return;
                }

                var delta = CalculateAutoScrollDelta(cursorPosition.Y, _grid.ActualHeight);
                if (Math.Abs(delta) < 0.01)
                {
                    return;
                }

                var currentOffset = _scrollViewer.VerticalOffset;
                var nextOffset = Math.Max(
                    0,
                    Math.Min(_scrollViewer.ScrollableHeight, currentOffset + delta));
                if (Math.Abs(nextOffset - currentOffset) > 0.01)
                {
                    _scrollViewer.ScrollToVerticalOffset(nextOffset);
                }
            }

            private bool TryGetCursorPositionInGrid(out Point position)
            {
                if (!GetCursorPos(out var cursorPoint))
                {
                    position = default;
                    return false;
                }

                position = _grid.PointFromScreen(new Point(cursorPoint.X, cursorPoint.Y));
                return true;
            }

            private static double CalculateAutoScrollDelta(double pointerY, double gridHeight)
            {
                if (gridHeight <= 0)
                {
                    return 0;
                }

                if (pointerY < AutoScrollEdgeThreshold)
                {
                    var pressure = (AutoScrollEdgeThreshold - pointerY) / AutoScrollEdgeThreshold;
                    var factor = Math.Min(AutoScrollMaxFactor, Math.Max(0, pressure));
                    return -(AutoScrollMinStep + (factor * AutoScrollVariableStep));
                }

                var lowerEdge = gridHeight - AutoScrollEdgeThreshold;
                if (pointerY > lowerEdge)
                {
                    var pressure = (pointerY - lowerEdge) / AutoScrollEdgeThreshold;
                    var factor = Math.Min(AutoScrollMaxFactor, Math.Max(0, pressure));
                    return AutoScrollMinStep + (factor * AutoScrollVariableStep);
                }

                return 0;
            }

            private void UpdateDropIndicator(DragEventArgs e)
            {
                var row = VisualTreeHelpers.FindVisualParent<DataGridRow>(e.OriginalSource as DependencyObject);
                var targetItem = row?.DataContext;
                if (targetItem != null && _options.IsReorderableItem(targetItem))
                {
                    var pointerInRow = e.GetPosition(row);
                    var insertAfter = pointerInRow.Y > row.ActualHeight / 2.0;
                    var rowTop = row.TranslatePoint(new Point(0, 0), _grid).Y;
                    var lineY = insertAfter ? rowTop + row.ActualHeight : rowTop;
                    ShowDropIndicator(lineY);
                    return;
                }

                if (_grid.Items.Count > 0)
                {
                    ShowDropIndicator(_grid.ActualHeight - 1);
                }
                else
                {
                    HideDropIndicator();
                }
            }

            private void ShowDropIndicator(double y)
            {
                if (double.IsNaN(y))
                {
                    HideDropIndicator();
                    return;
                }

                var indicator = _options.DropIndicator;
                var maxTop = Math.Max(0, _grid.ActualHeight - indicator.Height);
                var top = Math.Max(0, Math.Min(maxTop, y - (indicator.Height / 2.0)));
                indicator.Margin = new Thickness(0, top, 0, 0);
                indicator.Visibility = Visibility.Visible;
            }

            private void HideDropIndicator()
            {
                _options.DropIndicator.Visibility = Visibility.Collapsed;
            }

            private void ShowDragCountPopup()
            {
                if (_dragItemCount <= 0)
                {
                    return;
                }

                _options.DragCountText.Text = _dragItemCount.ToString();
                _options.DragCountPopup.IsOpen = true;
                UpdateDragCountPopupPosition();
            }

            private void HideDragCountPopup()
            {
                if (_options.DragCountPopup.IsOpen)
                {
                    _options.DragCountPopup.IsOpen = false;
                }
            }

            private void UpdateDragCountPopupPosition()
            {
                if (!GetCursorPos(out var cursorPoint))
                {
                    return;
                }

                _options.DragCountPopup.HorizontalOffset = cursorPoint.X + 18;
                _options.DragCountPopup.VerticalOffset = cursorPoint.Y + 18;
            }

            private bool IsPointWithinGrid(Point point)
            {
                return point.X >= 0 &&
                       point.Y >= 0 &&
                       point.X <= _grid.ActualWidth &&
                       point.Y <= _grid.ActualHeight;
            }
        }
    }
}
