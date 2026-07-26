using System;
using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Data;

namespace PlayniteAchievements.Views.Helpers
{
    /// <summary>
    /// Provides uniform sorting behavior for DataGrid controls.
    /// </summary>
    public static class DataGridSortingHelper
    {
        /// <summary>
        /// Handles the DataGrid.Sorting event with uniform tri-state sort direction toggling.
        /// Sets e.Handled to true, toggles the sort direction, clears other columns' sort indicators,
        /// and returns the computed sort direction for the caller to apply. The cycle is
        /// unsorted -> ascending -> descending -> unsorted.
        /// </summary>
        /// <param name="sender">The object that raised the Sorting event (typically a DataGrid or wrapper control).</param>
        /// <param name="e">The DataGridSortingEventArgs.</param>
        /// <param name="dataGrid">Optional DataGrid to use for clearing other columns' sort indicators.
        /// Required when sender is not a DataGrid (e.g., when using wrapper controls with external sorting).</param>
        /// <returns>The new sort direction, or null if the column is invalid.</returns>
        public static ListSortDirection? HandleSorting(object sender, DataGridSortingEventArgs e, DataGrid dataGrid = null)
        {
            e.Handled = true;

            var column = e.Column;
            if (column == null || string.IsNullOrEmpty(column.SortMemberPath))
            {
                return null;
            }

            ListSortDirection? sortDirection = ListSortDirection.Ascending;
            if (column.SortDirection == ListSortDirection.Ascending)
            {
                sortDirection = ListSortDirection.Descending;
            }
            else if (column.SortDirection == ListSortDirection.Descending)
            {
                sortDirection = null;
            }

            // Clear all columns' sort direction on the DataGrid if provided
            var targetGrid = dataGrid ?? (sender as DataGrid);
            ClearSortIndicators(targetGrid);

            if (sortDirection.HasValue)
            {
                column.SortDirection = sortDirection;
            }

            return sortDirection;
        }

        public static ListSortDirection? ApplyCollectionViewSorting(
            object sender,
            DataGridSortingEventArgs e,
            DataGrid dataGrid)
        {
            var sortDirection = HandleSorting(sender, e, dataGrid);
            var view = CollectionViewSource.GetDefaultView(dataGrid?.ItemsSource);
            if (view == null)
            {
                return sortDirection;
            }

            view.SortDescriptions.Clear();
            if (sortDirection.HasValue && !string.IsNullOrWhiteSpace(e.Column?.SortMemberPath))
            {
                view.SortDescriptions.Add(new SortDescription(e.Column.SortMemberPath, sortDirection.Value));
            }

            view.Refresh();
            return sortDirection;
        }

        public static void ClearSortIndicators(DataGrid dataGrid)
        {
            if (dataGrid?.Columns == null)
            {
                return;
            }

            foreach (var column in dataGrid.Columns)
            {
                column.SortDirection = null;
            }
        }

        public static void SetSortIndicator(
            DataGrid dataGrid,
            string sortMemberPath,
            ListSortDirection? direction)
        {
            ClearSortIndicators(dataGrid);
            if (dataGrid?.Columns == null ||
                direction == null ||
                string.IsNullOrWhiteSpace(sortMemberPath))
            {
                return;
            }

            foreach (var column in dataGrid.Columns)
            {
                if (string.Equals(column?.SortMemberPath, sortMemberPath, StringComparison.Ordinal))
                {
                    column.SortDirection = direction;
                    return;
                }
            }
        }
    }
}
