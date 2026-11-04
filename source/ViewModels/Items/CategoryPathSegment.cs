using System;
using System.Collections.Generic;
using System.Linq;
using PlayniteAchievements.Services.Achievements;

namespace PlayniteAchievements.ViewModels.Items
{
    /// <summary>
    /// One hop in the category path a host shows above its achievement grid, so a nested category
    /// reads as "Game &gt; DLC &gt; Winter &gt; Frost".
    ///
    /// <see cref="Depth"/> is how many segments of the drilled path this hop stands for, so 1 is the
    /// outermost category; 0 marks the elided hop the path panel shows in place of ancestors it
    /// had no room for. Every hop is inert: the game-name hop the hosts render to the left is the
    /// one control that returns to the category list, so the segments only name where the grid
    /// currently is.
    /// </summary>
    public sealed class CategoryPathSegment
    {
        private const string Ellipsis = "…";

        private CategoryPathSegment(string content, int depth, bool isCurrent, string toolTip)
        {
            Content = content;
            Depth = depth;
            IsCurrent = isCurrent;
            ToolTip = toolTip;
        }

        public string Content { get; }

        public int Depth { get; }

        /// <summary>Last hop in the path; the separator trails every hop except this one.</summary>
        public bool IsCurrent { get; }

        /// <summary>Always false: see the type remarks. Kept so the shared template can disable the hop.</summary>
        public bool IsNavigable => false;

        public bool ShowSeparator => !IsCurrent;

        public string ToolTip { get; }

        /// <summary>
        /// Builds the hops for a drilled path: the elided marker first, carrying the ancestors as
        /// its tooltip, then one hop per level with the level being viewed last. The panel that
        /// lays them out decides which ancestors fit and whether the marker shows.
        ///
        /// Empty when nothing is drilled, so a host can bind an ItemsControl straight to it.
        /// </summary>
        internal static IReadOnlyList<CategoryPathSegment> Build(IReadOnlyList<string> pathSegments)
        {
            if (pathSegments == null || pathSegments.Count == 0)
            {
                return Array.Empty<CategoryPathSegment>();
            }

            var last = pathSegments.Count - 1;
            var result = new List<CategoryPathSegment>(pathSegments.Count + 1);

            if (last > 0)
            {
                result.Add(new CategoryPathSegment(
                    Ellipsis,
                    depth: 0,
                    isCurrent: false,
                    toolTip: string.Join(
                        " > ",
                        pathSegments.Take(last).Select(CategoryPathHelper.ToDisplayLeaf))));
            }

            for (var i = 0; i <= last; i++)
            {
                result.Add(new CategoryPathSegment(
                    CategoryPathHelper.ToDisplayLeaf(pathSegments[i]),
                    depth: i + 1,
                    isCurrent: i == last,
                    toolTip: null));
            }

            return result;
        }
    }
}
