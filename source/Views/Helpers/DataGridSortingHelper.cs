using System.ComponentModel;
using System.Windows.Controls;

namespace PlayniteAchievements.Views.Helpers
{
    /// <summary>
    /// Provides uniform sorting behavior for DataGrid controls.
    /// </summary>
    public static class DataGridSortingHelper
    {
        /// <summary>
        /// Handles the DataGrid.Sorting event with uniform sort direction toggling.
        /// Sets e.Handled to true, toggles the sort direction, clears other columns' sort indicators,
        /// and returns the computed sort direction for the caller to apply.
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

            var sortDirection = ListSortDirection.Ascending;
            if (column.SortDirection == ListSortDirection.Ascending)
            {
                sortDirection = ListSortDirection.Descending;
            }

            // Clear all columns' sort direction on the DataGrid if provided
            var targetGrid = dataGrid ?? (sender as DataGrid);
            if (targetGrid != null)
            {
                foreach (var c in targetGrid.Columns)
                {
                    c.SortDirection = null;
                }
            }

            // Always set the sort direction on the column that was clicked
            column.SortDirection = sortDirection;

            return sortDirection;
        }
    }
}
