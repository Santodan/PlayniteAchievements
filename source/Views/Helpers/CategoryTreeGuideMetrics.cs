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
    }
}
