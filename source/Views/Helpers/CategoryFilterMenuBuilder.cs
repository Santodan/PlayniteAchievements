using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.ViewModels.Items;
using PlayniteAchievements.Views.Controls;

namespace PlayniteAchievements.Views.Helpers
{
    /// <summary>
    /// One row of a category dropdown: the caller's own option string, what the row shows, and
    /// where it sits in the tree.
    /// </summary>
    internal sealed class CategoryMenuRow
    {
        public CategoryMenuRow(
            string option,
            string leafDisplay,
            string pathDisplay,
            CategoryTreeShape treeShape)
        {
            Option = option;
            LeafDisplay = leafDisplay;
            PathDisplay = pathDisplay;
            TreeShape = treeShape;
        }

        /// <summary>
        /// The caller's own string, not the path rebuilt from its segments, so selections stay
        /// comparable to the option list that prunes them. Null for a row the tree synthesised,
        /// which is what marks it as structure rather than a choice.
        /// </summary>
        public string Option { get; }

        public string LeafDisplay { get; }

        public string PathDisplay { get; }

        /// <summary>Null when the list has no nesting, which collapses the guide to zero width.</summary>
        public CategoryTreeShape TreeShape { get; }

        public bool IsSelectable => Option != null;
    }

    /// <summary>
    /// Arranges category-path options as the tree they describe, for the dropdowns that offer them.
    ///
    /// Every category dropdown shows one row per node with the leaf name only, the full path on
    /// hover, and the same connectors the category grid draws standing in for the path - so the
    /// same picture means the same thing wherever categories are listed.
    ///
    /// An ancestor missing from the options holds no achievements of its own. It is drawn, because
    /// its children need a parent to hang off, but it is not a choice: checking it would match
    /// nothing.
    /// </summary>
    internal static class CategoryFilterMenuBuilder
    {
        /// <summary>Style key for a list that nests, which draws the connectors.</summary>
        private const string TreeItemStyleKey = "AchievementCategoryTreeMenuItemStyle";

        /// <summary>Style key for a list with nothing to nest, which stays exactly as it was.</summary>
        private const string FlatItemStyleKey = "AchievementMultiSelectMenuItemStyle";

        /// <summary>
        /// The rows for a set of category options, in tree order: siblings contiguous, each node
        /// immediately followed by its own subtree.
        /// </summary>
        public static IReadOnlyList<CategoryMenuRow> BuildRows(IEnumerable<string> options)
        {
            var present = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var option in options ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(option))
                {
                    continue;
                }

                var key = CategoryPathHelper.NormalizePath(option);
                if (!present.ContainsKey(key))
                {
                    present[key] = option;
                }
            }

            var rows = new List<CategoryMenuRow>(present.Count);
            if (present.Count == 0)
            {
                return rows;
            }

            foreach (var built in CategoryPickerResolver.BuildOptions(
                present.Values,
                synthesizedAreSelectable: false))
            {
                present.TryGetValue(built.Label, out var option);
                rows.Add(new CategoryMenuRow(
                    built.IsSelectable ? option : null,
                    built.LeafDisplay,
                    built.PathDisplay,
                    built.TreeShape));
            }

            return rows;
        }

        /// <summary>
        /// The menu-item style these rows want. Only a nesting list pays for the tree style; a flat
        /// one keeps the plain row style it has always had, down to the 1px it puts between rows.
        /// </summary>
        public static Style ResolveItemStyle(
            FrameworkElement resourceOwner,
            IReadOnlyList<CategoryMenuRow> rows)
        {
            var nests = rows != null && rows.Any(row => row.TreeShape != null);
            return resourceOwner?.TryFindResource(nests ? TreeItemStyleKey : FlatItemStyleKey) as Style;
        }

        /// <summary>
        /// Replaces <paramref name="menu"/>'s items with one row per category. Returns false when
        /// there is nothing to show, so the caller can leave the menu closed.
        /// </summary>
        public static bool Populate(
            FrameworkElement resourceOwner,
            ContextMenu menu,
            IEnumerable<string> options,
            Func<string, bool> isSelected,
            Action<string, bool> setSelection)
        {
            if (menu == null || isSelected == null || setSelection == null)
            {
                return false;
            }

            menu.Items.Clear();

            var rows = BuildRows(options);
            if (rows.Count == 0)
            {
                return false;
            }

            var itemStyle = ResolveItemStyle(resourceOwner, rows);
            foreach (var row in rows)
            {
                menu.Items.Add(CreateItem(row, itemStyle, isSelected, setSelection));
            }

            return menu.Items.Count > 0;
        }

        /// <summary>
        /// A row with its connectors, checkable only when it is a category the caller offers.
        /// </summary>
        public static CategoryTreeMenuItem CreateItem(
            CategoryMenuRow row,
            Style itemStyle,
            Func<string, bool> isSelected,
            Action<string, bool> setSelection)
        {
            var item = new CategoryTreeMenuItem
            {
                Header = row.LeafDisplay,
                ToolTip = row.PathDisplay,
                TreeShape = row.TreeShape,
                IsCheckable = row.IsSelectable,
                // Structural rows keep this too: a click on one should do nothing, not close the
                // menu out from under someone part way through picking.
                StaysOpenOnClick = true,
                IsChecked = row.IsSelectable && isSelected(row.Option)
            };

            if (itemStyle != null)
            {
                item.Style = itemStyle;
            }

            if (row.IsSelectable)
            {
                item.Click += (_, __) => setSelection(row.Option, item.IsChecked);
            }

            return item;
        }
    }
}
