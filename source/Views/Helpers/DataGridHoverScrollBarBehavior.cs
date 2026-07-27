using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace PlayniteAchievements.Views.Helpers
{
    public static class DataGridHoverScrollBarBehavior
    {
        public static readonly DependencyProperty IsEnabledProperty =
            DependencyProperty.RegisterAttached(
                "IsEnabled",
                typeof(bool),
                typeof(DataGridHoverScrollBarBehavior),
                new PropertyMetadata(false, OnIsEnabledChanged));

        private static readonly DependencyProperty StateProperty =
            DependencyProperty.RegisterAttached(
                "State",
                typeof(HoverScrollBarState),
                typeof(DataGridHoverScrollBarBehavior),
                new PropertyMetadata(null));

        public static bool GetIsEnabled(DependencyObject obj)
        {
            return (bool)obj.GetValue(IsEnabledProperty);
        }

        public static void SetIsEnabled(DependencyObject obj, bool value)
        {
            obj.SetValue(IsEnabledProperty, value);
        }

        private static HoverScrollBarState GetState(DependencyObject obj)
        {
            return (HoverScrollBarState)obj.GetValue(StateProperty);
        }

        private static void SetState(DependencyObject obj, HoverScrollBarState value)
        {
            obj.SetValue(StateProperty, value);
        }

        private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (!(d is DataGrid grid))
            {
                return;
            }

            var isEnabled = e.NewValue is bool value && value;
            var state = GetState(grid);

            if (isEnabled)
            {
                if (state == null)
                {
                    state = new HoverScrollBarState(grid);
                    SetState(grid, state);
                }

                state.Attach();
                return;
            }

            state?.Detach();
            SetState(grid, null);
        }

        private sealed class HoverScrollBarState
        {
            private const double IdleScrollBarOpacity = 0.42d;
            private static readonly TimeSpan ScrollRevealDuration = TimeSpan.FromMilliseconds(900);

            private static readonly DependencyPropertyDescriptor ScrollBarIsEnabledDescriptor =
                DependencyPropertyDescriptor.FromProperty(UIElement.IsEnabledProperty, typeof(ScrollBar));

            private readonly DataGrid _grid;
            private readonly DispatcherTimer _scrollRevealTimer;
            private readonly List<TrackedScrollBar> _scrollBars = new List<TrackedScrollBar>();
            private ScrollViewer _scrollViewer;
            private bool _isAttached;
            private bool _isMouseOverRightmostColumn;
            private bool _isScrollRevealActive;
            private bool _refreshQueued;

            public HoverScrollBarState(DataGrid grid)
            {
                _grid = grid;
                _scrollRevealTimer = new DispatcherTimer(DispatcherPriority.Background, grid.Dispatcher)
                {
                    Interval = ScrollRevealDuration
                };
                _scrollRevealTimer.Tick += OnScrollRevealTimerTick;
            }

            public void Attach()
            {
                if (_isAttached)
                {
                    return;
                }

                _grid.Loaded += OnLoaded;
                _grid.Unloaded += OnUnloaded;
                _grid.MouseEnter += OnMouseEnter;
                // Use the tunneling PreviewMouseMove so the grid always observes the cursor
                // first, before any descendant can mark the event handled. The column headers'
                // resize/reorder logic handles the bubbling MouseMove, which would otherwise
                // freeze the hover state the moment the cursor leaves the body and prevent it
                // from updating back to "not over a body cell".
                _grid.PreviewMouseMove += OnMouseMove;
                _grid.MouseLeave += OnMouseLeave;
                _grid.PreviewMouseWheel += OnPreviewMouseWheel;

                _isAttached = true;
                _isMouseOverRightmostColumn = false;

                if (_grid.IsLoaded)
                {
                    QueueRefreshScrollBars();
                }
            }

            public void Detach()
            {
                if (!_isAttached)
                {
                    return;
                }

                _grid.Loaded -= OnLoaded;
                _grid.Unloaded -= OnUnloaded;
                _grid.MouseEnter -= OnMouseEnter;
                _grid.PreviewMouseMove -= OnMouseMove;
                _grid.MouseLeave -= OnMouseLeave;
                _grid.PreviewMouseWheel -= OnPreviewMouseWheel;

                ClearTrackedScrollBars();
                DetachScrollViewer();
                StopScrollReveal();
                _isMouseOverRightmostColumn = false;
                _refreshQueued = false;
                _isAttached = false;
            }

            private void OnLoaded(object sender, RoutedEventArgs e)
            {
                _isMouseOverRightmostColumn = false;
                QueueRefreshScrollBars();
            }

            private void OnUnloaded(object sender, RoutedEventArgs e)
            {
                ClearTrackedScrollBars();
                DetachScrollViewer();
                StopScrollReveal();
                _isMouseOverRightmostColumn = false;
                _refreshQueued = false;
            }

            private void OnMouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
            {
                UpdateRightmostColumnHover(e);
            }

            private void OnMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
            {
                UpdateRightmostColumnHover(e);
            }

            private void OnMouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
            {
                _isMouseOverRightmostColumn = false;
                ApplyScrollBarState();
            }

            private void OnPreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
            {
                RevealForScrolling();
            }

            private void OnScrollViewerScrollChanged(object sender, ScrollChangedEventArgs e)
            {
                if (Math.Abs(e.VerticalChange) > 0.01 || Math.Abs(e.HorizontalChange) > 0.01)
                {
                    RevealForScrolling();
                }
            }

            private void QueueRefreshScrollBars()
            {
                if (_refreshQueued)
                {
                    return;
                }

                _refreshQueued = true;
                _grid.Dispatcher.BeginInvoke(
                    new Action(() =>
                    {
                        _refreshQueued = false;
                        if (!_isAttached || !_grid.IsLoaded)
                        {
                            return;
                        }

                        RefreshScrollBars();
                    }),
                    DispatcherPriority.Loaded);
            }

            private void RevealForScrolling()
            {
                _isScrollRevealActive = true;
                _scrollRevealTimer.Stop();
                _scrollRevealTimer.Start();
                ApplyScrollBarState();
            }

            private void OnScrollRevealTimerTick(object sender, EventArgs e)
            {
                StopScrollReveal();
                ApplyScrollBarState();
            }

            private void StopScrollReveal()
            {
                _scrollRevealTimer.Stop();
                _isScrollRevealActive = false;
            }

            private void RefreshScrollBars()
            {
                var scrollViewer = VisualTreeHelpers.FindVisualChild<ScrollViewer>(_grid);
                if (scrollViewer == null)
                {
                    return;
                }

                scrollViewer.ApplyTemplate();
                ClearTrackedScrollBars();
                AttachScrollViewer(scrollViewer);

                var seen = new HashSet<ScrollBar>();
                foreach (var scrollBar in FindDataGridScrollBars(scrollViewer))
                {
                    if (scrollBar == null || !seen.Add(scrollBar))
                    {
                        continue;
                    }

                    _scrollBars.Add(new TrackedScrollBar(scrollBar, ApplyScrollBarState));
                }

                ApplyScrollBarState();
            }

            private void AttachScrollViewer(ScrollViewer scrollViewer)
            {
                if (ReferenceEquals(_scrollViewer, scrollViewer))
                {
                    return;
                }

                DetachScrollViewer();
                _scrollViewer = scrollViewer;
                _scrollViewer.ScrollChanged += OnScrollViewerScrollChanged;
            }

            private void DetachScrollViewer()
            {
                if (_scrollViewer == null)
                {
                    return;
                }

                _scrollViewer.ScrollChanged -= OnScrollViewerScrollChanged;
                _scrollViewer = null;
            }

            private void ClearTrackedScrollBars()
            {
                foreach (var scrollBar in _scrollBars)
                {
                    scrollBar.Detach();
                }

                _scrollBars.Clear();
            }

            private void ApplyScrollBarState()
            {
                var shouldShow = _isAttached &&
                                  (_isMouseOverRightmostColumn ||
                                   _isScrollRevealActive ||
                                   _scrollBars.Any(scrollBar => scrollBar.IsInteractionActive));

                foreach (var scrollBar in _scrollBars)
                {
                    scrollBar.Apply(shouldShow, _isScrollRevealActive);
                }
            }

            private void UpdateRightmostColumnHover(System.Windows.Input.MouseEventArgs e)
            {
                var isOverRightmostColumn = IsMouseOverRightmostVisibleColumn(e);
                if (_isMouseOverRightmostColumn == isOverRightmostColumn)
                {
                    return;
                }

                _isMouseOverRightmostColumn = isOverRightmostColumn;
                ApplyScrollBarState();
            }

            private bool IsMouseOverRightmostVisibleColumn(System.Windows.Input.MouseEventArgs e)
            {
                var rightmostColumn = GetRightmostVisibleColumn();
                if (rightmostColumn == null || e == null)
                {
                    return false;
                }

                if (IsSourceWithinColumn(e.OriginalSource as DependencyObject, rightmostColumn))
                {
                    return true;
                }

                return IsPointWithinColumnBounds(rightmostColumn, e.GetPosition(_grid));
            }

            private DataGridColumn GetRightmostVisibleColumn()
            {
                DataGridColumn rightmostColumn = null;
                var rightmostDisplayIndex = int.MinValue;

                foreach (var column in _grid.Columns)
                {
                    if (column == null ||
                        column.Visibility != Visibility.Visible ||
                        column.DisplayIndex < rightmostDisplayIndex)
                    {
                        continue;
                    }

                    rightmostColumn = column;
                    rightmostDisplayIndex = column.DisplayIndex;
                }

                return rightmostColumn;
            }

            private static bool IsSourceWithinColumn(DependencyObject source, DataGridColumn column)
            {
                if (source == null || column == null)
                {
                    return false;
                }

                // Only body cells count; a header hover (including its resize grippers)
                // must not reveal the scrollbar.
                var cell = VisualTreeHelpers.FindVisualParent<DataGridCell>(source);
                return ReferenceEquals(cell?.Column, column);
            }

            private bool IsPointWithinColumnBounds(DataGridColumn column, Point point)
            {
                if (column == null ||
                    !IsFinite(point.X) ||
                    !IsFinite(point.Y) ||
                    point.X < 0 ||
                    point.Y < 0 ||
                    point.Y > _grid.ActualHeight)
                {
                    return false;
                }

                // Points within the header band are not part of the column body.
                if (point.Y < GetColumnBodyTop())
                {
                    return false;
                }

                foreach (var cell in EnumerateVisualDescendants<DataGridCell>(_grid))
                {
                    if (!ReferenceEquals(cell.Column, column) ||
                        cell.ActualWidth <= 0)
                    {
                        continue;
                    }

                    var cellOrigin = cell.TranslatePoint(new Point(0, 0), _grid);
                    if (point.X >= cellOrigin.X &&
                        point.X <= cellOrigin.X + cell.ActualWidth)
                    {
                        return true;
                    }
                }

                return false;
            }

            /// <summary>
            /// Returns the Y coordinate (in grid space) of the bottom of the column headers
            /// region, i.e. the top of the column body. Falls back to 0 when the headers
            /// presenter is not yet realized.
            /// </summary>
            private double GetColumnBodyTop()
            {
                var presenter = VisualTreeHelpers.FindVisualChild<DataGridColumnHeadersPresenter>(_grid);
                if (presenter == null || presenter.ActualHeight <= 0)
                {
                    return 0d;
                }

                var origin = presenter.TranslatePoint(new Point(0, 0), _grid);
                return origin.Y + presenter.ActualHeight;
            }

            private static bool IsFinite(double value)
            {
                return !double.IsNaN(value) && !double.IsInfinity(value);
            }

            private static IEnumerable<ScrollBar> FindDataGridScrollBars(ScrollViewer scrollViewer)
            {
                var vertical = scrollViewer.Template?.FindName("PART_VerticalScrollBar", scrollViewer) as ScrollBar;
                if (vertical != null)
                {
                    yield return vertical;
                }

                var horizontal = scrollViewer.Template?.FindName("PART_HorizontalScrollBar", scrollViewer) as ScrollBar;
                if (horizontal != null)
                {
                    yield return horizontal;
                }

                foreach (var scrollBar in EnumerateVisualDescendants<ScrollBar>(scrollViewer))
                {
                    if (IsScrollViewerTemplatePart(scrollViewer, scrollBar))
                    {
                        yield return scrollBar;
                    }
                }
            }

            private static bool IsScrollViewerTemplatePart(ScrollViewer scrollViewer, ScrollBar scrollBar)
            {
                if (scrollViewer == null || scrollBar == null)
                {
                    return false;
                }

                if (ReferenceEquals(scrollBar.TemplatedParent, scrollViewer))
                {
                    return true;
                }

                return string.Equals(scrollBar.Name, "PART_VerticalScrollBar", StringComparison.Ordinal) ||
                       string.Equals(scrollBar.Name, "PART_HorizontalScrollBar", StringComparison.Ordinal);
            }

            private static IEnumerable<T> EnumerateVisualDescendants<T>(DependencyObject parent)
                where T : DependencyObject
            {
                if (parent == null)
                {
                    yield break;
                }

                var childCount = 0;
                try
                {
                    childCount = VisualTreeHelper.GetChildrenCount(parent);
                }
                catch (ArgumentException)
                {
                    yield break;
                }
                catch (InvalidOperationException)
                {
                    yield break;
                }

                for (var i = 0; i < childCount; i++)
                {
                    var child = VisualTreeHelper.GetChild(parent, i);
                    if (child is T typed)
                    {
                        yield return typed;
                    }

                    foreach (var nested in EnumerateVisualDescendants<T>(child))
                    {
                        yield return nested;
                    }
                }
            }

            private sealed class TrackedScrollBar
            {
                private readonly ScrollBar _scrollBar;
                private readonly EventHandler _enabledChangedHandler;
                private readonly System.Windows.Input.MouseEventHandler _mouseEnterHandler;
                private readonly System.Windows.Input.MouseEventHandler _mouseLeaveHandler;
                private readonly DragStartedEventHandler _thumbDragStartedHandler;
                private readonly DragCompletedEventHandler _thumbDragCompletedHandler;
                private readonly object _originalOpacity;
                private readonly object _originalHitTestVisible;
                private readonly Thumb _thumb;
                private readonly Action _applyState;
                private bool _isDetached;
                private bool _isMouseWithin;
                private bool _isDragging;

                public bool IsInteractionActive => _isMouseWithin || _isDragging;

                public TrackedScrollBar(ScrollBar scrollBar, Action applyState)
                {
                    _scrollBar = scrollBar;
                    _applyState = applyState;
                    _originalOpacity = scrollBar.ReadLocalValue(UIElement.OpacityProperty);
                    _originalHitTestVisible = scrollBar.ReadLocalValue(UIElement.IsHitTestVisibleProperty);
                    _enabledChangedHandler = (_, __) => applyState?.Invoke();
                    _mouseEnterHandler = (_, __) =>
                    {
                        _isMouseWithin = true;
                        _applyState?.Invoke();
                    };
                    _mouseLeaveHandler = (_, __) =>
                    {
                        _isMouseWithin = false;
                        _applyState?.Invoke();
                    };
                    _thumbDragStartedHandler = (_, __) =>
                    {
                        _isDragging = true;
                        _applyState?.Invoke();
                    };
                    _thumbDragCompletedHandler = (_, __) =>
                    {
                        _isDragging = false;
                        _applyState?.Invoke();
                    };

                    ScrollBarIsEnabledDescriptor?.AddValueChanged(_scrollBar, _enabledChangedHandler);
                    _scrollBar.MouseEnter += _mouseEnterHandler;
                    _scrollBar.MouseLeave += _mouseLeaveHandler;

                    _scrollBar.ApplyTemplate();
                    _thumb = FindThumb(_scrollBar);
                    if (_thumb != null)
                    {
                        _thumb.DragStarted += _thumbDragStartedHandler;
                        _thumb.DragCompleted += _thumbDragCompletedHandler;
                    }
                }

                public void Apply(bool shouldShow, bool isScrollRevealActive)
                {
                    if (_isDetached)
                    {
                        return;
                    }

                    if (shouldShow && _scrollBar.IsEnabled)
                    {
                        RestoreOriginalValue(_scrollBar, UIElement.IsHitTestVisibleProperty, _originalHitTestVisible);

                        if (isScrollRevealActive || _isMouseWithin || _isDragging)
                        {
                            RestoreOriginalValue(_scrollBar, UIElement.OpacityProperty, _originalOpacity);
                        }
                        else
                        {
                            _scrollBar.SetValue(UIElement.OpacityProperty, ResolveIdleOpacity());
                        }

                        return;
                    }

                    _isMouseWithin = false;
                    _isDragging = false;
                    _scrollBar.SetValue(UIElement.OpacityProperty, 0d);
                    _scrollBar.SetValue(UIElement.IsHitTestVisibleProperty, false);
                }

                public void Detach()
                {
                    if (_isDetached)
                    {
                        return;
                    }

                    ScrollBarIsEnabledDescriptor?.RemoveValueChanged(_scrollBar, _enabledChangedHandler);
                    _scrollBar.MouseEnter -= _mouseEnterHandler;
                    _scrollBar.MouseLeave -= _mouseLeaveHandler;
                    if (_thumb != null)
                    {
                        _thumb.DragStarted -= _thumbDragStartedHandler;
                        _thumb.DragCompleted -= _thumbDragCompletedHandler;
                    }

                    RestoreOriginalValue(_scrollBar, UIElement.OpacityProperty, _originalOpacity);
                    RestoreOriginalValue(_scrollBar, UIElement.IsHitTestVisibleProperty, _originalHitTestVisible);
                    _isDetached = true;
                }

                private double ResolveIdleOpacity()
                {
                    var originalOpacity = ResolveOriginalOpacity();
                    return Math.Min(originalOpacity, IdleScrollBarOpacity);
                }

                private double ResolveOriginalOpacity()
                {
                    return _originalOpacity is double opacity &&
                           !double.IsNaN(opacity) &&
                           !double.IsInfinity(opacity)
                        ? opacity
                        : 1d;
                }

                private static Thumb FindThumb(DependencyObject parent)
                {
                    return VisualTreeHelpers.FindVisualChild<Thumb>(parent);
                }

                private static void RestoreOriginalValue(DependencyObject target, DependencyProperty property, object value)
                {
                    if (value == DependencyProperty.UnsetValue)
                    {
                        target.ClearValue(property);
                        return;
                    }

                    target.SetValue(property, value);
                }
            }
        }
    }
}
