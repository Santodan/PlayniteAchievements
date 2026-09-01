using System.Collections.Generic;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.Services.Achievements
{
    /// <summary>
    /// Drops the rows hidden by collapsed categories from a pre-order category run.
    ///
    /// The collapsed row itself stays - it carries the "+" toggle that re-expands the subtree - and
    /// every descendant under it goes, including nested collapsed nodes, whose own keys become
    /// inert while an ancestor hides them. Keys naming paths not present in the run are inert too,
    /// so a stale set never needs pruning.
    /// </summary>
    internal static class CategoryCollapseFilter
    {
        /// <summary>
        /// Rows must be the builder's pre-order run (never a column-sorted list): the single-pass
        /// skip anchor relies on a collapsed node being immediately followed by its own subtree.
        /// </summary>
        public static List<GameSummaryItem> Apply(
            IReadOnlyList<GameSummaryItem> rows,
            ISet<string> collapsedPaths,
            out bool removedAny)
        {
            removedAny = false;
            var result = new List<GameSummaryItem>(rows.Count);
            string skipAnchor = null;
            foreach (var row in rows)
            {
                var category = row as CategorySummaryItem;
                if (category == null)
                {
                    result.Add(row);
                    continue;
                }

                if (skipAnchor != null &&
                    CategoryPathHelper.IsSelfOrDescendantOf(category.CategoryPath, skipAnchor))
                {
                    // The collapsed row itself was kept before its anchor was set, so IsSame never
                    // removes it.
                    removedAny = true;
                    continue;
                }

                skipAnchor = null;
                result.Add(category);
                if (collapsedPaths.Contains(category.CategoryPath ?? string.Empty))
                {
                    skipAnchor = category.CategoryPath;
                }
            }

            return result;
        }
    }
}
