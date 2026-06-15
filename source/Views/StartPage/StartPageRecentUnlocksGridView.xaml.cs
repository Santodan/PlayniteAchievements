using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using PlayniteAchievements.Views.Helpers;

namespace PlayniteAchievements.Views.StartPage
{
    public partial class StartPageRecentUnlocksGridView : UserControl
    {
        private DataGridRow _pendingRightClickRow;

        public StartPageRecentUnlocksGridView()
        {
            InitializeComponent();
        }

        private void GridRow_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!TryResolveContextMenuRow(sender, e, out var row))
            {
                return;
            }

            e.Handled = true;
            _pendingRightClickRow = row;
        }

        private void GridRow_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!TryResolveContextMenuRow(sender, e, out var row))
            {
                return;
            }

            e.Handled = true;
            var targetRow = _pendingRightClickRow ?? row;
            _pendingRightClickRow = null;
            OpenContextMenuForRow(targetRow);
        }

        private static bool TryResolveContextMenuRow(object sender, MouseButtonEventArgs e, out DataGridRow row)
        {
            row = sender as DataGridRow
                  ?? e?.Source as DataGridRow
                  ?? VisualTreeHelpers.FindVisualParent<DataGridRow>(e?.OriginalSource as System.Windows.DependencyObject);
            return row != null;
        }

        private bool OpenContextMenuForRow(DataGridRow row)
        {
            if (row == null || !row.IsLoaded || row.DataContext == null)
            {
                return false;
            }

            var menu = PlayniteAchievementsPlugin.Instance?.BuildStartPageRowContextMenu(
                row.DataContext,
                this,
                RefreshAfterRowOptionsChanged);
            if (menu == null || menu.Items.Count == 0)
            {
                return false;
            }

            row.ContextMenu = menu;
            menu.Placement = PlacementMode.MousePoint;
            menu.PlacementTarget = row;
            menu.IsOpen = true;
            return true;
        }

        private void RefreshAfterRowOptionsChanged()
        {
            PlayniteAchievementsPlugin.Instance?.PersistSettingsForUi();
            PlayniteAchievementsPlugin.Instance?.InvalidateStartPageDataForUi();
            RecentUnlocksGrid?.Refresh();
        }
    }
}
