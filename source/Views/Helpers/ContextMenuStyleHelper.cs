using System.Windows;
using System.Windows.Controls;

namespace PlayniteAchievements.Views.Helpers
{
    internal static class ContextMenuStyleHelper
    {
        private const string ContextMenuStyleKey = "AchievementContextMenuStyle";
        private const string MenuItemStyleKey = "AchievementContextMenuItemStyle";
        private const string SeparatorStyleKey = "AchievementContextMenuSeparatorStyle";

        public static void ApplyAchievementContextMenuStyle(FrameworkElement resourceOwner, ContextMenu menu)
        {
            if (resourceOwner == null || menu == null)
            {
                return;
            }

            var menuStyle = resourceOwner.TryFindResource(ContextMenuStyleKey) as Style;
            var itemStyle = resourceOwner.TryFindResource(MenuItemStyleKey) as Style;
            var separatorStyle = resourceOwner.TryFindResource(SeparatorStyleKey) as Style;

            if (menuStyle != null)
            {
                menu.Style = menuStyle;
            }

            ApplyItemStyles(menu.Items, itemStyle, separatorStyle);
        }

        private static void ApplyItemStyles(ItemCollection items, Style itemStyle, Style separatorStyle)
        {
            if (items == null)
            {
                return;
            }

            foreach (var entry in items)
            {
                if (entry is MenuItem menuItem)
                {
                    if (itemStyle != null && menuItem.Style == null)
                    {
                        menuItem.Style = itemStyle;
                    }

                    ApplyItemStyles(menuItem.Items, itemStyle, separatorStyle);
                }
                else if (entry is Separator separator && separatorStyle != null)
                {
                    separator.Style = separatorStyle;
                }
            }
        }
    }
}
