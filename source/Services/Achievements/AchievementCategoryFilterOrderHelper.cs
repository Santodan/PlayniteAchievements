using System;
using System.Collections.Generic;
using System.Linq;

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
                var normalized = CategoryPathHelper.NormalizePath(categorySelector(item));
                if (!string.IsNullOrWhiteSpace(normalized) && !sourceLabels.ContainsKey(normalized))
                {
                    sourceLabels[normalized] = normalized;
                }
            }

            foreach (var label in preferredOrder ?? Array.Empty<string>())
            {
                var normalized = CategoryPathHelper.NormalizePath(label);
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
                var normalized = CategoryPathHelper.NormalizePath(categorySelector(item));
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
                    CategoryPathHelper.NormalizePath(categoryOrder[i]),
                    categoryLabel,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            return int.MaxValue;
        }

        /// <summary>
        /// Order index of a node, taken as the earliest index of the node itself or anything beneath
        /// it. This is what gives a parent its position from its first ordered child, so the stored
        /// order can stay a flat list of fully-qualified paths with no notion of nesting.
        /// <see cref="int.MaxValue"/> when nothing in the subtree is ordered.
        /// </summary>
        public static int ResolveCategoryOrderIndexForSubtree(
            string nodePath,
            IEnumerable<string> allLabels,
            IReadOnlyList<string> categoryOrder)
        {
            var normalizedNode = CategoryPathHelper.NormalizePath(nodePath);
            var best = ResolveCategoryOrderIndex(normalizedNode, categoryOrder);
            if (allLabels == null || best == 0)
            {
                return best;
            }

            foreach (var label in allLabels)
            {
                if (!CategoryPathHelper.IsDescendantOf(label, normalizedNode))
                {
                    continue;
                }

                var index = ResolveCategoryOrderIndex(CategoryPathHelper.NormalizePath(label), categoryOrder);
                if (index < best)
                {
                    best = index;
                }
            }

            return best;
        }

        /// <summary>
        /// The labels arranged as a tree in render order: siblings in resolved order, each node
        /// immediately followed by its own subtree. This is what keeps siblings contiguous and a
        /// subtree unsplittable, so the persisted order list need not itself be well-formed - an
        /// interleaved or hand-edited list still renders correctly and is written back tidy.
        ///
        /// Ancestors of a present label are included even when they hold no achievements of their
        /// own, so an intermediate node always has a row.
        /// </summary>
        public static List<string> BuildOrderedCategoryTree(
            IEnumerable<string> labels,
            IReadOnlyList<string> preferredOrder)
        {
            var result = new List<string>();
            if (labels == null)
            {
                return result;
            }

            var nodes = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var label in labels)
            {
                foreach (var node in CategoryPathHelper.EnumerateSelfAndAncestors(label))
                {
                    if (seen.Add(node))
                    {
                        nodes.Add(node);
                    }
                }
            }

            AppendCategoryTreeLevel(result, nodes, null, preferredOrder);
            return result;
        }

        private static void AppendCategoryTreeLevel(
            List<string> result,
            List<string> nodes,
            string parentPath,
            IReadOnlyList<string> preferredOrder)
        {
            var ordered = CategoryPathHelper.GetChildPaths(nodes, parentPath)
                .Select((path, firstSeen) => new { Path = path, FirstSeen = firstSeen })
                .OrderBy(entry => ResolveCategoryOrderIndexForSubtree(entry.Path, nodes, preferredOrder))
                .ThenBy(entry => entry.FirstSeen);

            foreach (var entry in ordered)
            {
                result.Add(entry.Path);
                AppendCategoryTreeLevel(result, nodes, entry.Path, preferredOrder);
            }
        }
    }
}
