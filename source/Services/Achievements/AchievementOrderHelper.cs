using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Services.Achievements
{
    /// <summary>
    /// Shared helpers for applying and editing per-game achievement order.
    /// </summary>
    public static class AchievementOrderHelper
    {
        /// <summary>
        /// Normalizes API names by trimming, removing empty entries, and de-duplicating
        /// case-insensitively while preserving first-seen order.
        /// </summary>
        public static List<string> NormalizeApiNames(IEnumerable<string> apiNames)
        {
            var result = new List<string>();
            if (apiNames == null)
            {
                return result;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var value in apiNames)
            {
                var normalized = (value ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(normalized))
                {
                    continue;
                }

                if (seen.Add(normalized))
                {
                    result.Add(normalized);
                }
            }

            return result;
        }

        /// <summary>
        /// Re-seats reverted achievements at the position the provider gave them, leaving the rest
        /// of the stored order untouched. Returns null when the result is the provider's own order,
        /// so the caller can drop the override rather than store a list that changes nothing.
        /// </summary>
        /// <remarks>
        /// The order is one positional list for the whole game, so an achievement cannot simply be
        /// dropped from it: an absent entry sorts to the end, which is not what reverting one row
        /// means. Each reverted entry is therefore placed before the first remaining entry the
        /// provider ranked after it.
        /// </remarks>
        /// <param name="current">
        /// The achievements in their current stored order, each paired with the provider's own
        /// index for it.
        /// </param>
        /// <param name="restoredApiNames">The achievements being returned to the provider's order.</param>
        public static List<string> RestoreDefaultPositions(
            IReadOnlyList<KeyValuePair<string, int>> current,
            IEnumerable<string> restoredApiNames)
        {
            if (current == null || current.Count == 0)
            {
                return null;
            }

            var restored = new HashSet<string>(
                NormalizeApiNames(restoredApiNames),
                StringComparer.OrdinalIgnoreCase);
            var remaining = new List<KeyValuePair<string, int>>();
            var moving = new List<KeyValuePair<string, int>>();
            foreach (var entry in current)
            {
                var apiName = (entry.Key ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(apiName))
                {
                    continue;
                }

                if (restored.Contains(apiName))
                {
                    moving.Add(new KeyValuePair<string, int>(apiName, entry.Value));
                }
                else
                {
                    remaining.Add(new KeyValuePair<string, int>(apiName, entry.Value));
                }
            }

            foreach (var entry in moving.OrderBy(item => item.Value))
            {
                var index = remaining.FindIndex(other => other.Value > entry.Value);
                if (index < 0)
                {
                    remaining.Add(entry);
                }
                else
                {
                    remaining.Insert(index, entry);
                }
            }

            var isDefaultOrder = true;
            for (var i = 1; i < remaining.Count && isDefaultOrder; i++)
            {
                isDefaultOrder = remaining[i - 1].Value <= remaining[i].Value;
            }

            return isDefaultOrder ? null : remaining.Select(entry => entry.Key).ToList();
        }

        /// <summary>
        /// Applies a saved order to a source list and appends unmatched items at the end.
        /// </summary>
        public static List<T> ApplyOrder<T>(
            IEnumerable<T> source,
            Func<T, string> apiNameSelector,
            IReadOnlyList<string> orderedApiNames)
        {
            var items = source?.ToList() ?? new List<T>();
            if (items.Count == 0 || apiNameSelector == null)
            {
                return items;
            }

            var normalizedOrder = NormalizeApiNames(orderedApiNames);
            if (normalizedOrder.Count == 0)
            {
                return items;
            }

            var rankMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < normalizedOrder.Count; i++)
            {
                if (!rankMap.ContainsKey(normalizedOrder[i]))
                {
                    rankMap[normalizedOrder[i]] = i;
                }
            }

            var matched = new List<Tuple<T, int, int>>();
            var unmatched = new List<T>();

            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                var apiName = (apiNameSelector(item) ?? string.Empty).Trim();
                if (!string.IsNullOrWhiteSpace(apiName) &&
                    rankMap.TryGetValue(apiName, out var rank))
                {
                    matched.Add(Tuple.Create(item, rank, i));
                }
                else
                {
                    unmatched.Add(item);
                }
            }

            matched.Sort((a, b) =>
            {
                var rankCompare = a.Item2.CompareTo(b.Item2);
                if (rankCompare != 0)
                {
                    return rankCompare;
                }

                return a.Item3.CompareTo(b.Item3);
            });

            var ordered = new List<T>(items.Count);
            ordered.AddRange(matched.Select(x => x.Item1));
            ordered.AddRange(unmatched);
            return ordered;
        }

        /// <summary>
        /// Reorders a selected block of indexes around a drop target.
        /// </summary>
        public static bool TryReorder<T>(
            IReadOnlyList<T> source,
            IReadOnlyList<int> selectedIndexes,
            int targetIndex,
            bool insertAfterTarget,
            out List<T> reordered)
        {
            reordered = source?.ToList() ?? new List<T>();
            if (source == null || source.Count == 0 || selectedIndexes == null || selectedIndexes.Count == 0)
            {
                return false;
            }

            var normalizedIndexes = selectedIndexes
                .Where(i => i >= 0 && i < source.Count)
                .Distinct()
                .OrderBy(i => i)
                .ToList();

            if (normalizedIndexes.Count == 0)
            {
                return false;
            }

            if (targetIndex < 0)
            {
                targetIndex = 0;
            }
            else if (targetIndex >= source.Count)
            {
                targetIndex = source.Count - 1;
            }

            var movingItems = normalizedIndexes.Select(i => source[i]).ToList();
            var selectedSet = new HashSet<int>(normalizedIndexes);
            var remaining = new List<T>(source.Count - normalizedIndexes.Count);
            for (var i = 0; i < source.Count; i++)
            {
                if (!selectedSet.Contains(i))
                {
                    remaining.Add(source[i]);
                }
            }

            var insertionIndex = insertAfterTarget ? targetIndex + 1 : targetIndex;
            var removedBeforeInsertion = normalizedIndexes.Count(i => i < insertionIndex);
            insertionIndex -= removedBeforeInsertion;

            if (insertionIndex < 0)
            {
                insertionIndex = 0;
            }
            else if (insertionIndex > remaining.Count)
            {
                insertionIndex = remaining.Count;
            }

            remaining.InsertRange(insertionIndex, movingItems);
            reordered = remaining;

            if (reordered.Count != source.Count)
            {
                return true;
            }

            for (var i = 0; i < source.Count; i++)
            {
                if (!Equals(source[i], reordered[i]))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
