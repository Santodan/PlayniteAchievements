using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PlayniteAchievements.Views.Helpers
{
    /// <summary>
    /// Row selection toggling and deferred row right-click handling shared by the overview,
    /// achievements and friends code-behinds. XAML wires the row event handlers by name, so each
    /// control keeps a one-line handler that forwards here.
    /// </summary>
    internal static class DataGridRowInteraction
    {
        /// <summary>
        /// Resolves the row a mouse event belongs to: the sender, the routed source, or the
        /// nearest row ancestor of the original source.
        /// </summary>
        public static DataGridRow ResolveRow(object sender, MouseButtonEventArgs e)
        {
            return sender as DataGridRow
                   ?? e?.Source as DataGridRow
                   ?? VisualTreeHelpers.FindVisualParent<DataGridRow>(e?.OriginalSource as DependencyObject);
        }

        public static bool IsSelectedRow(DataGridRow row, DataGrid grid)
        {
            return row != null &&
                   grid != null &&
                   (row.IsSelected ||
                   ReferenceEquals(grid.SelectedItem, row.DataContext) ||
                   ReferenceEquals(grid.CurrentItem, row.DataContext));
        }

        public static void ClearGridSelection(DataGrid grid)
        {
            if (grid == null)
            {
                return;
            }

            try
            {
                grid.SelectedItem = null;
                grid.UnselectAll();
                grid.CurrentItem = null;
                Keyboard.ClearFocus();
            }
            catch
            {
                // Best effort; focus clearing should not break row toggling.
            }
        }

        /// <summary>
        /// Right button down on a row: claims the event and remembers the row so the matching
        /// button up opens the menu for the row the press started on.
        /// </summary>
        public static void BeginRowRightClick(DataGridRow row, MouseButtonEventArgs e, ref DataGridRow pendingRow)
        {
            if (row == null)
            {
                return;
            }

            e.Handled = true;
            pendingRow = row;
        }

        /// <summary>
        /// Right button up on a row: claims the event, clears the pending row and opens the menu
        /// for the pending row (or the release row when no press was recorded).
        /// </summary>
        public static void CompleteRowRightClick(
            DataGridRow row,
            MouseButtonEventArgs e,
            ref DataGridRow pendingRow,
            Action<DataGridRow> openMenu)
        {
            if (row == null)
            {
                return;
            }

            e.Handled = true;
            var targetRow = pendingRow ?? row;
            pendingRow = null;
            openMenu(targetRow);
        }
    }
}
