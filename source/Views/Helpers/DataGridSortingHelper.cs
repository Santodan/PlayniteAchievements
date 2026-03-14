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

            var sortMemberPath = column.SortMemberPath;
            var sortDirection = ListSortDirection.Ascending;
            if (column.SortDirection == ListSortDirection.Ascending)
            {
                sortDirection = ListSortDirection.Descending;
            }

            // Clear all columns' sort direction first, then set the target column
            // Use provided dataGrid parameter, or fall back to sender if it's a DataGrid
            var targetGrid = dataGrid ?? (sender as DataGrid);
            if (targetGrid != null)
            {
                foreach (var c in targetGrid.Columns)
                {
                    c.SortDirection = null;
                }

                // Find and set the target column by SortMemberPath to ensure we update the correct column
                var targetColumn = FindColumnBySortMemberPath(targetGrid, sortMemberPath);
                if (targetColumn != null)
                {
                    targetColumn.SortDirection = sortDirection;
                }
            }
            else
            {
                // Fallback: set directly on e.Column
                column.SortDirection = sortDirection;
            }

            return sortDirection;
        }

        private static DataGridColumn FindColumnBySortMemberPath(DataGrid grid, string sortMemberPath)
        {
            if (grid == null || string.IsNullOrEmpty(sortMemberPath))
            {
                return null;
            }

            foreach (var column in grid.Columns)
            {
                if (column.SortMemberPath == sortMemberPath)
                {
                    return column;
                }
            }

            return null;
        }
    }
}
