using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Services.Achievements
{
    /// <summary>The order and post-move selection a gap drop produces.</summary>
    internal sealed class CategoryGapPlan
    {
        public List<string> Order { get; set; }

        public List<string> SelectionRoots { get; set; }
    }

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

            // Keyed case-insensitively but valued with the snapshot's own text, so a
            // differently-cased caller input cannot rewrite a label's casing as a side effect.
            var snapshot = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var label in orderedLabels)
            {
                if (string.IsNullOrWhiteSpace(label))
                {
                    continue;
                }

                var normalized = CategoryPathHelper.NormalizePath(label);
                if (!snapshot.ContainsKey(normalized))
                {
                    snapshot[normalized] = normalized;
                }
            }

            // Default never moves (it is the fallback bucket), and a label the grid is not
            // showing cannot be trusted as a move source.
            var moving = movingLabels
                .Where(label => !string.IsNullOrWhiteSpace(label))
                .Select(CategoryPathHelper.NormalizePath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(label =>
                    !string.Equals(label, AchievementCategoryTypeHelper.DefaultCategoryLabel, StringComparison.OrdinalIgnoreCase) &&
                    snapshot.ContainsKey(label))
                .Select(label => snapshot[label])
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
                    !snapshot.TryGetValue(target, out target) ||
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

                if (snapshot.ContainsKey(reparented) || !plannedResults.Add(reparented))
                {
                    continue;
                }

                moves.Add(new KeyValuePair<string, string>(label, reparented));
            }

            return moves;
        }

        /// <summary>
        /// The rendered order after dropping <paramref name="movingLabels"/> into the gap above
        /// <paramref name="gapBeforeLabel"/> (null or blank = end of list), with
        /// <paramref name="moves"/> (from <see cref="PlanNestMoves"/>) already applied to every
        /// label: each dragged subtree is lifted out as a contiguous block and spliced in at the
        /// gap, so the order and the reparent land in one write. SelectionRoots are the dragged
        /// roots under their post-move labels, for restoring selection.
        /// </summary>
        public static CategoryGapPlan PlanGapOrder(
            IReadOnlyList<string> renderedLabels,
            IReadOnlyList<string> movingLabels,
            IReadOnlyList<KeyValuePair<string, string>> moves,
            string gapBeforeLabel)
        {
            var rendered = (renderedLabels ?? Array.Empty<string>())
                .Where(label => !string.IsNullOrWhiteSpace(label))
                .Select(CategoryPathHelper.NormalizePath)
                .ToList();
            var renderedSet = new HashSet<string>(rendered, StringComparer.OrdinalIgnoreCase);

            // Same moving-set derivation as PlanNestMoves, then rendered order so multi-drag
            // blocks land in the order the grid showed them.
            var roots = (movingLabels ?? Array.Empty<string>())
                .Where(label => !string.IsNullOrWhiteSpace(label))
                .Select(CategoryPathHelper.NormalizePath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(label =>
                    !string.Equals(label, AchievementCategoryTypeHelper.DefaultCategoryLabel, StringComparison.OrdinalIgnoreCase) &&
                    renderedSet.Contains(label))
                .ToList();
            roots = roots
                .Where(label => !roots.Any(other => CategoryPathHelper.IsDescendantOf(label, other)))
                .ToList();
            roots = rendered
                .Where(label => roots.Any(root => CategoryPathHelper.IsSame(root, label)))
                .ToList();

            // Move keys are disjoint original paths and no value collides with another key (the
            // collision guard), so folding every rewrite over a label applies at most one.
            string MapLabel(string label)
            {
                foreach (var move in moves ?? Array.Empty<KeyValuePair<string, string>>())
                {
                    label = CategoryPathHelper.RewritePrefix(label, move.Key, move.Value);
                }

                return label;
            }

            var mappedRoots = roots.Select(MapLabel).ToList();
            var working = rendered.Select(MapLabel).ToList();
            var blocks = new List<List<string>>(mappedRoots.Count);
            foreach (var root in mappedRoots)
            {
                blocks.Add(working.Where(label => CategoryPathHelper.IsSelfOrDescendantOf(label, root)).ToList());
                working.RemoveAll(label => CategoryPathHelper.IsSelfOrDescendantOf(label, root));
            }

            var insertAt = working.Count;
            if (!string.IsNullOrWhiteSpace(gapBeforeLabel))
            {
                var anchor = CategoryPathHelper.NormalizePath(gapBeforeLabel);
                var anchorIndex = working.FindIndex(label => CategoryPathHelper.IsSame(label, anchor));
                if (anchorIndex >= 0)
                {
                    insertAt = anchorIndex;
                }
            }

            foreach (var block in blocks)
            {
                working.InsertRange(insertAt, block);
                insertAt += block.Count;
            }

            return new CategoryGapPlan
            {
                Order = working,
                SelectionRoots = mappedRoots
            };
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
