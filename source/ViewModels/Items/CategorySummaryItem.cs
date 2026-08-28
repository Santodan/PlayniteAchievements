using System;
using System.Windows;

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
        /// Depth of the path, 1 for a root category. Setting it also sets the name cell's inset,
        /// so a list holding every node of the tree reads as one.
        /// </summary>
        public int CategoryDepth
        {
            get => _categoryDepth;
            set
            {
                _categoryDepth = value;
                NameIndent = new Thickness(NestingIndentPerLevel * Math.Max(0, value - 1), 0, 0, 0);
            }
        }

        private const double NestingIndentPerLevel = 16;

        private int _categoryDepth = 1;

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
