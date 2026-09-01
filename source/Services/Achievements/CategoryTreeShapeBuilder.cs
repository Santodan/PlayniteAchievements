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

        // Synthetic leaf segment that places a self row one level under its own category for the
        // geometry pass. Internal to this builder: it exists only inside the paths array built in
        // Stamp and is never stored on a row or shown.
        private const string SelfRowMarkerSegment = "\u0001";

        /// <summary>
        /// Stamps the rows with the geometry that draws them as a tree, or clears it.
        ///
        /// Filtering the rows first is fine - the lanes are resolved against whatever is in the
        /// list, so a filtered subset still connects only the rows actually present. Sorting is not:
        /// a re-sorted list is no longer pre-order, so callers pass enabled: false there and the
        /// guide disappears rather than drawing lanes between unrelated rows.
        ///
        /// A list with no nesting left in it clears too, so a flat game pays nothing for a feature
        /// it has no use for. <paramref name="assumeNesting"/> skips that bail-out: a collapse
        /// filter can leave only depth-1 rows visible, and those rows must keep their shapes or the
        /// "+" toggles that re-expand them vanish with the guide.
        /// </summary>
        public static void Stamp(IReadOnlyList<GameSummaryItem> rows, bool enabled, bool assumeNesting = false)
        {
            if (rows == null || rows.Count == 0)
            {
                return;
            }

            var categories = new List<CategorySummaryItem>(rows.Count);
            foreach (var row in rows)
            {
                if (row is CategorySummaryItem category)
                {
                    categories.Add(category);
                }
            }

            var paths = new string[categories.Count];
            for (var i = 0; i < categories.Count; i++)
            {
                // A self row shares its category's path but sits one level below it as its first
                // child. A synthetic leaf segment gives it exactly that position, and every
                // positional rule here then applies verbatim: the parent's junction opens for it,
                // its own elbow reads as a tee because the child categories that made the node
                // mixed follow as its siblings, and ancestor lanes resolve through the real rows.
                // The marker never renders and cannot collide with a user segment - segments are
                // trimmed of whitespace and this is a control character.
                paths[i] = categories[i].IsSelfRow
                    ? categories[i].CategoryPath + CategoryPathHelper.Separator + SelfRowMarkerSegment
                    : categories[i].CategoryPath;
            }

            if (!enabled || (!assumeNesting && !HasNesting(paths)) || categories.Count != rows.Count)
            {
                foreach (var row in rows)
                {
                    row.TreeShape = null;
                }

                return;
            }

            var shapes = Build(paths);
            for (var i = 0; i < categories.Count; i++)
            {
                // Resolved against the emitted rows like everything else here: a self row dropped
                // by a filter leaves its category without the flag, so nothing dangles toward a
                // row that is not there.
                var hasSelfRowBelow =
                    !categories[i].IsSelfRow &&
                    i + 1 < categories.Count &&
                    categories[i + 1].IsSelfRow &&
                    CategoryPathHelper.IsSame(categories[i + 1].CategoryPath, categories[i].CategoryPath);

                categories[i].TreeShape = categories[i].IsSelfRow || hasSelfRowBelow
                    ? WithSelfFlags(shapes[i], categories[i].IsSelfRow, hasSelfRowBelow)
                    : shapes[i];
            }
        }

        /// <summary>
        /// Same geometry, marked with the self-row roles so the guide and the name cell draw them
        /// accordingly. Applied after <see cref="Build"/>, which only sees paths and stays reusable
        /// for runs that have no self rows in them.
        /// </summary>
        private static CategoryTreeShape WithSelfFlags(CategoryTreeShape shape, bool isSelfRow, bool hasSelfRowBelow)
        {
            var lanes = new bool[shape.AncestorContinues.Count];
            for (var i = 0; i < lanes.Length; i++)
            {
                lanes[i] = shape.AncestorContinues[i];
            }

            return new CategoryTreeShape(
                shape.Depth,
                shape.IsLastSibling,
                shape.HasChildren,
                lanes,
                isSelfRow,
                hasSelfRowBelow);
        }

        /// <summary>
        /// Whether any path in the run is nested. Surfaces gate the guide on this so a game with a
        /// flat category list pays nothing for it - no lane, no gutter, no reserved width.
        /// </summary>
        public static bool HasNesting(IReadOnlyList<string> paths)
        {
            if (paths == null)
            {
                return false;
            }

            for (var i = 0; i < paths.Count; i++)
            {
                if (CategoryPathHelper.GetDepth(paths[i]) > 1)
                {
                    return true;
                }
            }

            return false;
        }

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
