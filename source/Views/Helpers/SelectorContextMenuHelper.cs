using PlayniteAchievements.Services.UI;
using PlayniteAchievements.ViewModels.ManageAchievements;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace PlayniteAchievements.Views.Helpers
{
    /// <summary>
    /// Opens the summary-button style dropdowns the Manage Achievements tabs share: a button that
    /// pops its ContextMenu below itself, and the checkable category-type list built on top of it.
    /// </summary>
    internal static class SelectorContextMenuHelper
    {
        public static void Open(Button button, ContextMenu menu)
        {
            if (button == null || menu == null)
            {
                return;
            }

            RoutedEventHandler onClosed = null;
            onClosed = (_, __) =>
            {
                menu.Closed -= onClosed;
                button.ReleaseMouseCapture();
            };

            menu.Closed += onClosed;
            menu.PlacementTarget = button;
            menu.Placement = PlacementMode.Bottom;
            menu.HorizontalOffset = 0;
            menu.VerticalOffset = 0;
            if (button.IsKeyboardFocusWithin)
            {
                FullscreenControllerNavigationService.OpenContextMenu(button, menu);
            }
            else
            {
                menu.IsOpen = true;
            }
        }

        public static void OpenCategoryTypeMenu(
            Button button,
            ContextMenu menu,
            IEnumerable<CategoryTypeSelectionOption> options)
        {
            if (button == null || menu == null)
            {
                return;
            }

            menu.Items.Clear();

            var itemStyle = button.TryFindResource("AchievementMultiSelectMenuItemStyle") as Style;
            foreach (var option in options ?? Enumerable.Empty<CategoryTypeSelectionOption>())
            {
                if (option == null)
                {
                    continue;
                }

                var item = new MenuItem
                {
                    Header = option.DisplayName,
                    IsCheckable = true,
                    StaysOpenOnClick = true,
                    IsChecked = option.IsSelected
                };
                if (itemStyle != null)
                {
                    item.Style = itemStyle;
                }

                item.Click += (_, __) => option.IsSelected = item.IsChecked;
                menu.Items.Add(item);
            }

            if (menu.Items.Count == 0)
            {
                return;
            }

            Open(button, menu);
        }
    }
}
