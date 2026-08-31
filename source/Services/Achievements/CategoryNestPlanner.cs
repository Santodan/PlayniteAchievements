using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Services.Achievements
{
    /// <summary>
    /// Plans the move batch for "make these categories subcategories of that one" (or top-level
    /// when the target is null). Pure over label lists so every gesture - context menu, drag,
    /// anything later - resolves against one snapshot and produces one
    /// ApplyCategoryMoves batch, and so the guards are unit-testable without the view model.
    /// </summary>
    internal static class CategoryNestPlanner
    {
        /// <summary>
        /// Source-to-target label moves for reparenting <paramref name="movingLabels"/> under
        /// <paramref name="targetParentLabel"/> (null or blank = top level), planned against one
        /// snapshot of the rendered labels. Empty when nothing can move.
        ///
        /// A label is skipped rather than folded when the move would push its subtree past
        /// <see cref="CategoryPathHelper.MaxDepth"/>: overflow folding rewrites label text, which
        /// silently merges distinct nodes. A label is also skipped when its reparented path would
        /// collide with an existing label or with another planned result, because the prefix
        /// rewrite would merge the two categories without the user asking for a merge.
        /// </summary>
        public static List<KeyValuePair<string, string>> PlanNestMoves(
            IReadOnlyList<string> orderedLabels,
            IReadOnlyList<string> movingLabels,
            string targetParentLabel)
        {
            var moves = new List<KeyValuePair<string, string>>();
            if (orderedLabels == null || orderedLabels.Count == 0 ||
                movingLabels == null || movingLabels.Count == 0)
            {
                return moves;
            }

            var snapshot = new HashSet<string>(
                orderedLabels
                    .Where(label => !string.IsNullOrWhiteSpace(label))
                    .Select(CategoryPathHelper.NormalizePath),
                StringComparer.OrdinalIgnoreCase);

            // Default never moves (it is the fallback bucket), and a label the grid is not
            // showing cannot be trusted as a move source.
            var moving = movingLabels
                .Where(label => !string.IsNullOrWhiteSpace(label))
                .Select(CategoryPathHelper.NormalizePath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(label =>
                    !string.Equals(label, AchievementCategoryTypeHelper.DefaultCategoryLabel, StringComparison.OrdinalIgnoreCase) &&
                    snapshot.Contains(label))
                .ToList();

            // A moving descendant of a moving ancestor is dropped: the ancestor's prefix
            // rewrite carries it. Same filter as the indent path.
            moving = moving
                .Where(label => !moving.Any(other => CategoryPathHelper.IsDescendantOf(label, other)))
                .OrderByDescending(CategoryPathHelper.GetDepth)
                .ToList();
            if (moving.Count == 0)
            {
                return moves;
            }

            string target = null;
            if (!string.IsNullOrWhiteSpace(targetParentLabel))
            {
                target = CategoryPathHelper.NormalizePath(targetParentLabel);

                // Nothing may nest under Default (it has no provider identity and refuses rename
                // and merge, so a child there would be unreachable), the target must be a rendered
                // row, and a node cannot become its own descendant.
                if (string.Equals(
                        CategoryPathHelper.Split(target)[0],
                        AchievementCategoryTypeHelper.DefaultCategoryLabel,
                        StringComparison.OrdinalIgnoreCase) ||
                    !snapshot.Contains(target) ||
                    moving.Any(label => CategoryPathHelper.IsSelfOrDescendantOf(target, label)))
                {
                    return moves;
                }
            }

            var targetDepth = target == null ? 0 : CategoryPathHelper.GetDepth(target);
            var plannedResults = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var label in moving)
            {
                var reparented = CategoryPathHelper.Reparent(label, target);
                if (CategoryPathHelper.IsSame(reparented, label))
                {
                    continue;
                }

                if (targetDepth + GetSubtreeHeight(orderedLabels, label) > CategoryPathHelper.MaxDepth)
                {
                    continue;
                }

                if (snapshot.Contains(reparented) || !plannedResults.Add(reparented))
                {
                    continue;
                }

                moves.Add(new KeyValuePair<string, string>(label, reparented));
            }

            return moves;
        }

        /// <summary>1 for a leaf; 1 plus the deepest descendant's distance otherwise.</summary>
        public static int GetSubtreeHeight(IReadOnlyList<string> orderedLabels, string label)
        {
            var normalized = CategoryPathHelper.NormalizePath(label);
            var depth = CategoryPathHelper.GetDepth(normalized);
            var height = 1;
            foreach (var candidate in orderedLabels ?? Array.Empty<string>())
            {
                if (!CategoryPathHelper.IsDescendantOf(candidate, normalized))
                {
                    continue;
                }

                var candidateHeight = CategoryPathHelper.GetDepth(candidate) - depth + 1;
                if (candidateHeight > height)
                {
                    height = candidateHeight;
                }
            }

            return height;
        }
    }
}
