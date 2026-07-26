using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

namespace PlayniteAchievements.Views.Helpers
{
    /// <summary>
    /// Collapses the right resize gripper of the last visible column so there is no orphan
    /// gripper at the grid's trailing edge. Each header keeps its own left and right grippers
    /// (revealed on header hover via the gripper template), which mirrors WPF's built-in
    /// left-gripper handling: a gripper is shown only when it sits on a boundary between two
    /// columns. The first column's left gripper is collapsed by WPF for the same reason.
    /// </summary>
    public static class DataGridColumnGripperBehavior
    {
        public static readonly DependencyProperty IsEnabledProperty =
            DependencyProperty.RegisterAttached(
                "IsEnabled",
                typeof(bool),
                typeof(DataGridColumnGripperBehavior),
                new PropertyMetadata(false, OnIsEnabledChanged));

        private static readonly DependencyProperty StateProperty =
            DependencyProperty.RegisterAttached(
                "State",
                typeof(GripperState),
                typeof(DataGridColumnGripperBehavior),
                new PropertyMetadata(null));

        public static bool GetIsEnabled(DependencyObject obj)
        {
            return (bool)obj.GetValue(IsEnabledProperty);
        }

        public static void SetIsEnabled(DependencyObject obj, bool value)
        {
            obj.SetValue(IsEnabledProperty, value);
        }

        private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (!(d is DataGrid grid))
            {
                return;
            }

            var state = grid.GetValue(StateProperty) as GripperState;
            if (e.NewValue is bool value && value)
            {
                if (state == null)
                {
                    state = new GripperState(grid);
                    grid.SetValue(StateProperty, state);
                }

                state.Attach();
                return;
            }

            state?.Detach();
            grid.SetValue(StateProperty, null);
        }

        private sealed class GripperState
        {
            private const int MaxRealizationAttempts = 8;

            private static readonly DependencyPropertyDescriptor VisibilityDescriptor =
                DependencyPropertyDescriptor.FromProperty(DataGridColumn.VisibilityProperty, typeof(DataGridColumn));

            private static readonly DependencyPropertyDescriptor DisplayIndexDescriptor =
                DependencyPropertyDescriptor.FromProperty(DataGridColumn.DisplayIndexProperty, typeof(DataGridColumn));

            private readonly DataGrid _grid;
            private readonly List<DataGridColumn> _hookedColumns = new List<DataGridColumn>();
            private readonly EventHandler _columnChangedHandler;
            private bool _isAttached;
            private bool _updateQueued;
            private int _attempts;

            public GripperState(DataGrid grid)
            {
                _grid = grid;
                _columnChangedHandler = (_, __) => QueueUpdate();
            }

            public void Attach()
            {
                if (_isAttached)
                {
                    return;
                }

                _isAttached = true;
                _grid.Loaded += OnLoaded;
                _grid.ColumnReordered += OnColumnReordered;
                if (_grid.Columns is INotifyCollectionChanged columns)
                {
                    columns.CollectionChanged += OnColumnsChanged;
                }

                HookColumns();
                QueueUpdate();
            }

            public void Detach()
            {
                if (!_isAttached)
                {
                    return;
                }

                _isAttached = false;
                _grid.Loaded -= OnLoaded;
                _grid.ColumnReordered -= OnColumnReordered;
                if (_grid.Columns is INotifyCollectionChanged columns)
                {
                    columns.CollectionChanged -= OnColumnsChanged;
                }

                UnhookColumns();
            }

            private void OnLoaded(object sender, RoutedEventArgs e)
            {
                QueueUpdate();
            }

            private void OnColumnReordered(object sender, DataGridColumnEventArgs e)
            {
                QueueUpdate();
            }

            private void OnColumnsChanged(object sender, NotifyCollectionChangedEventArgs e)
            {
                HookColumns();
                QueueUpdate();
            }

            private void HookColumns()
            {
                UnhookColumns();
                foreach (var column in _grid.Columns)
                {
                    if (column == null)
                    {
                        continue;
                    }

                    VisibilityDescriptor?.AddValueChanged(column, _columnChangedHandler);
                    DisplayIndexDescriptor?.AddValueChanged(column, _columnChangedHandler);
                    _hookedColumns.Add(column);
                }
            }

            private void UnhookColumns()
            {
                foreach (var column in _hookedColumns)
                {
                    VisibilityDescriptor?.RemoveValueChanged(column, _columnChangedHandler);
                    DisplayIndexDescriptor?.RemoveValueChanged(column, _columnChangedHandler);
                }

                _hookedColumns.Clear();
            }

            private void QueueUpdate()
            {
                if (_updateQueued || !_isAttached)
                {
                    return;
                }

                _updateQueued = true;
                _attempts = 0;
                _grid.Dispatcher.BeginInvoke(new Action(Update), DispatcherPriority.Loaded);
            }

            private void Update()
            {
                _updateQueued = false;
                if (!_isAttached)
                {
                    return;
                }

                var lastVisibleColumn = _grid.Columns
                    .Where(c => c != null && c.Visibility == Visibility.Visible)
                    .OrderBy(c => c.DisplayIndex)
                    .LastOrDefault();

                var collapsedLastGripper = false;
                foreach (var header in VisualTreeHelpers.FindVisualChildren<DataGridColumnHeader>(_grid))
                {
                    if (header.Column == null)
                    {
                        continue;
                    }

                    var rightGripper = VisualTreeHelpers.FindVisualChildren<Thumb>(header)
                        .FirstOrDefault(t => t.Name == "PART_RightHeaderGripper");
                    if (rightGripper == null)
                    {
                        continue;
                    }

                    var isLastVisible = ReferenceEquals(header.Column, lastVisibleColumn);
                    rightGripper.Visibility = isLastVisible ? Visibility.Collapsed : Visibility.Visible;
                    collapsedLastGripper |= isLastVisible;
                }

                // Grippers may not be realized on the first pass; retry until the trailing one is collapsed.
                if (!collapsedLastGripper && _attempts < MaxRealizationAttempts)
                {
                    _attempts++;
                    _updateQueued = true;
                    _grid.Dispatcher.BeginInvoke(new Action(Update), DispatcherPriority.Background);
                }
            }
        }
    }
}
