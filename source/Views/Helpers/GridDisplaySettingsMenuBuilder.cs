using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Views.Controls;
using PlayniteAchievements.Views.Dialogs;
using PlayniteAchievements.Views.Settings.Controls;

namespace PlayniteAchievements.Views.Helpers
{
    /// <summary>
    /// Adds the "Display settings…" entry that opens <see cref="GridDisplaySettingsDialog"/> for
    /// whichever grid was right-clicked.
    ///
    /// The surface is resolved from the clicked element's ancestor grid control rather than passed
    /// in by the host, so the runtime-varying keys (the friend achievement grids rebind
    /// ColumnSettingsKey as the selection changes) are always read in their current state, and
    /// there is no second surface mapping to drift from the one the grids themselves use.
    /// </summary>
    internal static class GridDisplaySettingsMenuBuilder
    {
        private const string MenuItemKey = "LOCPlayAch_Menu_DisplaySettings";

        /// <summary>
        /// Appends the display settings item to an existing row menu, preceded by a separator when
        /// the menu already has entries. Returns false and appends nothing when the grid cannot be
        /// resolved.
        /// </summary>
        public static bool Append(ContextMenu menu, FrameworkElement resourceOwner, DependencyObject source)
        {
            if (menu == null || !TryResolveSurface(source, out var kind, out var surfaceKey, out var categoryKey))
            {
                return false;
            }

            if (menu.Items.Count > 0)
            {
                menu.Items.Add(new Separator());
            }

            menu.Items.Add(CreateItem(resourceOwner, kind, surfaceKey, categoryKey));
            return true;
        }

        /// <summary>
        /// A menu holding only the display settings item, for grids with no row to right-click.
        /// Returns null when the grid cannot be resolved.
        /// </summary>
        public static ContextMenu BuildStandalone(FrameworkElement resourceOwner, DependencyObject source)
        {
            if (!TryResolveSurface(source, out var kind, out var surfaceKey, out var categoryKey))
            {
                return null;
            }

            var menu = new ContextMenu();
            menu.Items.Add(CreateItem(resourceOwner, kind, surfaceKey, categoryKey));
            return menu;
        }

        /// <summary>
        /// Opens the display settings menu for a right-click that landed on the grid itself rather
        /// than on a row, so the settings stay reachable in a grid with nothing to right-click.
        /// Callers handle a column-header hit first; this declines a row hit so the row menu runs,
        /// and declines a scrollbar hit.
        /// </summary>
        public static bool TryOpenFallbackMenu(FrameworkElement host, DataGrid grid, MouseButtonEventArgs e)
        {
            if (host == null || grid == null || e == null)
            {
                return false;
            }

            var clicked = e.OriginalSource as DependencyObject;
            if (VisualTreeHelpers.FindVisualParent<DataGridRow>(clicked) != null
                || VisualTreeHelpers.FindVisualParent<ScrollBar>(clicked) != null)
            {
                return false;
            }

            var menu = BuildStandalone(host, grid);
            if (menu == null)
            {
                return false;
            }

            ContextMenuStyleHelper.ApplyAchievementContextMenuStyle(host, menu);
            menu.PlacementTarget = grid;
            menu.Placement = PlacementMode.MousePoint;
            menu.IsOpen = true;
            e.Handled = true;
            return true;
        }

        /// <summary>
        /// Opens the display settings menu on a row whose host offers no row menu of its own, so
        /// no grid has a dead right-click.
        /// </summary>
        public static bool TryOpenRowFallbackMenu(FrameworkElement host, DataGridRow row, MouseButtonEventArgs e)
        {
            if (host == null || row == null || e == null || row.ContextMenu != null)
            {
                return false;
            }

            var menu = BuildStandalone(host, row);
            if (menu == null)
            {
                return false;
            }

            ContextMenuStyleHelper.ApplyAchievementContextMenuStyle(host, menu);
            menu.PlacementTarget = row;
            menu.IsOpen = true;
            e.Handled = true;
            return true;
        }

        private static MenuItem CreateItem(
            FrameworkElement resourceOwner,
            GridOptionKind kind,
            string surfaceKey,
            string categoryKey)
        {
            return GameRowContextMenuBuilder.CreateMenuItem(
                resourceOwner,
                MenuItemKey,
                () => GridDisplaySettingsDialog.Show(kind, surfaceKey, categoryKey));
        }

        /// <summary>
        /// Walks up from the clicked element to the grid control that owns it and reads its live
        /// surface key.
        /// </summary>
        internal static bool TryResolveSurface(
            DependencyObject source,
            out GridOptionKind kind,
            out string surfaceKey,
            out string categorySurfaceKey)
        {
            kind = GridOptionKind.Achievement;
            surfaceKey = null;
            categorySurfaceKey = null;

            if (source == null)
            {
                return false;
            }

            var achievementHost = VisualTreeHelpers.FindVisualParent<AchievementDataGridControl>(source);

            // The category drill header and category list live inside an achievement grid and
            // carry its resolved category key. Resolve the parent achievement surface instead:
            // its popup already contains the category record, so a category row and an
            // achievement row open the same window.
            if (achievementHost != null)
            {
                if (!achievementHost.AllowDisplaySettingsMenu)
                {
                    return false;
                }

                var rawKey = achievementHost.ColumnSettingsKey;

                kind = GridOptionKind.Achievement;
                if (!GridOptionsCatalog.TryResolveAchievementId(rawKey, out surfaceKey))
                {
                    return false;
                }

                categorySurfaceKey = GridOptionsCatalog.ResolveCategorySummariesId(
                    achievementHost.ResolvedCategoryColumnSettingsKey);
                return true;
            }

            var gameHost = VisualTreeHelpers.FindVisualParent<GameSummariesGridControl>(source);
            if (gameHost != null)
            {
                if (!gameHost.AllowDisplaySettingsMenu)
                {
                    return false;
                }

                var rawKey = gameHost.ColumnSettingsKey;

                kind = GridOptionKind.GameSummaries;
                return GridOptionsCatalog.TryResolveGameSummariesId(rawKey, out surfaceKey);
            }

            var friendHost = VisualTreeHelpers.FindVisualParent<FriendSummariesGridControl>(source);
            if (friendHost != null)
            {
                if (!friendHost.AllowDisplaySettingsMenu)
                {
                    return false;
                }

                kind = GridOptionKind.FriendSummaries;
                return GridOptionsCatalog.TryResolveFriendSummariesId(
                    friendHost.ColumnSettingsKey,
                    out surfaceKey);
            }

            return false;
        }
    }
}
