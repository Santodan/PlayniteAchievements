using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.ViewModels.Items
{
    /// <summary>
    /// The order of a control bar's items: the default order a bar builds, overlaid by the order
    /// saved for it (item ids, earliest first). One rule serves the bar, its Filters panel and the
    /// settings lists that edit the order.
    /// </summary>
    public static class GridControlBarOrder
    {
        /// <summary>
        /// <paramref name="defaultOrder"/> rearranged by <paramref name="saved"/>: an item the
        /// saved order names takes its place there, and any other item (one with no id, or one
        /// added after the order was saved) follows the item it follows by default. A null or
        /// empty saved order returns the default order.
        /// </summary>
        public static List<T> Apply<T>(IEnumerable<T> defaultOrder, Func<T, string> id, IReadOnlyList<string> saved)
        {
            var items = (defaultOrder ?? Enumerable.Empty<T>()).ToList();
            if (saved == null || saved.Count == 0 || items.Count < 2)
            {
                return items;
            }

            var ranks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < saved.Count; i++)
            {
                if (!string.IsNullOrWhiteSpace(saved[i]) && !ranks.ContainsKey(saved[i]))
                {
                    ranks[saved[i]] = i;
                }
            }

            var keyed = new List<KeyValuePair<long, T>>(items.Count);
            var rank = -1;
            var step = 0;
            foreach (var item in items)
            {
                var itemId = id?.Invoke(item);
                if (!string.IsNullOrWhiteSpace(itemId) && ranks.TryGetValue(itemId, out var savedRank))
                {
                    rank = savedRank;
                    step = 0;
                }
                else
                {
                    step++;
                }

                keyed.Add(new KeyValuePair<long, T>(((long)rank << 20) + step, item));
            }

            // OrderBy is stable, so items sharing a key keep their default order.
            return keyed.OrderBy(pair => pair.Key).Select(pair => pair.Value).ToList();
        }

        /// <summary>
        /// The id list after moving <paramref name="id"/> one place earlier (-1) or later (+1)
        /// among the ids <paramref name="sameGroup"/> accepts, which is every id when it is null.
        /// Returns the whole list, so a saved order names every item that existed when it was
        /// written; null when the item cannot move that way.
        /// </summary>
        public static List<string> Move(
            IReadOnlyList<string> ids,
            string id,
            int direction,
            Func<string, bool> sameGroup = null)
        {
            var list = (ids ?? Array.Empty<string>()).ToList();
            var index = list.FindIndex(item => string.Equals(item, id, StringComparison.OrdinalIgnoreCase));
            if (index < 0 || direction == 0)
            {
                return null;
            }

            var step = direction < 0 ? -1 : 1;
            var target = index + step;
            while (target >= 0 && target < list.Count && sameGroup != null && !sameGroup(list[target]))
            {
                target += step;
            }

            if (target < 0 || target >= list.Count)
            {
                return null;
            }

            var moved = list[index];
            list.RemoveAt(index);
            list.Insert(target, moved);
            return list;
        }

        /// <summary>
        /// Splits a bar's shown items between the bar and its Filters panel and orders both. The
        /// bar's default order is its items, then Sort By, then <paramref name="filtersButton"/>
        /// when the panel holds anything; <paramref name="collapsesFilters"/> off keeps every item
        /// on the bar and leaves the button out.
        /// </summary>
        public static void Arrange(
            IEnumerable<GridControlBarItem> shown,
            GridControlBarItem filtersButton,
            bool collapsesFilters,
            IReadOnlyList<string> saved,
            out List<GridControlBarItem> bar,
            out List<GridControlBarItem> panel)
        {
            var items = (shown ?? Enumerable.Empty<GridControlBarItem>()).Where(item => item != null).ToList();
            var inPanel = items.Where(item => collapsesFilters && item.IsPanelFilter).ToList();
            var onBar = items.Where(item => !(collapsesFilters && item.IsPanelFilter)).ToList();
            var ordered = new List<GridControlBarItem>();
            ordered.AddRange(onBar.Where(item => !(item is GridSortSelector)));
            ordered.AddRange(onBar.OfType<GridSortSelector>());
            if (inPanel.Count > 0 && filtersButton != null)
            {
                ordered.Add(filtersButton);
            }

            bar = Apply(ordered, item => item.Id, saved);
            panel = Apply(inPanel, item => item.Id, saved);
        }
    }
}
