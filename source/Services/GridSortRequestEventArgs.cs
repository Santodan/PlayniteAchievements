using System.ComponentModel;
using System.Runtime.CompilerServices;
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
        /// <param name="direction">The direction, or null to remove the sort.</param>
        public GridSortRequestEventArgs(DataGridColumn column, ListSortDirection? direction)
            : base(column)
        {
            Direction = direction;
        }

        /// <summary>The direction to sort in; null removes the sort, as the header cycle's last step does.</summary>
        public ListSortDirection? Direction { get; }

        /// <summary>True when the request removes the sort.</summary>
        public bool IsReset => !Direction.HasValue;

        /// <summary>The request <paramref name="e"/> carries, or null for a header click.</summary>
        public static GridSortRequestEventArgs Of(DataGridSortingEventArgs e) => e as GridSortRequestEventArgs;

        // Sorting events whose handling removed the sort, header clicks included.
        private static readonly ConditionalWeakTable<DataGridSortingEventArgs, object> Resets =
            new ConditionalWeakTable<DataGridSortingEventArgs, object>();

        private static readonly object ResetMarker = new object();

        /// <summary>
        /// Records that handling <paramref name="e"/> removed the sort (the header cycle's last
        /// step, or a request to unsort), so Sort By shows no sort after it.
        /// </summary>
        public static void MarkReset(DataGridSortingEventArgs e)
        {
            if (e != null && !Resets.TryGetValue(e, out _))
            {
                Resets.Add(e, ResetMarker);
            }
        }

        /// <summary>True when handling <paramref name="e"/> removed the sort.</summary>
        public static bool WasReset(DataGridSortingEventArgs e) =>
            e != null && Resets.TryGetValue(e, out _);
    }
}
