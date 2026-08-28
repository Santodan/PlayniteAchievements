using System.Collections.Concurrent;
using System.Globalization;
using System.Windows.Media;

namespace PlayniteAchievements.ViewModels.Items
{
    /// <summary>
    /// A <see cref="GameSummaryItem"/> that represents a rollup over one achievement category
    /// rather than a game. Carries the normalized category label as a stable key so the grid can
    /// map a clicked summary row back to its category when drilling into the achievement list.
    /// The display name (localized label) is held by <see cref="GameSummaryItem.GameName"/> so the
    /// existing game-summary column templates render it unchanged.
    /// </summary>
    public sealed class CategorySummaryItem : GameSummaryItem
    {
        public string CategoryLabel { get; set; }

        /// <summary>
        /// The category's fully qualified path. Same value as <see cref="CategoryLabel"/>, named
        /// for what it is once labels can nest.
        /// </summary>
        public string CategoryPath { get; set; }

        /// <summary>Last path segment - what a drilled level titles its rows with.</summary>
        public string CategoryLeafName { get; set; }

        /// <summary>
        /// Depth of the path, 1 for a root category. Setting it also builds the name cell's tree
        /// guide, so a list holding every node of the tree reads as one.
        /// </summary>
        public int CategoryDepth
        {
            get => _categoryDepth;
            set
            {
                _categoryDepth = value;
                NameGuideWidth = GetGuideWidth(value);
                NameGuide = GetGuide(value);
            }
        }

        private int _categoryDepth = 1;

        // Geometry of the guide: a leg dropping from the row above, turning right into an arrowhead
        // that points at the label. Length carries the depth, so a glance reads nesting without
        // counting blank space.
        private const double GuideLeadIn = 6;
        private const double GuidePerLevel = 16;
        private const double GuideMidY = 6;
        private const double GuideHeadSize = 3.5;

        // Depths are capped at CategoryPathHelper.MaxDepth, so this holds at most a handful of
        // frozen geometries shared by every row at that depth.
        private static readonly ConcurrentDictionary<int, Geometry> GuideByDepth =
            new ConcurrentDictionary<int, Geometry>();

        private static double GetGuideWidth(int depth)
        {
            return depth <= 1 ? 0 : GuideLeadIn + (GuidePerLevel * (depth - 1));
        }

        private static Geometry GetGuide(int depth)
        {
            if (depth <= 1)
            {
                return null;
            }

            return GuideByDepth.GetOrAdd(depth, d =>
            {
                var tipX = GetGuideWidth(d) - 4;
                var geometry = Geometry.Parse(string.Format(
                    CultureInfo.InvariantCulture,
                    "M {0},0 L {0},{1} L {2},{1} M {3},{4} L {2},{1} L {3},{5}",
                    GuideLeadIn - 1,
                    GuideMidY,
                    tipX,
                    tipX - GuideHeadSize,
                    GuideMidY - GuideHeadSize,
                    GuideMidY + GuideHeadSize));

                // Frozen so one instance is shared across rows and threads.
                geometry.Freeze();
                return geometry;
            });
        }

        /// <summary>
        /// How many immediate child categories this row aggregates. Zero for a leaf, which is what
        /// tells a surface whether clicking the row drills to another category level or straight to
        /// the achievements.
        /// </summary>
        public int ChildCategoryCount { get; set; }

        public bool HasChildCategories => ChildCategoryCount > 0;

        /// <summary>
        /// Achievements whose category is exactly this row, as opposed to a descendant's. A node
        /// with both children and direct achievements is legal and renders as both.
        /// </summary>
        public int DirectAchievementCount { get; set; }

        /// <summary>
        /// The category's group-based type token (one of Base/DLC/Update/Subset, or Default when the
        /// bucket has no group membership). Carries the locale-independent classification so the
        /// theme-facing summary can expose type flags (IsBaseCategory, etc.).
        /// </summary>
        public string CategoryType { get; set; }

        private bool _allowCompletionBadge = true;

        /// <summary>
        /// Whether the CategoryCompletionBadgeMode display setting permits this row to render the
        /// completion badge. Stamped by <see cref="PlayniteAchievements.Services.Summaries.CategorySummaryBuilder"/>
        /// in configured category order, so it is stable across the grid's column sorts.
        /// </summary>
        public bool AllowCompletionBadge
        {
            get => _allowCompletionBadge;
            set
            {
                if (SetValueAndReturn(ref _allowCompletionBadge, value))
                {
                    OnPropertyChanged(nameof(ShowCompletionBadge));
                }
            }
        }

        public override bool ShowCompletionBadge => IsCompleted && AllowCompletionBadge;
    }
}
