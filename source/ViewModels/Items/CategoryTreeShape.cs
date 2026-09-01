using System;
using System.Collections.Generic;

namespace PlayniteAchievements.ViewModels.Items
{
    /// <summary>
    /// The connector geometry one category row needs to draw itself as part of a tree: how deep it
    /// sits, whether it closes its sibling run, whether it opens one of its own, and which shallower
    /// lanes still have a line passing through this row.
    ///
    /// Derived from the emitted row order rather than from the path alone: whether a lane continues
    /// past a row depends on what comes after it in the list, so this cannot be answered by looking
    /// at one label. Built by <see cref="PlayniteAchievements.Services.Achievements.CategoryTreeShapeBuilder"/>.
    /// </summary>
    public sealed class CategoryTreeShape
    {
        private static readonly bool[] NoLanes = new bool[0];

        public CategoryTreeShape(
            int depth,
            bool isLastSibling,
            bool hasChildren,
            bool[] ancestorContinues,
            bool isSelfRow = false)
        {
            Depth = depth;
            IsLastSibling = isLastSibling;
            HasChildren = hasChildren;
            AncestorContinues = ancestorContinues ?? NoLanes;
            IsSelfRow = isSelfRow;
        }

        /// <summary>
        /// True for a mixed category's synthesized self row. The guide then draws only the lines
        /// passing through toward the rows below - no arm and no bead - so the row reads as an
        /// annotation on its category rather than a node of the tree.
        /// </summary>
        public bool IsSelfRow { get; }

        /// <summary>One-based: a root category is 1.</summary>
        public int Depth { get; }

        /// <summary>
        /// True when no later row shares this row's parent, which is what picks the closing elbow
        /// over the tee and stops the stem at the row's middle instead of carrying it to the bottom.
        /// </summary>
        public bool IsLastSibling { get; }

        /// <summary>
        /// True when the next row is this row's child. Drives the descender into the lane below and
        /// the filled junction dot, so a folder reads differently from a leaf at a glance.
        /// </summary>
        public bool HasChildren { get; }

        /// <summary>
        /// One entry per lane strictly shallower than this row's own stem, ordered outermost first:
        /// index i answers whether the ancestor at depth i + 2 has a following sibling, and so
        /// whether lane i + 1 draws a full-height line through this row. Empty for depth 1 and 2,
        /// which have no lane above their own.
        /// </summary>
        public IReadOnlyList<bool> AncestorContinues { get; }
    }
}
