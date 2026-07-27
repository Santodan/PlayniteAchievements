using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace PlayniteAchievements.Views.Helpers
{
    internal sealed class CompareMenuOption
    {
        public string Label { get; set; }
        public bool IsChecked { get; set; }
        public Action OnSelected { get; set; }
    }

    /// <summary>
    /// Opens the compare-friend single-select dropdown on a selector button's ContextMenu,
    /// mirroring the refresh-mode selector pattern (menu items rebuilt per open).
    /// </summary>
    internal static class CompareFriendMenuHelper
    {
        public static void Open(Button button, IReadOnlyList<CompareMenuOption> options)
        {
            var menu = button?.ContextMenu;
            if (menu == null || options == null || options.Count == 0)
            {
                return;
            }

            menu.Items.Clear();
            var itemStyle = button.TryFindResource("AchievementMultiSelectMenuItemStyle") as Style;
            foreach (var option in options)
            {
                if (option == null)
                {
                    continue;
                }

                var item = new MenuItem
                {
                    Header = option.Label,
                    IsCheckable = true,
                    IsChecked = option.IsChecked
                };
                if (itemStyle != null)
                {
                    item.Style = itemStyle;
                }

                var onSelected = option.OnSelected;
                item.Click += (_, __) => onSelected?.Invoke();
                menu.Items.Add(item);
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
