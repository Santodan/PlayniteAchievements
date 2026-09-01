using System;

namespace PlayniteAchievements.Views.Helpers
{
    /// <summary>
    /// Horizontal geometry for the category tree guide.
    ///
    /// Lane spacing decays with depth and stops at a fixed total rather than stepping by a constant
    /// amount forever. A constant step is what made every earlier depth cue unaffordable here: the
    /// category grid disables horizontal scrolling, so an indent that grows without bound is taken
    /// straight out of the columns the user configured. With this table the guide costs
    /// <see cref="MaxGuideWidth"/> at depth 8 and never more, and adjacent levels stay separated by
    /// at least two pixels the whole way down.
    /// </summary>
    internal static class CategoryTreeGuideMetrics
    {
        /// <summary>
        /// Lane centre for depth 1..8, in device-independent pixels. Tunable as a unit: the only
        /// invariants the renderer depends on are that it is strictly increasing and that it covers
        /// <see cref="CategoryPathHelperMaxDepth"/> entries.
        /// </summary>
        private static readonly double[] LaneCentres = { 12d, 30d, 43d, 52d, 58d, 63d, 67d, 70d };

        /// <summary>Mirrors CategoryPathHelper.MaxDepth; deeper input clamps to the last lane.</summary>
        private const int CategoryPathHelperMaxDepth = 8;

        /// <summary>
        /// Gap between the centre of the junction bead and the row's text. Clears the bead's own
        /// radius as well as the space after it, so it is larger than it looks.
        /// </summary>
        public const double TextGap = 11d;

        /// <summary>Radius of the rounded corner on a closing elbow.</summary>
        public const double CornerRadius = 7d;

        /// <summary>Radius of the solid bead marking a node that has children.</summary>
        public const double NodeRadius = 4d;

        /// <summary>Radius of the outlined bead marking a leaf.</summary>
        public const double LeafNodeRadius = 3.5d;

        public static double MaxGuideWidth => LaneCentres[LaneCentres.Length - 1] + TextGap;

        /// <summary>
        /// Lane centre for a one-based depth. Depth 0 and below resolve to the first lane, and
        /// anything past the table clamps to the last, so an over-deep path folds into the deepest
        /// lane instead of running off the end of the column.
        /// </summary>
        public static double GetLaneCentre(int depth)
        {
            if (depth <= 1)
            {
                return LaneCentres[0];
            }

            var index = Math.Min(depth, CategoryPathHelperMaxDepth) - 1;
            return LaneCentres[index];
        }

        /// <summary>
        /// Total width the guide occupies for a row at this depth: out to its junction dot, plus the
        /// gap before the text. A row's content therefore starts further right the deeper it sits,
        /// which is the indent - the guide is what pays for it, not a margin on the text.
        /// </summary>
        public static double GetGuideWidth(int depth)
        {
            return GetLaneCentre(depth) + TextGap;
        }

        /// <summary>
        /// Radius of a row's bead, capped at half the gap to the lane it hangs off.
        ///
        /// Lane spacing decays to three pixels at the deepest levels, which is narrower than a bead,
        /// so a fixed radius would have deep beads overlapping - and hiding - the lane beside them.
        /// Tapering keeps every lane visible and reads as depth in its own right.
        /// </summary>
        public static double GetBeadRadius(int depth, bool hasChildren)
        {
            var preferred = hasChildren ? NodeRadius : LeafNodeRadius;
            if (depth <= 1)
            {
                return preferred;
            }

            var spacing = GetLaneCentre(depth) - GetLaneCentre(depth - 1);
            return Math.Max(MinimumBeadRadius, Math.Min(preferred, spacing / 2d));
        }

        /// <summary>Floor for <see cref="GetBeadRadius"/>; below this a bead stops reading as one.</summary>
        public const double MinimumBeadRadius = 2d;

        /// <summary>Preferred radius of the circled expand/collapse toggle on a parent's descender.</summary>
        public const double ToggleRadius = 7d;

        /// <summary>Floor for <see cref="GetToggleRadius"/>; below this the +/- glyph stops reading.</summary>
        public const double MinimumToggleRadius = 4d;

        /// <summary>
        /// Radius of a row's collapse toggle, tapered the same way as <see cref="GetBeadRadius"/>:
        /// the toggle sits on the descender at the child lane, and deep lanes are only 3-5 pixels
        /// apart, so a fixed circle would swallow the neighbouring lane.
        /// </summary>
        public static double GetToggleRadius(int depth)
        {
            if (depth <= 1)
            {
                return ToggleRadius;
            }

            var spacing = GetLaneCentre(depth) - GetLaneCentre(depth - 1);
            return Math.Max(MinimumToggleRadius, Math.Min(ToggleRadius, spacing / 2d));
        }

        /// <summary>
        /// Radius of the boundary toggle - the +/- glyph centred on the border between a
        /// toggle-bearing row and the row beneath it. Shared by both rows so the row above (which
        /// stops its descender at the glyph's top) and the row below (which draws the glyph and
        /// gaps its own stem under it) agree on the same circle without seeing each other. On a
        /// short row it shrinks so the glyph's upper half stays clear of the junction bead, and
        /// below a 3px floor it is skipped entirely (returns 0) - children stay reachable through
        /// Expand All.
        /// </summary>
        public static double GetBoundaryToggleRadius(int depth, double rowHeight)
        {
            if (rowHeight <= 0d)
            {
                return 0d;
            }

            var mid = Math.Round(rowHeight / 2d);
            var beadRadius = GetBeadRadius(depth, hasChildren: true);
            var maxRadius = rowHeight - mid - beadRadius - 1d;
            var radius = Math.Min(GetToggleRadius(depth), maxRadius);
            return radius < 3d ? 0d : radius;
        }
    }
}
