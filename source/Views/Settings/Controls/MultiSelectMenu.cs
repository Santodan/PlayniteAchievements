using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace PlayniteAchievements.Views.Settings.Controls
{
    /// <summary>
    /// Shared plumbing for the checkable multi-select "dropdowns": a summary button that opens
    /// a menu of toggle items. The menu stays open across clicks, so toggle callbacks must read
    /// the current persisted value fresh instead of capturing a snapshot.
    /// </summary>
    internal static class MultiSelectMenu
    {
        public static MenuItem CreateItem(
            FrameworkElement resourceOwner,
            string header,
            bool isChecked,
            Action<bool> onToggle)
        {
            var item = new MenuItem
            {
                Header = header,
                IsCheckable = true,
                StaysOpenOnClick = true,
                IsChecked = isChecked
            };

            if (resourceOwner?.TryFindResource("AchievementMultiSelectMenuItemStyle") is Style itemStyle)
            {
                item.Style = itemStyle;
            }

            item.Click += (_, __) => onToggle?.Invoke(item.IsChecked);
            return item;
        }

        public static void Open(Button button, ContextMenu menu)
        {
            if (button == null || menu == null || menu.Items.Count == 0)
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
            menu.IsOpen = true;
        }
    }
}
