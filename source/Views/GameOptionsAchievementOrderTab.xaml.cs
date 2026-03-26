using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using PlayniteAchievements.Services;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.Views.Helpers;

namespace PlayniteAchievements.Views
{
    public partial class GameOptionsAchievementOrderTab : UserControl
    {
        private const string DragDataFormat = "PlayniteAchievements.GameOptionsAchievementOrderRows";
        private const double AutoScrollEdgeThreshold = 64;
        private const double AutoScrollMinStep = 2;
        private const double AutoScrollVariableStep = 14;
        private const double AutoScrollMaxFactor = 3;

        private Point _dragStartPoint;
        private bool _hasDragStartPoint;
        private AchievementDisplayItem _dragAnchorItem;
        private ScrollViewer _dataGridScrollViewer;
        private readonly DispatcherTimer _autoScrollTimer;
        private bool _isDragging;
        private int _dragItemCount;
        private bool _pendingRefreshRequested;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        public GameOptionsAchievementOrderTab(GameOptionsAchievementOrderViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            _autoScrollTimer = new DispatcherTimer(DispatcherPriority.Input)
            {
                Interval = TimeSpan.FromMilliseconds(16)
            };
            _autoScrollTimer.Tick += AutoScrollTimer_Tick;
        }

        private GameOptionsAchievementOrderViewModel ViewModel => DataContext as GameOptionsAchievementOrderViewModel;

        public void RefreshData()
        {
            if (ViewModel == null)
            {
                return;
            }

            if (_isDragging)
            {
                _pendingRefreshRequested = true;
                return;
            }

            RefreshDataCore(CaptureSelectedApiNames());
        }

        private void Control_Loaded(object sender, RoutedEventArgs e)
        {
            EnsureDataGridScrollViewer();
        }

        private void EnsureDataGridScrollViewer()
        {
            if (_dataGridScrollViewer != null)
            {
                return;
            }

            _dataGridScrollViewer = VisualTreeHelpers.FindVisualChild<ScrollViewer>(AchievementOrderDataGrid);
        }

        private void DataGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _hasDragStartPoint = false;
            _dragAnchorItem = null;

            var row = VisualTreeHelpers.FindVisualParent<DataGridRow>(e.OriginalSource as DependencyObject);
            if (!(row?.DataContext is AchievementDisplayItem clickedItem))
            {
                return;
            }

            var hitDragColumn = IsDragColumnHit(e.OriginalSource as DependencyObject);
            if (!hitDragColumn)
            {
                if (clickedItem.CanReveal)
                {
                    clickedItem.ToggleReveal();
                    e.Handled = true;
                }
                return;
            }

            _dragStartPoint = e.GetPosition(AchievementOrderDataGrid);
            _hasDragStartPoint = true;
            _dragAnchorItem = clickedItem;

            var hasSelectionModifier = (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0;
            if (row.IsSelected &&
                AchievementOrderDataGrid.SelectedItems.Count > 1 &&
                !hasSelectionModifier)
            {
                // Preserve existing multi-selection when drag starts on an already-selected row.
                e.Handled = true;
            }
        }

        private void DataGrid_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _hasDragStartPoint = false;
            _dragAnchorItem = null;
        }

        private void DataGrid_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (!_hasDragStartPoint || e.LeftButton != MouseButtonState.Pressed || ViewModel == null)
            {
                return;
            }

            var currentPos = e.GetPosition(AchievementOrderDataGrid);
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
                clickedItem = row?.DataContext as AchievementDisplayItem;
            }

            if (clickedItem == null)
            {
                return;
            }

            var selectedRows = AchievementOrderDataGrid.SelectedItems
                .OfType<AchievementDisplayItem>()
                .ToList();

            if (!selectedRows.Contains(clickedItem))
            {
                selectedRows = new List<AchievementDisplayItem> { clickedItem };
            }

            var sourceOrder = ViewModel.AchievementRows.ToList();
            selectedRows = selectedRows
                .OrderBy(item => sourceOrder.IndexOf(item))
                .ToList();

            var draggedApiNames = AchievementOrderHelper.NormalizeApiNames(
                selectedRows.Select(item => item.ApiName));
            if (draggedApiNames.Count == 0)
            {
                return;
            }

            var dragData = new DataObject(DragDataFormat, draggedApiNames);
            _hasDragStartPoint = false;
            _dragAnchorItem = null;
            _dragItemCount = draggedApiNames.Count;
            _isDragging = true;
            ShowDragCountPopup();
            StartAutoScroll();

            try
            {
                DragDrop.DoDragDrop(AchievementOrderDataGrid, dragData, DragDropEffects.Move);
            }
            finally
            {
                StopAutoScroll();
                _isDragging = false;
                _dragItemCount = 0;
                HideDragCountPopup();
                HideDropIndicator();
                ApplyPendingRefreshIfNeeded();
            }
        }

        private void DataGrid_DragOver(object sender, DragEventArgs e)
        {
            var hasValidDragData = e.Data.GetDataPresent(DragDataFormat);
            e.Effects = hasValidDragData ? DragDropEffects.Move : DragDropEffects.None;

            if (hasValidDragData)
            {
                EnsureDataGridScrollViewer();
                UpdateDropIndicator(e);
            }
            else
            {
                HideDropIndicator();
            }

            e.Handled = true;
        }

        private void DataGrid_DragLeave(object sender, DragEventArgs e)
        {
            var pointerPosition = Mouse.GetPosition(AchievementOrderDataGrid);
            if (!IsPointWithinDataGrid(pointerPosition))
            {
                HideDropIndicator();
            }
        }

        private void DataGrid_GiveFeedback(object sender, GiveFeedbackEventArgs e)
        {
            if (!_isDragging)
            {
                return;
            }

            UpdateDragCountPopupPosition();
            e.UseDefaultCursors = true;
            e.Handled = true;
        }

        private void DataGrid_QueryContinueDrag(object sender, QueryContinueDragEventArgs e)
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

        private void DataGrid_Drop(object sender, DragEventArgs e)
        {
            HideDropIndicator();

            if (ViewModel == null ||
                !e.Data.GetDataPresent(DragDataFormat))
            {
                HideDragCountPopup();
                return;
            }

            var draggedApiNames = (e.Data.GetData(DragDataFormat) as IEnumerable<string>)?
                .ToList();
            if (draggedApiNames == null || draggedApiNames.Count == 0)
            {
                HideDragCountPopup();
                return;
            }

            var row = VisualTreeHelpers.FindVisualParent<DataGridRow>(e.OriginalSource as DependencyObject);
            var moved = false;

            if (row?.DataContext is AchievementDisplayItem targetItem)
            {
                var pos = e.GetPosition(row);
                var insertAfter = pos.Y > row.ActualHeight / 2.0;
                moved = ViewModel.MoveItemsByApiName(draggedApiNames, targetItem.ApiName, insertAfter);
            }
            else
            {
                moved = ViewModel.MoveItemsToEndByApiName(draggedApiNames);
            }

            if (!moved)
            {
                HideDragCountPopup();
                return;
            }

            RestoreSelectionByApiNames(draggedApiNames);

            _isDragging = false;
            _dragItemCount = 0;
            StopAutoScroll();
            HideDragCountPopup();
            e.Handled = true;
        }

        private void ResetOrderButton_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel?.ResetCustomOrder() != true)
            {
                return;
            }

            HideDropIndicator();
            HideDragCountPopup();
        }

        private void StartAutoScroll()
        {
            EnsureDataGridScrollViewer();
            if (_autoScrollTimer.IsEnabled)
            {
                return;
            }

            _autoScrollTimer.Start();
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
            if (!_isDragging || _dataGridScrollViewer == null)
            {
                return;
            }

            if (!TryGetCursorPositionInDataGrid(out var cursorPosition))
            {
                return;
            }

            var delta = CalculateAutoScrollDelta(cursorPosition.Y, AchievementOrderDataGrid.ActualHeight);
            if (Math.Abs(delta) < 0.01)
            {
                return;
            }

            var currentOffset = _dataGridScrollViewer.VerticalOffset;
            var nextOffset = Math.Max(
                0,
                Math.Min(_dataGridScrollViewer.ScrollableHeight, currentOffset + delta));

            if (Math.Abs(nextOffset - currentOffset) > 0.01)
            {
                _dataGridScrollViewer.ScrollToVerticalOffset(nextOffset);
            }
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

        private bool TryGetCursorPositionInDataGrid(out Point position)
        {
            if (!GetCursorPos(out var cursorPoint))
            {
                position = default;
                return false;
            }

            position = AchievementOrderDataGrid.PointFromScreen(new Point(cursorPoint.X, cursorPoint.Y));
            return true;
        }

        private void UpdateDropIndicator(DragEventArgs e)
        {
            var row = VisualTreeHelpers.FindVisualParent<DataGridRow>(e.OriginalSource as DependencyObject);
            if (row?.DataContext is AchievementDisplayItem)
            {
                var pointerInRow = e.GetPosition(row);
                var insertAfter = pointerInRow.Y > row.ActualHeight / 2.0;
                var rowTop = row.TranslatePoint(new Point(0, 0), AchievementOrderDataGrid).Y;
                var lineY = insertAfter ? rowTop + row.ActualHeight : rowTop;
                ShowDropIndicator(lineY);
                return;
            }

            if (ViewModel?.AchievementRows?.Count > 0)
            {
                ShowDropIndicator(AchievementOrderDataGrid.ActualHeight - 1);
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

            var maxTop = Math.Max(0, AchievementOrderDataGrid.ActualHeight - DropInsertLine.Height);
            var top = Math.Max(0, Math.Min(maxTop, y - (DropInsertLine.Height / 2.0)));
            DropInsertLine.Margin = new Thickness(0, top, 0, 0);
            DropInsertLine.Visibility = Visibility.Visible;
        }

        private void HideDropIndicator()
        {
            DropInsertLine.Visibility = Visibility.Collapsed;
        }

        private void ShowDragCountPopup()
        {
            if (_dragItemCount <= 0)
            {
                return;
            }

            DragCountText.Text = _dragItemCount.ToString();
            DragCountPopup.IsOpen = true;
            UpdateDragCountPopupPosition();
        }

        private void HideDragCountPopup()
        {
            if (DragCountPopup.IsOpen)
            {
                DragCountPopup.IsOpen = false;
            }
        }

        private void UpdateDragCountPopupPosition()
        {
            if (!GetCursorPos(out var cursorPoint))
            {
                return;
            }

            DragCountPopup.HorizontalOffset = cursorPoint.X + 18;
            DragCountPopup.VerticalOffset = cursorPoint.Y + 18;
        }

        private bool IsPointWithinDataGrid(Point point)
        {
            return point.X >= 0 &&
                   point.Y >= 0 &&
                   point.X <= AchievementOrderDataGrid.ActualWidth &&
                   point.Y <= AchievementOrderDataGrid.ActualHeight;
        }

        private static bool IsDragColumnHit(DependencyObject source)
        {
            var cell = VisualTreeHelpers.FindVisualParent<DataGridCell>(source);
            return cell?.Column?.DisplayIndex == 0;
        }

        private void RefreshDataCore(IReadOnlyList<string> selectedApiNames)
        {
            HideDropIndicator();
            HideDragCountPopup();
            _hasDragStartPoint = false;
            _dragAnchorItem = null;
            ViewModel?.ReloadData();
            RestoreSelectionByApiNames(selectedApiNames);
        }

        private List<string> CaptureSelectedApiNames()
        {
            var selectedItems = AchievementOrderDataGrid?.SelectedItems;
            if (selectedItems == null)
            {
                return new List<string>();
            }

            return AchievementOrderHelper.NormalizeApiNames(
                selectedItems
                    .OfType<AchievementDisplayItem>()
                    .Select(item => item.ApiName));
        }

        private void RestoreSelectionByApiNames(IEnumerable<string> apiNames)
        {
            if (AchievementOrderDataGrid == null)
            {
                return;
            }

            var selectedApiNames = new HashSet<string>(
                AchievementOrderHelper.NormalizeApiNames(apiNames),
                StringComparer.OrdinalIgnoreCase);

            AchievementOrderDataGrid.SelectedItems.Clear();
            if (selectedApiNames.Count == 0 || ViewModel?.AchievementRows == null)
            {
                return;
            }

            foreach (var row in ViewModel.AchievementRows)
            {
                var apiName = (row?.ApiName ?? string.Empty).Trim();
                if (!string.IsNullOrWhiteSpace(apiName) && selectedApiNames.Contains(apiName))
                {
                    AchievementOrderDataGrid.SelectedItems.Add(row);
                }
            }
        }

        private void ApplyPendingRefreshIfNeeded()
        {
            if (!_pendingRefreshRequested)
            {
                return;
            }

            _pendingRefreshRequested = false;
            RefreshDataCore(CaptureSelectedApiNames());
        }
    }
}
