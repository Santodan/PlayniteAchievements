using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Playnite.SDK;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.Views.Helpers
{
    /// <summary>
    /// Links a grid control's data grid to the Sort By item of its control bar. The item lists
    /// every column with a sort member, hidden ones included, under the names the column menu
    /// uses. Picking one sorts through the grid's own Sorting path with a
    /// <see cref="GridSortRequestEventArgs"/>, exactly where a header click goes, and every
    /// column's sort arrow is mirrored back, so a header click shows in the item too. A sort that
    /// ends unsorted (a header's third click, or unsorting from the item) clears the arrows, even
    /// where the grid then falls back to its default order, and the item reads "Sort By" until
    /// the next sort.
    /// </summary>
    /// <remarks>
    /// A bar without the item gets one when the host provides it. A showcase widget's bar does not:
    /// its item belongs to the widget's adapter, which offers it only when the widget sorts, and
    /// the widget's settings may hide it.
    /// The column arrows are watched through a <see cref="DependencyPropertyDescriptor"/>, whose
    /// static registry would root the grid, so the watch follows the grid's Loaded and Unloaded.
    /// </remarks>
    internal sealed class GridSortSelectorBinder
    {
        private static readonly DependencyPropertyDescriptor SortDirectionDescriptor =
            DependencyPropertyDescriptor.FromProperty(DataGridColumn.SortDirectionProperty, typeof(DataGridColumn));

        private readonly DataGrid _grid;
        private readonly Action<DataGridSortingEventArgs> _sort;
        private readonly Func<bool> _providesSelector;
        private readonly Func<DataGridColumn, bool> _isOffered;
        private readonly IReadOnlyList<GridSortColumn> _catalog;
        private readonly Action<string, ListSortDirection?> _apply;
        private readonly List<DataGridColumn> _watchedColumns = new List<DataGridColumn>();
        private GridControlBarViewModel _bar;
        private GridSortSelector _selector;

        // The item this binder added, which it takes back out when the host stops providing one.
        private GridSortSelector _created;
        private bool _syncPending;
        private bool _placePending;

        // True after a sort that ended unsorted, until the next sort.
        private bool _unsorted;

        /// <param name="grid">The data grid whose columns the item lists and whose arrows it mirrors.</param>
        /// <param name="sort">The grid control's Sorting handler, which a request runs through.</param>
        /// <param name="providesSelector">True when a bar without the item should get one.</param>
        /// <param name="isOffered">
        /// Whether the item offers a column; the grid's column menu decides, so a column the
        /// surface keeps out of that menu is not offered here either.
        /// </param>
        public GridSortSelectorBinder(
            DataGrid grid,
            Action<DataGridSortingEventArgs> sort,
            Func<bool> providesSelector,
            Func<DataGridColumn, bool> isOffered,
            IReadOnlyList<GridSortColumn> catalog = null)
        {
            _grid = grid ?? throw new ArgumentNullException(nameof(grid));
            _sort = sort;
            _providesSelector = providesSelector ?? (() => true);
            _isOffered = isOffered ?? (_ => true);
            _catalog = catalog;
            _apply = Apply;

            // Subscribed after the grid control's own XAML handler, so it reads the outcome.
            _grid.Sorting += OnGridSorting;
            _grid.Loaded += (_, __) => Attach();
            _grid.Unloaded += (_, __) => Detach();
            _grid.Columns.CollectionChanged += (_, __) =>
            {
                if (_grid.IsLoaded)
                {
                    Attach();
                }
            };
        }

        /// <summary>The item, while the bar holds one.</summary>
        public GridSortSelector Selector => _selector;

        /// <summary>Follows the grid control's control bar.</summary>
        public void SetBar(GridControlBarViewModel bar)
        {
            if (ReferenceEquals(_bar, bar))
            {
                Ensure();
                return;
            }

            if (_bar != null)
            {
                _bar.Items.CollectionChanged -= OnBarItemsChanged;
                if (_created != null)
                {
                    _bar.Items.Remove(_created);
                }
            }

            _created = null;
            _bar = bar;
            if (_bar != null)
            {
                _bar.Items.CollectionChanged += OnBarItemsChanged;
            }

            Ensure();
        }

        /// <summary>Finds or adds the item, then lists the columns and shows the current sort.</summary>
        public void Ensure()
        {
            var provides = _providesSelector();
            if (!provides && _created != null)
            {
                _bar?.Items.Remove(_created);
                _created = null;
            }

            var selector = _bar?.Items.OfType<GridSortSelector>().FirstOrDefault();
            if (selector == null && _bar != null && provides)
            {
                selector = _created ?? new GridSortSelector();
                _created = selector;
                _bar.Items.Add(selector);
            }

            if (!ReferenceEquals(selector, _selector))
            {
                ReleaseSelector();
                _selector = selector;
            }

            if (_selector != null && _grid.IsLoaded)
            {
                _selector.Apply = _apply;
                _selector.Opening = RefreshOptions;
                RefreshOptions();
                SyncState();
            }
        }

        private void Attach()
        {
            UnwatchColumns();
            foreach (var column in _grid.Columns)
            {
                SortDirectionDescriptor.AddValueChanged(column, OnColumnSortDirectionChanged);
                _watchedColumns.Add(column);
            }

            Ensure();
        }

        private void Detach()
        {
            UnwatchColumns();
            ReleaseSelector();
        }

        private void UnwatchColumns()
        {
            foreach (var column in _watchedColumns)
            {
                SortDirectionDescriptor.RemoveValueChanged(column, OnColumnSortDirectionChanged);
            }

            _watchedColumns.Clear();
        }

        // Another loaded grid sharing the bar takes the item over; an unloaded one lets it go.
        private void ReleaseSelector()
        {
            if (_selector != null && _selector.Apply == _apply)
            {
                _selector.Apply = null;
                _selector.Opening = null;
            }
        }

        // Items a host adds later (a filter, the category mode toggle) would land after the item;
        // move it back to the end. Deferred: the collection cannot change inside its own event.
        private void OnBarItemsChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (_placePending)
            {
                return;
            }

            _placePending = true;
            _grid.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                _placePending = false;
                var bar = _bar;
                if (bar == null)
                {
                    return;
                }

                var selector = bar.Items.OfType<GridSortSelector>().FirstOrDefault();
                if (selector == null)
                {
                    Ensure();
                    return;
                }

                var index = bar.Items.IndexOf(selector);
                if (index != bar.Items.Count - 1)
                {
                    bar.Items.Move(index, bar.Items.Count - 1);
                }
            }));
        }

        private void RefreshOptions()
        {
            if (_selector == null)
            {
                return;
            }

            // Columns sharing a sort (Achievement and Achievement Name; the three rarity columns)
            // are one option, named as the catalog names it so the default sort dropdown agrees.
            var options = _grid.Columns
                .Where(column => column != null &&
                                 column.CanUserSort &&
                                 !string.IsNullOrWhiteSpace(column.SortMemberPath) &&
                                 _isOffered(column))
                .OrderBy(column => column.DisplayIndex)
                .GroupBy(column => column.SortMemberPath, StringComparer.Ordinal)
                .Select(group => new GridSortOption(group.Key, ResolveLabel(group.ToList())))
                .ToList();
            if (!options.Select(option => option.Key + "\n" + option.Label)
                    .SequenceEqual(_selector.Options.Select(option => option.Key + "\n" + option.Label)))
            {
                _selector.Options = options;
            }
        }

        private string ResolveLabel(IReadOnlyList<DataGridColumn> columns)
        {
            var labelKey = columns.Count > 1 ? GridSortColumns.LabelKeyFor(_catalog, columns[0].SortMemberPath) : null;
            var label = labelKey == null ? null : ResourceProvider.GetString(labelKey);
            return string.IsNullOrWhiteSpace(label)
                ? DataGridColumnLayoutService.ResolveColumnDisplayName(columns[0])
                : label;
        }

        private void Apply(string key, ListSortDirection? direction)
        {
            DataGridColumn column = null;
            if (key != null)
            {
                column = _grid.Columns.FirstOrDefault(c =>
                    string.Equals(c?.SortMemberPath, key, StringComparison.Ordinal));
                if (column == null || !direction.HasValue)
                {
                    return;
                }
            }

            var request = new GridSortRequestEventArgs(column, key == null ? null : direction);
            _sort?.Invoke(request);
            AfterSort(request);
        }

        private void OnGridSorting(object sender, DataGridSortingEventArgs e) => AfterSort(e);

        private void AfterSort(DataGridSortingEventArgs e)
        {
            _unsorted = GridSortRequestEventArgs.WasReset(e);
            ScheduleSync();
        }

        private void OnColumnSortDirectionChanged(object sender, EventArgs e) => ScheduleSync();

        // A sort clears every arrow and sets one; read them once it settles.
        private void ScheduleSync()
        {
            if (_syncPending)
            {
                return;
            }

            // After the grid's own deferred work, so the item shows where the sort settled
            // rather than each step on the way.
            _syncPending = true;
            _grid.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
            {
                _syncPending = false;
                if (_unsorted)
                {
                    // The grid may show its default order's arrow after unsorting; the rows are
                    // unsorted as far as the user chose, so no column reads as sorted.
                    DataGridSortingHelper.ClearSortIndicators(_grid);
                }

                SyncState();
            }));
        }

        private void SyncState()
        {
            if (_unsorted)
            {
                _selector?.ShowState(null, null);
                return;
            }

            var sorted = _grid.Columns.FirstOrDefault(column =>
                column?.SortDirection != null && !string.IsNullOrWhiteSpace(column.SortMemberPath));
            _selector?.ShowState(sorted?.SortMemberPath, sorted?.SortDirection);
        }
    }
}
