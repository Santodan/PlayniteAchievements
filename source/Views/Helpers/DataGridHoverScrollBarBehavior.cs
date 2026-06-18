using System;
using System.Collections.Generic;
using System.ComponentModel;
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

        private static readonly DependencyPropertyDescriptor GridMouseOverDescriptor =
            DependencyPropertyDescriptor.FromProperty(UIElement.IsMouseOverProperty, typeof(DataGrid));

        private static readonly DependencyPropertyDescriptor GridKeyboardFocusWithinDescriptor =
            DependencyPropertyDescriptor.FromProperty(UIElement.IsKeyboardFocusWithinProperty, typeof(DataGrid));

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
            private static readonly DependencyPropertyDescriptor ScrollBarIsEnabledDescriptor =
                DependencyPropertyDescriptor.FromProperty(UIElement.IsEnabledProperty, typeof(ScrollBar));

            private readonly DataGrid _grid;
            private readonly List<TrackedScrollBar> _scrollBars = new List<TrackedScrollBar>();
            private bool _isAttached;
            private bool _isMouseWithin;
            private bool _refreshQueued;

            public HoverScrollBarState(DataGrid grid)
            {
                _grid = grid;
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
                _grid.MouseLeave += OnMouseLeave;
                _grid.GotKeyboardFocus += OnKeyboardFocusChanged;
                _grid.LostKeyboardFocus += OnKeyboardFocusChanged;
                GridMouseOverDescriptor?.AddValueChanged(_grid, OnInteractionStateChanged);
                GridKeyboardFocusWithinDescriptor?.AddValueChanged(_grid, OnInteractionStateChanged);

                _isAttached = true;
                _isMouseWithin = _grid.IsMouseOver;

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
                _grid.MouseLeave -= OnMouseLeave;
                _grid.GotKeyboardFocus -= OnKeyboardFocusChanged;
                _grid.LostKeyboardFocus -= OnKeyboardFocusChanged;
                GridMouseOverDescriptor?.RemoveValueChanged(_grid, OnInteractionStateChanged);
                GridKeyboardFocusWithinDescriptor?.RemoveValueChanged(_grid, OnInteractionStateChanged);

                ClearTrackedScrollBars();
                _isMouseWithin = false;
                _refreshQueued = false;
                _isAttached = false;
            }

            private void OnLoaded(object sender, RoutedEventArgs e)
            {
                _isMouseWithin = _grid.IsMouseOver;
                QueueRefreshScrollBars();
            }

            private void OnUnloaded(object sender, RoutedEventArgs e)
            {
                ClearTrackedScrollBars();
                _isMouseWithin = false;
                _refreshQueued = false;
            }

            private void OnMouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
            {
                _isMouseWithin = true;
                ApplyScrollBarState();
            }

            private void OnMouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
            {
                _isMouseWithin = false;
                ApplyScrollBarState();
            }

            private void OnKeyboardFocusChanged(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
            {
                QueueApplyScrollBarState();
            }

            private void OnInteractionStateChanged(object sender, EventArgs e)
            {
                ApplyScrollBarState();
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

            private void QueueApplyScrollBarState()
            {
                _grid.Dispatcher.BeginInvoke(
                    new Action(() =>
                    {
                        if (_isAttached)
                        {
                            ApplyScrollBarState();
                        }
                    }),
                    DispatcherPriority.Input);
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
                                 (_isMouseWithin || _grid.IsMouseOver || _grid.IsKeyboardFocusWithin);

                foreach (var scrollBar in _scrollBars)
                {
                    scrollBar.Apply(shouldShow);
                }
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
                private readonly object _originalOpacity;
                private readonly object _originalHitTestVisible;
                private bool _isDetached;

                public TrackedScrollBar(ScrollBar scrollBar, Action applyState)
                {
                    _scrollBar = scrollBar;
                    _originalOpacity = scrollBar.ReadLocalValue(UIElement.OpacityProperty);
                    _originalHitTestVisible = scrollBar.ReadLocalValue(UIElement.IsHitTestVisibleProperty);
                    _enabledChangedHandler = (_, __) => applyState?.Invoke();
                    ScrollBarIsEnabledDescriptor?.AddValueChanged(_scrollBar, _enabledChangedHandler);
                }

                public void Apply(bool shouldShow)
                {
                    if (_isDetached)
                    {
                        return;
                    }

                    if (shouldShow && _scrollBar.IsEnabled)
                    {
                        RestoreOriginalValue(_scrollBar, UIElement.OpacityProperty, _originalOpacity);
                        RestoreOriginalValue(_scrollBar, UIElement.IsHitTestVisibleProperty, _originalHitTestVisible);
                        return;
                    }

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
                    RestoreOriginalValue(_scrollBar, UIElement.OpacityProperty, _originalOpacity);
                    RestoreOriginalValue(_scrollBar, UIElement.IsHitTestVisibleProperty, _originalHitTestVisible);
                    _isDetached = true;
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
