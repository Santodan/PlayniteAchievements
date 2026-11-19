using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using PlayniteAchievements.Services.Database;

namespace PlayniteAchievements.Views.Helpers
{
    /// <summary>
    /// Shows a <see cref="PagedRows{T}"/> in a <see cref="DataGrid"/>: sets it as the items
    /// source and turns column header sorts into query sorts, so the grid never sorts in memory.
    /// A sortable column names its <see cref="AchievementSort"/> in <c>SortMemberPath</c>.
    /// </summary>
    public static class PagedRowsBehavior
    {
        public static readonly DependencyProperty RowsProperty =
            DependencyProperty.RegisterAttached(
                "Rows",
                typeof(object),
                typeof(PagedRowsBehavior),
                new PropertyMetadata(null, OnRowsChanged));

        public static object GetRows(DependencyObject obj) => obj.GetValue(RowsProperty);

        public static void SetRows(DependencyObject obj, object value) => obj.SetValue(RowsProperty, value);

        private static void OnRowsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (!(d is DataGrid grid))
            {
                return;
            }

            grid.Sorting -= OnSorting;
            if (e.NewValue != null)
            {
                grid.Sorting += OnSorting;
            }

            DataGridSortingHelper.ClearSortIndicators(grid);
            grid.ItemsSource = e.NewValue as System.Collections.IEnumerable;
        }

        private static void OnSorting(object sender, DataGridSortingEventArgs e)
        {
            var grid = (DataGrid)sender;
            if (!(GetRows(grid) is ISortableRows rows))
            {
                return;
            }

            var direction = DataGridSortingHelper.HandleSorting(sender, e, grid);
            if (!direction.HasValue)
            {
                rows.SortBy(null, false);
            }
            else if (Enum.TryParse(e.Column.SortMemberPath, out AchievementSort sort))
            {
                rows.SortBy(sort, direction == ListSortDirection.Descending);
            }
            else
            {
                e.Column.SortDirection = null;
            }
        }
    }
}
