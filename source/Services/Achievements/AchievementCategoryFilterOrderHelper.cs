using System;
using System.Collections.Generic;

namespace PlayniteAchievements.Services.Achievements
{
    internal static class AchievementCategoryFilterOrderHelper
    {
        public static List<string> BuildOrderedCategoryLabels<T>(
            IEnumerable<T> source,
            Func<T, string> categorySelector,
            IEnumerable<string> preferredOrder = null)
        {
            var ordered = new List<string>();
            if (source == null || categorySelector == null)
            {
                return ordered;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var sourceLabels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in source)
            {
                var normalized = AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(categorySelector(item));
                if (!string.IsNullOrWhiteSpace(normalized) && !sourceLabels.ContainsKey(normalized))
                {
                    sourceLabels[normalized] = normalized;
                }
            }

            foreach (var label in preferredOrder ?? Array.Empty<string>())
            {
                var normalized = AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(label);
                if (string.IsNullOrWhiteSpace(normalized) ||
                    !sourceLabels.TryGetValue(normalized, out var sourceLabel) ||
                    !seen.Add(normalized))
                {
                    continue;
                }

                ordered.Add(sourceLabel);
            }

            foreach (var item in source)
            {
                var normalized = AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(categorySelector(item));
                if (string.IsNullOrWhiteSpace(normalized) || !seen.Add(normalized))
                {
                    continue;
                }

                ordered.Add(normalized);
            }

            return ordered;
        }

        /// <summary>
        /// Index of a normalized category label in the game's custom category order, or
        /// <see cref="int.MaxValue"/> when there is no custom order or the label is absent.
        /// </summary>
        public static int ResolveCategoryOrderIndex(string categoryLabel, IReadOnlyList<string> categoryOrder)
        {
            if (string.IsNullOrWhiteSpace(categoryLabel) || categoryOrder == null || categoryOrder.Count == 0)
            {
                return int.MaxValue;
            }

            for (var i = 0; i < categoryOrder.Count; i++)
            {
                if (string.Equals(
                    AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(categoryOrder[i]),
                    categoryLabel,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            return int.MaxValue;
        }
    }
}
