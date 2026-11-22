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

            var seen = new HashSet<string>(CategoryPathHelper.LabelComparer);
            var sourceLabels = new Dictionary<string, string>(CategoryPathHelper.LabelComparer);
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
        /// A game's category order as a normalized lookup, so a caller resolving many
        /// achievements against one game's order normalizes each entry once instead of once per
        /// probe. First position wins, matching the scan it replaces.
        /// </summary>
        public static Dictionary<string, int> BuildCategoryOrderIndex(IReadOnlyList<string> categoryOrder)
        {
            var map = new Dictionary<string, int>(CategoryPathHelper.LabelComparer);
            if (categoryOrder == null)
            {
                return map;
            }

            for (var i = 0; i < categoryOrder.Count; i++)
            {
                var normalized = CategoryPathHelper.NormalizePath(categoryOrder[i]);
                if (!string.IsNullOrWhiteSpace(normalized) && !map.ContainsKey(normalized))
                {
                    map[normalized] = i;
                }
            }

            return map;
        }

        /// <summary>
        /// Index of a normalized category label in a prebuilt
        /// <see cref="BuildCategoryOrderIndex"/> lookup, or <see cref="int.MaxValue"/> when the
        /// label is absent.
        /// </summary>
        public static int ResolveCategoryOrderIndex(
            string categoryLabel,
            IReadOnlyDictionary<string, int> categoryOrderIndex)
        {
            if (string.IsNullOrWhiteSpace(categoryLabel) ||
                categoryOrderIndex == null ||
                categoryOrderIndex.Count == 0)
            {
                return int.MaxValue;
            }

            return categoryOrderIndex.TryGetValue(categoryLabel, out var index) ? index : int.MaxValue;
        }

        /// <summary>
        /// Index of a normalized category label in the game's custom category order, or
        /// <see cref="int.MaxValue"/> when there is no custom order or the label is absent.
        /// </summary>
        /// <remarks>
        /// Scans and normalizes as it goes. Resolving a whole game's achievements against one
        /// order list should build the lookup above once instead of calling this per achievement.
        /// </remarks>
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
                    CategoryPathHelper.LabelComparison))
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

            var tree = new CategoryTree(ExpandToNodes(labels), preferredOrder);
            tree.AppendSubtree(result, null);
            return result;
        }

        /// <summary>
        /// The immediate children of <paramref name="parentPath"/>, in the order they should
        /// render. A null or blank parent gives the roots. This is one level of
        /// <see cref="BuildOrderedCategoryTree"/>, for surfaces that show a single level at a time.
        /// </summary>
        public static List<string> BuildOrderedCategoryLevel(
            IEnumerable<string> labels,
            string parentPath,
            IReadOnlyList<string> preferredOrder)
        {
            if (labels == null)
            {
                return new List<string>();
            }

            return new CategoryTree(ExpandToNodes(labels), preferredOrder).Level(parentPath);
        }

        /// <summary>
        /// Every distinct label plus every ancestor of one, in first-seen order, so a node that
        /// holds no achievements of its own is still part of the tree.
        /// </summary>
        private static List<string> ExpandToNodes(IEnumerable<string> labels)
        {
            var nodes = new List<string>();
            var seen = new HashSet<string>(CategoryPathHelper.LabelComparer);
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

            return nodes;
        }

        /// <summary>
        /// The nodes indexed once: each parent's children in first-seen order, and each node's
        /// subtree order index. Asking <see cref="CategoryPathHelper.GetChildPaths"/> and
        /// <see cref="ResolveCategoryOrderIndexForSubtree"/> per node scanned every node for every
        /// node, which the library editor's thousands of categories turned into a hang.
        /// </summary>
        private sealed class CategoryTree
        {
            private readonly List<string> _roots = new List<string>();
            private readonly HashSet<string> _rootSet = new HashSet<string>(CategoryPathHelper.LabelComparer);
            private readonly Dictionary<string, List<string>> _children =
                new Dictionary<string, List<string>>(CategoryPathHelper.LabelComparer);
            private readonly Dictionary<string, HashSet<string>> _childSets =
                new Dictionary<string, HashSet<string>>(CategoryPathHelper.LabelComparer);
            private readonly Dictionary<string, int> _subtreeOrder =
                new Dictionary<string, int>(CategoryPathHelper.LabelComparer);

            public CategoryTree(IReadOnlyList<string> nodes, IReadOnlyList<string> preferredOrder)
            {
                var orderIndex = preferredOrder != null && preferredOrder.Count > 0
                    ? BuildCategoryOrderIndex(preferredOrder)
                    : null;

                foreach (var node in nodes)
                {
                    var segments = CategoryPathHelper.Split(node);
                    string parent = null;
                    for (var depth = 1; depth <= segments.Count; depth++)
                    {
                        var path = string.Join(CategoryPathHelper.Separator, segments.Take(depth));
                        AddChild(parent, path);
                        parent = path;
                    }

                    // A node's own position counts for itself and for every ancestor above it.
                    if (orderIndex != null &&
                        orderIndex.TryGetValue(CategoryPathHelper.NormalizePath(node), out var index))
                    {
                        for (var depth = 1; depth <= segments.Count; depth++)
                        {
                            var path = string.Join(CategoryPathHelper.Separator, segments.Take(depth));
                            if (!_subtreeOrder.TryGetValue(path, out var best) || index < best)
                            {
                                _subtreeOrder[path] = index;
                            }
                        }
                    }
                }
            }

            /// <summary>The children of <paramref name="parentPath"/> in render order; the roots for none.</summary>
            public List<string> Level(string parentPath)
            {
                List<string> children;
                if (string.IsNullOrWhiteSpace(parentPath))
                {
                    children = _roots;
                }
                else if (!_children.TryGetValue(
                             string.Join(CategoryPathHelper.Separator, CategoryPathHelper.Split(parentPath)),
                             out children))
                {
                    return new List<string>();
                }

                return children
                    .Select((path, firstSeen) => new { Path = path, FirstSeen = firstSeen })
                    .OrderBy(entry => _subtreeOrder.TryGetValue(entry.Path, out var index) ? index : int.MaxValue)
                    .ThenBy(entry => entry.FirstSeen)
                    .Select(entry => entry.Path)
                    .ToList();
            }

            public void AppendSubtree(List<string> result, string parentPath)
            {
                foreach (var path in Level(parentPath))
                {
                    result.Add(path);
                    AppendSubtree(result, path);
                }
            }

            private void AddChild(string parent, string path)
            {
                if (parent == null)
                {
                    if (_rootSet.Add(path))
                    {
                        _roots.Add(path);
                    }

                    return;
                }

                if (!_childSets.TryGetValue(parent, out var set))
                {
                    set = new HashSet<string>(CategoryPathHelper.LabelComparer);
                    _childSets[parent] = set;
                    _children[parent] = new List<string>();
                }

                if (set.Add(path))
                {
                    _children[parent].Add(path);
                }
            }
        }
    }
}
