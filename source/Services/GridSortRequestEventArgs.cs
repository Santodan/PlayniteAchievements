using System.ComponentModel;
using System.Windows.Controls;

namespace PlayniteAchievements.Services
{
    /// <summary>
    /// A grid sort chosen outright, by the control bar's Sort By, rather than by a header click.
    /// It travels through the grid's Sorting event like a header click, so every handler that
    /// sorts a grid sorts it the same way for both; the handlers apply <see cref="Direction"/>
    /// in place of stepping the header click cycle.
    /// </summary>
    public sealed class GridSortRequestEventArgs : DataGridSortingEventArgs
    {
        /// <param name="column">The column to sort by; null with a null direction.</param>
        /// <param name="direction">The direction, or null to return to the grid's default sort.</param>
        public GridSortRequestEventArgs(DataGridColumn column, ListSortDirection? direction)
            : base(column)
        {
            Direction = direction;
        }

        /// <summary>The direction to sort in; null returns the grid to its default sort.</summary>
        public ListSortDirection? Direction { get; }

        /// <summary>True when the request returns the grid to its default sort.</summary>
        public bool IsReset => !Direction.HasValue;

        /// <summary>The request <paramref name="e"/> carries, or null for a header click.</summary>
        public static GridSortRequestEventArgs Of(DataGridSortingEventArgs e) => e as GridSortRequestEventArgs;
    }
}
