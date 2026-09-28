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
    /// Collapses the resize grippers that sit on a boundary nothing may drag: the right gripper
    /// of the last visible column (no orphan gripper at the grid's trailing edge) and both
    /// grippers on either side of a locked column's edges. Each header keeps its own left and
    /// right grippers (revealed on header hover via the gripper template), which mirrors WPF's
    /// built-in left-gripper handling: a gripper is shown only when it sits on a boundary between
    /// two columns. The first column's left gripper is collapsed by WPF for the same reason.
    /// </summary>
    /// <remarks>
    /// Locking a column sets CanUserResize false, so WPF itself hides that column's right gripper
    /// and the next header's left gripper. The other edge is ours: the locked column's left
    /// gripper and the previous header's right gripper. The pass writes every gripper it can see,
    /// so it stays authoritative after WPF's own local writes on template apply and CanUserResize
    /// changes.
    /// </remarks>
    public static class DataGridColumnGripperBehavior
    {
        public static readonly DependencyProperty IsEnabledProperty =
            DependencyProperty.RegisterAttached(
                "IsEnabled",
                typeof(bool),
                typeof(DataGridColumnGripperBehavior),
                new PropertyMetadata(false, OnIsEnabledChanged));

        /// <summary>
        /// Set on a <see cref="DataGridColumn"/> by <see cref="DataGridColumnLayoutService"/> when
        /// the user locks its width. Read here to hide the grippers on both of its edges.
        /// </summary>
        public static readonly DependencyProperty IsLockedProperty =
            DependencyProperty.RegisterAttached(
                "IsLocked",
                typeof(bool),
                typeof(DataGridColumnGripperBehavior),
                new PropertyMetadata(false));

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

        public static bool GetIsLocked(DependencyObject obj)
        {
            return obj != null && (bool)obj.GetValue(IsLockedProperty);
        }

        public static void SetIsLocked(DependencyObject obj, bool value)
        {
            obj?.SetValue(IsLockedProperty, value);
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

            private static readonly DependencyPropertyDescriptor CanUserResizeDescriptor =
                DependencyPropertyDescriptor.FromProperty(DataGridColumn.CanUserResizeProperty, typeof(DataGridColumn));

            private static readonly DependencyPropertyDescriptor IsLockedDescriptor =
                DependencyPropertyDescriptor.FromProperty(IsLockedProperty, typeof(DataGridColumn));

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
                _grid.Unloaded += OnUnloaded;
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
                _grid.Unloaded -= OnUnloaded;
                _grid.ColumnReordered -= OnColumnReordered;
                if (_grid.Columns is INotifyCollectionChanged columns)
                {
                    columns.CollectionChanged -= OnColumnsChanged;
                }

                UnhookColumns();
            }

            private void OnLoaded(object sender, RoutedEventArgs e)
            {
                // Re-hook: the columns are released on unload (see OnUnloaded), and a grid can
                // be unloaded and reloaded any number of times - tab switches, re-parenting.
                // HookColumns unhooks first, so a Loaded without an intervening Unloaded cannot
                // double-subscribe.
                HookColumns();
                QueueUpdate();
            }

            private void OnUnloaded(object sender, RoutedEventArgs e)
            {
                // DependencyPropertyDescriptor.AddValueChanged registers the handler in a
                // process-wide static table that is only ever cleared by RemoveValueChanged.
                // A grid left hooked after it leaves the tree therefore roots itself through
                // that table - and with it its DataContext and the whole hosting view - for
                // the rest of the session. Detach() alone is not enough: it only runs when the
                // attached property is switched off, which never happens when a window closes.
                UnhookColumns();
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
                    CanUserResizeDescriptor?.AddValueChanged(column, _columnChangedHandler);
                    IsLockedDescriptor?.AddValueChanged(column, _columnChangedHandler);
                    _hookedColumns.Add(column);
                }
            }

            private void UnhookColumns()
            {
                foreach (var column in _hookedColumns)
                {
                    VisibilityDescriptor?.RemoveValueChanged(column, _columnChangedHandler);
                    DisplayIndexDescriptor?.RemoveValueChanged(column, _columnChangedHandler);
                    CanUserResizeDescriptor?.RemoveValueChanged(column, _columnChangedHandler);
                    IsLockedDescriptor?.RemoveValueChanged(column, _columnChangedHandler);
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

                var visibleColumns = _grid.Columns
                    .Where(c => c != null && c.Visibility == Visibility.Visible)
                    .OrderBy(c => c.DisplayIndex)
                    .ToList();
                var lastVisibleColumn = visibleColumns.LastOrDefault();

                var collapsedLastGripper = false;
                foreach (var header in VisualTreeHelpers.FindVisualChildren<DataGridColumnHeader>(_grid))
                {
                    if (header.Column == null)
                    {
                        continue;
                    }

                    var thumbs = VisualTreeHelpers.FindVisualChildren<Thumb>(header).ToList();
                    var rightGripper = thumbs.FirstOrDefault(t => t.Name == "PART_RightHeaderGripper");
                    if (rightGripper == null)
                    {
                        continue;
                    }

                    var index = visibleColumns.IndexOf(header.Column);
                    var isLocked = GetIsLocked(header.Column);
                    var previousLocked = index > 0 && GetIsLocked(visibleColumns[index - 1]);
                    var nextLocked = index >= 0 && index < visibleColumns.Count - 1 && GetIsLocked(visibleColumns[index + 1]);

                    var isLastVisible = ReferenceEquals(header.Column, lastVisibleColumn);
                    rightGripper.Visibility = isLastVisible || isLocked || nextLocked
                        ? Visibility.Collapsed
                        : Visibility.Visible;
                    collapsedLastGripper |= isLastVisible;

                    // The left gripper is only forced closed; open boundaries stay under WPF's own
                    // rule (collapsed for the first column and after a non-resizable neighbour).
                    if (isLocked || previousLocked)
                    {
                        var leftGripper = thumbs.FirstOrDefault(t => t.Name == "PART_LeftHeaderGripper");
                        if (leftGripper != null)
                        {
                            leftGripper.Visibility = Visibility.Collapsed;
                        }
                    }
                    else if (index > 0 && visibleColumns[index - 1].CanUserResize)
                    {
                        var leftGripper = thumbs.FirstOrDefault(t => t.Name == "PART_LeftHeaderGripper");
                        if (leftGripper != null && leftGripper.Visibility != Visibility.Visible)
                        {
                            leftGripper.Visibility = Visibility.Visible;
                        }
                    }
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
