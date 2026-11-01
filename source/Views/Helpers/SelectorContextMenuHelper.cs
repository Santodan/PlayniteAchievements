using PlayniteAchievements.Services.UI;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.ViewModels.ManageAchievements;
using System;
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
        public static void Open(ButtonBase button, ContextMenu menu)
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
            IEnumerable<TagSelectionOption> options,
            Guid? gameId)
        {
            if (button == null || menu == null)
            {
                return;
            }

            menu.Items.Clear();

            var shown = (options ?? Enumerable.Empty<TagSelectionOption>()).Where(option => option != null).ToList();
            var iconKeys = TagIconKeys(shown.Select(option => option.Value), gameId);
            var itemStyle = button.TryFindResource("AchievementMultiSelectMenuItemStyle") as Style;
            for (var i = 0; i < shown.Count; i++)
            {
                var option = shown[i];
                var item = new MenuItem
                {
                    IsCheckable = true,
                    StaysOpenOnClick = true,
                    IsChecked = option.IsSelected
                };
                if (itemStyle != null)
                {
                    item.Style = itemStyle;
                }

                SetTagHeader(item, iconKeys, i, option.DisplayName);

                item.Click += (_, __) => option.IsSelected = item.IsChecked;
                menu.Items.Add(item);
            }

            if (menu.Items.Count == 0)
            {
                return;
            }

            Open(button, menu);
        }

        /// <summary>
        /// Each tag's icon key, in order, when at least one of the tags has an icon; null when none
        /// does, so the menu keeps plain labels without an icon gutter.
        /// </summary>
        public static IReadOnlyList<string> TagIconKeys(IEnumerable<string> tags, Guid? gameId)
        {
            var keys = tags.Select(tag => TagControlBarFilter.IconKey(tag, gameId)).ToList();
            return keys.Any(key => key != null) ? keys : null;
        }

        /// <summary>
        /// Sets a tag menu item's header: its icon before its label when the menu has an icon gutter
        /// (a tag without an icon keeps the gutter's width so labels align), else the label.
        /// </summary>
        public static void SetTagHeader(MenuItem item, IReadOnlyList<string> iconKeys, int index, string label) =>
            MultiSelectFilterMenu.SetHeader(
                item,
                iconKeys == null ? (object)label : MultiSelectFilterMenu.BuildIconHeader(iconKeys[index], label));
    }
}
