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
        /// Sort member paths for text-like columns that should default to Ascending (A→Z)
        /// on first click.
        /// </summary>
        private static readonly System.Collections.Generic.HashSet<string> TextAscendingDefaultPaths =
            new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase)
            {
                "SortingName",   // Game name column in GamesOverview
                "DisplayName",   // Achievement display name
                "Name",          // Generic name fallback
                "ApiName",       // Achievement API name
                "CategoryType",
                "CategoryLabel",
                "TrophyType",
            };

        /// <summary>
        /// Sort member paths for date/time columns that should default to Descending
        /// (most recent first) on first click.
        /// </summary>
        private static readonly System.Collections.Generic.HashSet<string> DateTimeDescendingDefaultPaths =
            new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase)
            {
                "UnlockTime",
                "LastPlayed",
            };

        /// <summary>
        /// Handles the DataGrid.Sorting event with uniform sort direction toggling.
        /// Sets e.Handled to true, toggles the sort direction, clears other columns' sort indicators,
        /// and returns the computed sort direction for the caller to apply.
        /// </summary>
        /// <param name="sender">The object that raised the Sorting event (typically a DataGrid or wrapper control).</param>
        /// <param name="e">The DataGridSortingEventArgs.</param>
        /// <param name="dataGrid">Optional DataGrid to use for clearing other columns' sort indicators.
        /// Required when sender is not a DataGrid (e.g., when using wrapper controls with external sorting).</param>
        /// <param name="clearOtherColumns">When false (Ctrl+Click multi-sort), other columns' sort indicators
        /// are preserved so all active sort columns remain visible.</param>
        /// <returns>The new sort direction, or null if the column is invalid.</returns>
        public static ListSortDirection? HandleSorting(object sender, DataGridSortingEventArgs e, DataGrid dataGrid = null, bool clearOtherColumns = true)
        {
            e.Handled = true;

            var column = e.Column;
            if (column == null || string.IsNullOrEmpty(column.SortMemberPath))
            {
                return null;
            }

            // First click behavior:
            // - text-like columns: Ascending (A→Z)
            // - date/time columns: Descending (newest → oldest)
            // - all others: Descending
            var isTextColumn = TextAscendingDefaultPaths.Contains(column.SortMemberPath);
            var isDateTimeColumn = DateTimeDescendingDefaultPaths.Contains(column.SortMemberPath);
            ListSortDirection sortDirection;
            if (column.SortDirection == null)
            {
                sortDirection = isTextColumn && !isDateTimeColumn
                    ? ListSortDirection.Ascending
                    : ListSortDirection.Descending;
            }
            else
            {
                sortDirection = column.SortDirection == ListSortDirection.Descending
                    ? ListSortDirection.Ascending
                    : ListSortDirection.Descending;
            }

            // Clear all columns' sort direction on the DataGrid when not in additive mode
            if (clearOtherColumns)
            {
                var targetGrid = dataGrid ?? (sender as DataGrid);
                if (targetGrid != null)
                {
                    foreach (var c in targetGrid.Columns)
                    {
                        c.SortDirection = null;
                    }
                }
            }

            // Always set the sort direction on the column that was clicked
            column.SortDirection = sortDirection;

            return sortDirection;
        }
    }
}
