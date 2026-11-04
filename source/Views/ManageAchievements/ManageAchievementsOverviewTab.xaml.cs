using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using PlayniteAchievements.ViewModels.ManageAchievements;

namespace PlayniteAchievements.Views.ManageAchievements
{
    public partial class ManageAchievementsOverviewTab : UserControl
    {
        public ManageAchievementsOverviewTab()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Opens a button's context menu below it on a plain click, so the Export and Import
        /// buttons act as file-or-Workshop menus.
        /// </summary>
        private void ContextMenuButton_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is Button button) || button.ContextMenu == null)
            {
                return;
            }

            button.ContextMenu.PlacementTarget = button;
            button.ContextMenu.Placement = PlacementMode.Bottom;
            button.ContextMenu.IsOpen = true;
        }

        /// <summary>
        /// A link row's page button: opens the one page the link uses, or a menu naming each when
        /// it uses several.
        /// </summary>
        private void PageLinks_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is Button button) || !(button.DataContext is MetadataLinkRow row) || row.PageLinks.Count == 0)
            {
                return;
            }

            if (row.PageLinks.Count == 1)
            {
                row.PageLinks[0].OpenCommand.Execute(null);
                return;
            }

            var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
            foreach (var page in row.PageLinks)
            {
                menu.Items.Add(new MenuItem { Header = page.Label, ToolTip = page.Url, Command = page.OpenCommand });
            }

            menu.IsOpen = true;
        }
    }
}
