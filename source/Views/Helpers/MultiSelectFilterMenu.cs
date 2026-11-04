using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using PlayniteAchievements.Services.UI;
using PlayniteAchievements.ViewModels.Items;
using PlayniteAchievements.Views.Controls;

namespace PlayniteAchievements.Views.Helpers
{
    /// <summary>
    /// Builds and opens the checkable drop-down behind a multi-select filter button.
    /// </summary>
    /// <remarks>
    /// One copy, shared by the grid control bar and the Manage Achievements editor's filter row.
    /// The menu is populated per opening rather than bound, because its rows carry per-option
    /// state -- the marker gutter and the category tree connectors -- that is decided by the whole
    /// option set rather than by any one option.
    /// </remarks>
    internal static class MultiSelectFilterMenu
    {
        private const int VirtualizeAbove = 100;

        /// <summary>
        /// Fills <paramref name="button"/>'s context menu with one checkable row per option and
        /// opens it beneath the button.
        /// </summary>
        public static void Open(Button button, GridMultiSelectFilter filter)
        {
            var menu = button?.ContextMenu;
            if (button == null || filter == null || menu == null)
            {
                return;
            }

            menu.Items.Clear();
            var options = (filter.Options ?? Enumerable.Empty<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToList();
            // Only reserve the marker gutter when at least one option is actually marked; with none
            // marked the dropdown drops the gutter entirely and labels sit flush left.
            var showMarkerGutter = filter.HasMarker && options.Any(filter.IsMarked);
            // Likewise the badge gutter: only when some option has a badge to show.
            var showIconGutter = filter.HasIcons && options.Any(option => !string.IsNullOrWhiteSpace(filter.IconKeyOf(option)));

            // Every checkable item created for this opening, so a click can re-sync its siblings.
            var checkableItems = new List<MenuItem>();

            if (filter.RendersCategoryTree)
            {
                AppendCategoryTreeItems(
                    menu.Items,
                    button,
                    filter,
                    options,
                    showMarkerGutter,
                    showIconGutter,
                    checkableItems);
            }
            else
            {
                // A long list (the Game filter over a whole library) lays out only the rows in
                // view; laying out every row made the drop-down slow to open.
                if (options.Count > VirtualizeAbove &&
                    button.TryFindResource("AchievementSelectorVirtualizingContextMenuStyle") is Style virtualizing)
                {
                    menu.Style = virtualizing;
                }

                var itemStyle = button.TryFindResource("AchievementMultiSelectMenuItemStyle") as Style;
                foreach (var option in options)
                {
                    menu.Items.Add(CreateMultiSelectItem(
                        filter,
                        option,
                        filter.GetDisplayLabel(option),
                        itemStyle,
                        showMarkerGutter,
                        showIconGutter,
                        checkableItems));
                }
            }

            OpenSelectorContextMenu(button, menu);
        }

        /// <summary>
        /// Renders category-path options as the tree they describe: one row per node showing its
        /// leaf name, with the connectors the category grid draws standing in for the path.
        ///
        /// Flat rather than nested submenus, so every option and every checkmark is visible at
        /// once - which is what a multi-select dropdown is for, and it matches the category grid
        /// and the Manage Achievements dropdowns.
        ///
        /// A node absent from the options holds no achievements of its own. It is drawn, because
        /// its children need a parent to hang off, but it is not checkable: it would match nothing.
        /// </summary>
        private static void AppendCategoryTreeItems(
            ItemCollection target,
            FrameworkElement resourceOwner,
            GridMultiSelectFilter filter,
            IReadOnlyList<string> options,
            bool showMarkerGutter,
            bool showIconGutter,
            List<MenuItem> checkableItems)
        {
            var rows = CategoryFilterMenuBuilder.BuildRows(options);
            var itemStyle = CategoryFilterMenuBuilder.ResolveItemStyle(resourceOwner, rows);

            foreach (var row in rows)
            {
                if (!row.IsSelectable)
                {
                    // Structure only: never checked, never re-synced, and nothing to select.
                    target.Add(CategoryFilterMenuBuilder.CreateItem(
                        row,
                        itemStyle,
                        _ => false,
                        (_, __) => { }));
                    continue;
                }

                target.Add(CreateMultiSelectItem(
                    filter,
                    row.Option,
                    row.LeafDisplay,
                    itemStyle,
                    showMarkerGutter,
                    showIconGutter,
                    checkableItems,
                    row.TreeShape,
                    row.PathDisplay));
            }
        }

        /// <summary>
        /// One checkable row. <paramref name="treeShape"/> and <paramref name="toolTip"/> are set
        /// only for category paths, where the row also has to say where it sits and offer its full
        /// path on hover; every other dropdown leaves them null and the guide collapses away.
        /// </summary>
        private static MenuItem CreateMultiSelectItem(
            GridMultiSelectFilter filter,
            string value,
            string label,
            Style itemStyle,
            bool showMarkerGutter,
            bool showIconGutter,
            List<MenuItem> checkableItems,
            CategoryTreeShape treeShape = null,
            string toolTip = null)
        {
            var item = new CategoryTreeMenuItem
            {
                ToolTip = toolTip,
                TreeShape = treeShape,
                IsCheckable = true,
                StaysOpenOnClick = true,
                IsChecked = filter.IsSelected(value),
                Tag = value
            };
            if (itemStyle != null)
            {
                item.Style = itemStyle;
            }

            SetHeader(item, BuildMultiSelectHeader(filter, value, label, showMarkerGutter, showIconGutter));

            item.Click += (_, __) =>
            {
                filter.SetSelected(value, item.IsChecked);

                // Re-sync every sibling checkmark from the filter while the menu stays
                // open: single-select-style filters (e.g. Compare) uncheck the previous
                // option when a new one is selected. No-op for plain multi-selects.
                foreach (var sibling in checkableItems)
                {
                    if (sibling.Tag is string optionValue)
                    {
                        sibling.IsChecked = filter.IsSelected(optionValue);
                    }
                }
            };

            checkableItems.Add(item);
            return item;
        }

        // Plain string header for ordinary dropdowns; for a marker-aware filter (e.g. the friend
        // Compare dropdown) render a fixed-width star gutter before the label so marked options show
        // a star while every label stays aligned (unmarked options keep a transparent star). The
        // gutter is only shown when the dropdown has at least one marked option.
        private static object BuildMultiSelectHeader(
            GridMultiSelectFilter filter,
            string value,
            string label,
            bool showMarkerGutter,
            bool showIconGutter)
        {
            if (showIconGutter)
            {
                return BuildIconHeader(filter.IconKeyOf(value), label);
            }

            if (!showMarkerGutter)
            {
                return label;
            }

            var panel = new DockPanel { LastChildFill = true };
            var star = new TextBlock
            {
                Text = "★",
                Width = 14,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = filter.IsMarked(value) ? 1d : 0d
            };
            DockPanel.SetDock(star, Dock.Left);
            panel.Children.Add(star);
            panel.Children.Add(new TextBlock
            {
                Text = label,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap
            });
            return panel;
        }

        /// <summary>
        /// Sets a menu item's header. The multi-select menu-item style forces a string
        /// HeaderTemplate (TextBlock Text={Binding}), which would ToString() a UIElement header, so
        /// a panel header clears it locally (a directly-set property beats the style setter).
        /// </summary>
        internal static void SetHeader(MenuItem item, object header)
        {
            item.Header = header;
            if (!(header is string))
            {
                item.HeaderTemplate = null;
            }
        }

        // The option's badge before its label; an option without one keeps the badge's width so
        // the labels stay aligned.
        internal static object BuildIconHeader(string iconKey, string label)
        {
            var panel = new DockPanel { LastChildFill = true };
            var icon = new Image
            {
                Width = 16,
                Height = 16,
                Margin = new Thickness(0, 0, 6, 0),
                Stretch = System.Windows.Media.Stretch.Uniform,
                VerticalAlignment = VerticalAlignment.Center
            };
            ImageResourceKey.SetKey(icon, iconKey);
            DockPanel.SetDock(icon, Dock.Left);
            panel.Children.Add(icon);
            panel.Children.Add(new TextBlock
            {
                Text = label,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap
            });
            return panel;
        }

        public static void OpenSelectorContextMenu(Button button, ContextMenu menu, bool allowEmptyItems = false)
        {
            if (button == null || menu == null || (!allowEmptyItems && menu.Items.Count == 0))
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
    }
}
