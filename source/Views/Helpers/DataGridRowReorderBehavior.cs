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

        /// <summary>
        /// Optional: restores grid selection for the moved keys after a successful drop. Reorder
        /// drops only - after a nest the dragged keys may be stale (a nest rewrites them), so the
        /// host's own move pipeline is responsible for selection there.
        /// </summary>
        public Action<IReadOnlyList<string>> RestoreSelection { get; set; }

        /// <summary>
        /// Optional nest support, all-or-none with <see cref="CanNestOnTarget"/> and
        /// <see cref="NestItemsOnTarget"/>: row-outline overlay shown while hovering a row's nest
        /// zone; positioned and toggled by the behavior.
        /// </summary>
        public Border NestHighlight { get; set; }

        /// <summary>Optional: whether the dragged keys may nest onto the target row's item.</summary>
        public Func<List<string>, object, bool> CanNestOnTarget { get; set; }

        /// <summary>Optional: nests the keyed items onto the target item; returns true when applied.</summary>
        public Func<List<string>, object, bool> NestItemsOnTarget { get; set; }

        /// <summary>
        /// Optional: left inset for the insert line at (target item, insertAfter), letting a host
        /// whose gaps carry meaning (a tree level) indent the line to say so. Null target = the
        /// end-of-list line. Unset keeps the full-width line.
        /// </summary>
        public Func<object, bool, double> ResolveDropIndicatorInset { get; set; }

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

            var nestMembers = (options.NestHighlight != null ? 1 : 0) +
                              (options.CanNestOnTarget != null ? 1 : 0) +
                              (options.NestItemsOnTarget != null ? 1 : 0);
            if (nestMembers != 0 && nestMembers != 3)
            {
                throw new InvalidOperationException(
                    "DataGridRowReorderOptions nest support requires NestHighlight, CanNestOnTarget " +
                    "and NestItemsOnTarget together.");
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

            // Per-drag caches: the payload keys (stable for the whole drag) and the last hovered
            // row's nest verdict, so DragOver ticks do not re-read the DataObject or re-plan the
            // move per pixel.
            private List<string> _dragOverKeys;
            private object _nestVerdictItem;
            private bool _nestVerdict;

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
                _grid.PreviewDragOver += OnPreviewDragOver;
                _grid.PreviewDrop += OnPreviewDrop;
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
                _grid.PreviewDragOver -= OnPreviewDragOver;
                _grid.PreviewDrop -= OnPreviewDrop;
                _grid.DragOver -= OnDragOver;
                _grid.DragLeave -= OnDragLeave;
                _grid.GiveFeedback -= OnGiveFeedback;
                _grid.QueryContinueDrag -= OnQueryContinueDrag;
                _grid.Drop -= OnDrop;
            }

            public void CancelPendingDrag()
            {
                HideDropVisuals();
                HideDragCountPopup();
                ClearDragCaches();
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
                    HideDropVisuals();
                    ClearDragCaches();
                    _options.DragCompleted?.Invoke();
                }
            }

            // Cells can contain controls with their own drag handling - a TextBox rejects any
            // non-text payload and marks the events handled, which made a row's text boxes dead
            // zones a drag could hover but never drop on. Claiming OUR payload at the tunneling
            // stage keeps the whole row a live target while leaving every other payload (image
            // files or URLs bound for the art box) to the controls that want it.
            private void OnPreviewDragOver(object sender, DragEventArgs e)
            {
                if (!e.Data.GetDataPresent(_options.DragDataFormat))
                {
                    return;
                }

                OnDragOver(sender, e);
            }

            private void OnPreviewDrop(object sender, DragEventArgs e)
            {
                if (!e.Data.GetDataPresent(_options.DragDataFormat))
                {
                    return;
                }

                OnDrop(sender, e);
                e.Handled = true;
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
                    HideDropVisuals();
                }

                e.Handled = true;
            }

            private void OnDragLeave(object sender, DragEventArgs e)
            {
                var pointerPosition = Mouse.GetPosition(_grid);
                if (!IsPointWithinGrid(pointerPosition))
                {
                    HideDropVisuals();
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
                HideDropVisuals();
                ClearDragCaches();
            }

            private void OnDrop(object sender, DragEventArgs e)
            {
                HideDropVisuals();
                if (!e.Data.GetDataPresent(_options.DragDataFormat))
                {
                    HideDragCountPopup();
                    ClearDragCaches();
                    return;
                }

                var draggedKeys = (e.Data.GetData(_options.DragDataFormat) as IEnumerable<string>)?.ToList();
                if (draggedKeys == null || draggedKeys.Count == 0)
                {
                    HideDragCountPopup();
                    ClearDragCaches();
                    return;
                }

                var row = VisualTreeHelpers.FindVisualParent<DataGridRow>(e.OriginalSource as DependencyObject);
                bool moved;
                var isNest = false;
                var targetItem = row?.DataContext;
                if (targetItem != null && _options.IsReorderableItem(targetItem))
                {
                    // The same zone resolution the hover indicator used, so the drop can never
                    // disagree with what the visuals promised.
                    var zone = ResolveDropZone(e, row, targetItem);
                    isNest = zone == DataGridDropZoneKind.NestOnTarget;
                    moved = isNest
                        ? _options.NestItemsOnTarget(draggedKeys, targetItem)
                        : _options.MoveItemsRelativeToTarget(
                            draggedKeys,
                            targetItem,
                            zone == DataGridDropZoneKind.InsertAfter);
                }
                else
                {
                    moved = _options.MoveItemsToEnd(draggedKeys);
                }

                ClearDragCaches();
                if (!moved)
                {
                    HideDragCountPopup();
                    return;
                }

                // A nest rewrites the dragged keys, so restoring selection from them would clear
                // the selection the host's own move pipeline just made.
                if (!isNest)
                {
                    _options.RestoreSelection?.Invoke(draggedKeys);
                }
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
                    var zone = ResolveDropZone(e, row, targetItem);
                    if (zone == DataGridDropZoneKind.NestOnTarget)
                    {
                        HideDropIndicator();
                        ShowNestHighlight(row);
                        return;
                    }

                    HideNestHighlight();
                    var rowTop = row.TranslatePoint(new Point(0, 0), _grid).Y;
                    var insertAfter = zone == DataGridDropZoneKind.InsertAfter;
                    var lineY = insertAfter ? rowTop + row.ActualHeight : rowTop;
                    ShowDropIndicator(
                        lineY,
                        _options.ResolveDropIndicatorInset?.Invoke(targetItem, insertAfter) ?? 0d);
                    return;
                }

                // Empty space below the last row stays reorder-to-end only: there is no row there
                // to become a parent.
                HideNestHighlight();
                if (_grid.Items.Count > 0)
                {
                    ShowDropIndicator(
                        _grid.ActualHeight - 1,
                        _options.ResolveDropIndicatorInset?.Invoke(null, false) ?? 0d);
                }
                else
                {
                    HideDropIndicator();
                }
            }

            /// <summary>
            /// The zone the pointer is in over <paramref name="row"/>. Shared by the hover
            /// indicator and the drop handler; nesting collapses back to the midpoint split when
            /// the host offers no nest support or the target refuses these keys.
            /// </summary>
            private DataGridDropZoneKind ResolveDropZone(DragEventArgs e, DataGridRow row, object targetItem)
            {
                var pointerInRow = e.GetPosition(row);
                return DataGridDropZone.Resolve(pointerInRow.Y, row.ActualHeight, IsNestAllowed(e, targetItem));
            }

            private bool IsNestAllowed(DragEventArgs e, object targetItem)
            {
                if (_options.NestItemsOnTarget == null || targetItem == null)
                {
                    return false;
                }

                if (ReferenceEquals(_nestVerdictItem, targetItem))
                {
                    return _nestVerdict;
                }

                var keys = GetDragKeys(e);
                _nestVerdictItem = targetItem;
                _nestVerdict = keys != null && keys.Count > 0 && _options.CanNestOnTarget(keys, targetItem);
                return _nestVerdict;
            }

            private List<string> GetDragKeys(DragEventArgs e)
            {
                if (_dragOverKeys == null)
                {
                    _dragOverKeys = (e.Data.GetData(_options.DragDataFormat) as IEnumerable<string>)?.ToList();
                }

                return _dragOverKeys;
            }

            private void ClearDragCaches()
            {
                _dragOverKeys = null;
                _nestVerdictItem = null;
                _nestVerdict = false;
            }

            private void ShowDropIndicator(double y, double leftInset = 0d)
            {
                if (double.IsNaN(y))
                {
                    HideDropIndicator();
                    return;
                }

                var indicator = _options.DropIndicator;
                var maxTop = Math.Max(0, _grid.ActualHeight - indicator.Height);
                var top = Math.Max(0, Math.Min(maxTop, y - (indicator.Height / 2.0)));
                if (double.IsNaN(leftInset) || leftInset < 0d)
                {
                    leftInset = 0d;
                }

                indicator.Margin = new Thickness(leftInset, top, 0, 0);
                indicator.Visibility = Visibility.Visible;
            }

            private void HideDropIndicator()
            {
                _options.DropIndicator.Visibility = Visibility.Collapsed;
            }

            private void ShowNestHighlight(DataGridRow row)
            {
                var highlight = _options.NestHighlight;
                if (highlight == null)
                {
                    return;
                }

                // Clamp to the grid so a row half scrolled out lights only its visible part; a
                // sliver too thin to read as a row outline hides instead.
                var top = row.TranslatePoint(new Point(0, 0), _grid).Y;
                var bottom = Math.Min(_grid.ActualHeight, top + row.ActualHeight);
                top = Math.Max(0, top);
                var height = bottom - top;
                if (double.IsNaN(height) || height <= 4)
                {
                    HideNestHighlight();
                    return;
                }

                highlight.Margin = new Thickness(0, top, 0, 0);
                highlight.Height = height;
                highlight.Visibility = Visibility.Visible;
            }

            private void HideNestHighlight()
            {
                if (_options.NestHighlight != null)
                {
                    _options.NestHighlight.Visibility = Visibility.Collapsed;
                }
            }

            private void HideDropVisuals()
            {
                HideDropIndicator();
                HideNestHighlight();
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

                // GetCursorPos reports physical pixels, but an AbsolutePoint popup's offsets are
                // device-independent units - without the transform the badge drifts away from the
                // cursor by the DPI scale factor.
                var cursor = new Point(cursorPoint.X, cursorPoint.Y);
                var transform = PresentationSource.FromVisual(_grid)?.CompositionTarget?.TransformFromDevice;
                if (transform != null)
                {
                    cursor = transform.Value.Transform(cursor);
                }

                _options.DragCountPopup.HorizontalOffset = cursor.X + 18;
                _options.DragCountPopup.VerticalOffset = cursor.Y + 18;
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
