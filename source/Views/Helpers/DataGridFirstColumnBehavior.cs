using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace PlayniteAchievements.Views.Helpers
{
    /// <summary>
    /// Marks whichever column is currently leftmost and visible, so a cell template can render a
    /// left-edge affordance only where it belongs.
    ///
    /// Columns here are reorderable and hideable, so "the first column" is not a fixed column: the
    /// name column can be dragged into the middle or hidden outright. Anything that depends on
    /// sitting at the row's left edge - a tree guide, an indent - has to follow the current order
    /// rather than be pinned to one column, or it ends up drawing a left edge in the middle of the
    /// row.
    /// </summary>
    public static class DataGridFirstColumnBehavior
    {
        public static readonly DependencyProperty IsEnabledProperty =
            DependencyProperty.RegisterAttached(
                "IsEnabled",
                typeof(bool),
                typeof(DataGridFirstColumnBehavior),
                new PropertyMetadata(false, OnIsEnabledChanged));

        /// <summary>Read-only from XAML: set on each column by the behavior, bound by cell templates.</summary>
        public static readonly DependencyProperty IsFirstColumnProperty =
            DependencyProperty.RegisterAttached(
                "IsFirstColumn",
                typeof(bool),
                typeof(DataGridFirstColumnBehavior),
                new FrameworkPropertyMetadata(false));

        private static readonly DependencyProperty StateProperty =
            DependencyProperty.RegisterAttached(
                "State",
                typeof(FirstColumnState),
                typeof(DataGridFirstColumnBehavior),
                new PropertyMetadata(null));

        public static bool GetIsEnabled(DependencyObject obj)
        {
            return (bool)obj.GetValue(IsEnabledProperty);
        }

        public static void SetIsEnabled(DependencyObject obj, bool value)
        {
            obj.SetValue(IsEnabledProperty, value);
        }

        public static bool GetIsFirstColumn(DependencyObject obj)
        {
            return obj != null && (bool)obj.GetValue(IsFirstColumnProperty);
        }

        public static void SetIsFirstColumn(DependencyObject obj, bool value)
        {
            obj?.SetValue(IsFirstColumnProperty, value);
        }

        private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (!(d is DataGrid grid))
            {
                return;
            }

            var state = grid.GetValue(StateProperty) as FirstColumnState;
            if (e.NewValue is bool value && value)
            {
                if (state == null)
                {
                    state = new FirstColumnState(grid);
                    grid.SetValue(StateProperty, state);
                }

                state.Attach();
                return;
            }

            state?.Detach();
            grid.SetValue(StateProperty, null);
        }

        private sealed class FirstColumnState
        {
            private static readonly DependencyPropertyDescriptor VisibilityDescriptor =
                DependencyPropertyDescriptor.FromProperty(DataGridColumn.VisibilityProperty, typeof(DataGridColumn));

            private static readonly DependencyPropertyDescriptor DisplayIndexDescriptor =
                DependencyPropertyDescriptor.FromProperty(DataGridColumn.DisplayIndexProperty, typeof(DataGridColumn));

            private readonly DataGrid _grid;
            private readonly List<DataGridColumn> _hookedColumns = new List<DataGridColumn>();
            private readonly EventHandler _columnChangedHandler;
            private bool _isAttached;
            private bool _updateQueued;

            public FirstColumnState(DataGrid grid)
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
                // Re-hook after an unload released the columns; HookColumns unhooks first, so a
                // Loaded without an intervening Unloaded cannot double-subscribe.
                HookColumns();
                QueueUpdate();
            }

            private void OnUnloaded(object sender, RoutedEventArgs e)
            {
                // DependencyPropertyDescriptor.AddValueChanged registers in a process-wide static
                // table that only RemoveValueChanged clears, so a grid left hooked after it leaves
                // the tree roots itself and its whole hosting view for the rest of the session.
                // Detach() alone does not cover this: it runs only when the attached property is
                // switched off, which never happens when a window closes.
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
                _grid.Dispatcher.BeginInvoke(new Action(Update), DispatcherPriority.Loaded);
            }

            private void Update()
            {
                _updateQueued = false;
                if (!_isAttached)
                {
                    return;
                }

                var firstVisibleColumn = _grid.Columns
                    .Where(c => c != null && c.Visibility == Visibility.Visible)
                    .OrderBy(c => c.DisplayIndex)
                    .FirstOrDefault();

                foreach (var column in _grid.Columns)
                {
                    if (column == null)
                    {
                        continue;
                    }

                    var isFirst = ReferenceEquals(column, firstVisibleColumn);
                    if (GetIsFirstColumn(column) != isFirst)
                    {
                        SetIsFirstColumn(column, isFirst);
                    }
                }
            }
        }
    }
}
