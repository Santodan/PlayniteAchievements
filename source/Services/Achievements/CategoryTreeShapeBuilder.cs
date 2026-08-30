using System;
using System.Collections.Generic;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.Services.Achievements
{
    /// <summary>
    /// Turns a pre-ordered run of category paths into the per-row connector geometry that draws
    /// them as a tree.
    ///
    /// Everything is resolved against the emitted rows rather than the full label set, because the
    /// list is what the user sees: a node that produced no row must not leave a lane hanging, and
    /// the last row of a sibling run has to close its elbow even when deeper labels exist in the
    /// data behind it.
    /// </summary>
    internal static class CategoryTreeShapeBuilder
    {
        private static readonly bool[] NoLanes = new bool[0];

        /// <summary>
        /// Builds one shape per path, positionally. Input must be pre-order (each node immediately
        /// followed by its own subtree), which is what
        /// <see cref="AchievementCategoryFilterOrderHelper.BuildOrderedCategoryTree"/> emits.
        ///
        /// A manually sorted list is no longer pre-order and must not be passed here - the lanes
        /// would connect rows that are not related. Callers drop the guide entirely in that state.
        /// </summary>
        public static IReadOnlyList<CategoryTreeShape> Build(IReadOnlyList<string> orderedPaths)
        {
            if (orderedPaths == null || orderedPaths.Count == 0)
            {
                return new CategoryTreeShape[0];
            }

            var count = orderedPaths.Count;
            var depths = new int[count];
            var indexByPath = new Dictionary<string, int>(count, StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < count; i++)
            {
                depths[i] = CategoryPathHelper.GetDepth(orderedPaths[i]);

                // First writer wins: a duplicate label cannot be two nodes, and the earlier row is
                // the one later rows were ordered under.
                var key = orderedPaths[i] ?? string.Empty;
                if (!indexByPath.ContainsKey(key))
                {
                    indexByPath[key] = i;
                }
            }

            var isLastSibling = new bool[count];
            for (var i = 0; i < count; i++)
            {
                isLastSibling[i] = ResolveIsLastSibling(orderedPaths, depths, i);
            }

            var result = new CategoryTreeShape[count];
            for (var i = 0; i < count; i++)
            {
                var hasChildren =
                    i + 1 < count &&
                    depths[i + 1] == depths[i] + 1 &&
                    CategoryPathHelper.IsDescendantOf(orderedPaths[i + 1], orderedPaths[i]);

                result[i] = new CategoryTreeShape(
                    depths[i],
                    isLastSibling[i],
                    hasChildren,
                    BuildAncestorLanes(orderedPaths[i], depths[i], indexByPath, isLastSibling));
            }

            return result;
        }

        /// <summary>
        /// True when nothing later in the list shares this row's parent. Scans forward to the first
        /// row at or above this row's depth: everything skipped in between is this row's own
        /// subtree, so that first row either is the next sibling or closes the run.
        /// </summary>
        private static bool ResolveIsLastSibling(IReadOnlyList<string> paths, int[] depths, int index)
        {
            var depth = depths[index];
            for (var j = index + 1; j < paths.Count; j++)
            {
                if (depths[j] > depth)
                {
                    continue;
                }

                return depths[j] != depth ||
                    !CategoryPathHelper.IsSame(
                        CategoryPathHelper.GetParentPath(paths[j]),
                        CategoryPathHelper.GetParentPath(paths[index]));
            }

            return true;
        }

        /// <summary>
        /// Which shallower lanes still carry a line through this row, outermost first. Lane i + 1
        /// belongs to the ancestor at depth i + 2, and stays open exactly while that ancestor has a
        /// following sibling of its own.
        ///
        /// An ancestor missing from the emitted rows leaves its lane closed rather than assuming a
        /// line: a lane drawn past a row that is not there reads as a connection to whatever
        /// happens to sit at that depth instead.
        /// </summary>
        private static bool[] BuildAncestorLanes(
            string path,
            int depth,
            Dictionary<string, int> indexByPath,
            bool[] isLastSibling)
        {
            if (depth <= 2)
            {
                return NoLanes;
            }

            // Root first, so the ancestor at depth d sits at index d - 1.
            var ancestors = CategoryPathHelper.EnumerateAncestors(path);
            var lanes = new bool[depth - 2];
            for (var lane = 0; lane < lanes.Length; lane++)
            {
                var ancestorIndex = lane + 1;
                lanes[lane] = ancestorIndex < ancestors.Count &&
                    indexByPath.TryGetValue(ancestors[ancestorIndex] ?? string.Empty, out var rowIndex) &&
                    !isLastSibling[rowIndex];
            }

            return lanes;
        }
    }
}
