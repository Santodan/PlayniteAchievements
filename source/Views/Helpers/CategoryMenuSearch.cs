using System;
using System.Linq;
using System.Windows.Controls;
using PlayniteAchievements.Services.Achievements;

namespace PlayniteAchievements.Views.Helpers
{
    /// <summary>
    /// The search of a menu that lists categories, one option per category with its label in Tag
    /// and its leaf name as the header: a category shows when its name holds the query or a category
    /// below it matches, so each match keeps the categories above it, as the category picker does.
    /// </summary>
    internal static class CategoryMenuSearch
    {
        public static void Attach(MenuItem categoryMenu, Action<string> create)
        {
            SelectorSearch.SetMenuMatcher(categoryMenu, Match);
            SelectorSearch.SetCreateAction(categoryMenu, create);
        }

        private static Func<MenuItem, bool> Match(string query)
        {
            return item =>
            {
                var label = item.Tag as string;
                return !string.IsNullOrEmpty(label) &&
                       (NameHolds(item, query) || HasMatchingDescendant(item, label, query));
            };
        }

        private static bool NameHolds(MenuItem item, string query) =>
            (item.Header as string ?? string.Empty).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;

        private static bool HasMatchingDescendant(MenuItem item, string label, string query)
        {
            var level = ItemsControl.ItemsControlFromItemContainer(item) ?? item.Parent as ItemsControl;
            return level != null && level.Items.OfType<MenuItem>().Any(other =>
                other.Tag is string otherLabel &&
                !ReferenceEquals(other, item) &&
                CategoryPathHelper.IsDescendantOf(otherLabel, label) &&
                NameHolds(other, query));
        }
    }
}
