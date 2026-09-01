using PlayniteAchievements.Services.Summaries;

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

        /// <summary>Depth of the path, 1 for a root category.</summary>
        public int CategoryDepth { get; set; } = 1;

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
        /// Stats over the achievements labelled exactly this node - what an expanded row reports,
        /// so the visible rows partition the set. Stashed by the tree builder; null on rows built
        /// by the flat and level surfaces, which never swap.
        /// </summary>
        internal AchievementGameStats OwnStats { get; set; }

        /// <summary>
        /// Stats over the node's whole subtree - what a collapsed row reports, absorbing the
        /// descendants its collapse hid. Stashed by the tree builder; null elsewhere.
        /// </summary>
        internal AchievementGameStats SubtreeStats { get; set; }

        internal bool OwnIsCompleted { get; set; }

        internal bool SubtreeIsCompleted { get; set; }

        /// <summary>
        /// Which snapshot the row's live stat properties currently hold. Defaults to Own because
        /// the builder applies the own-members reading as it emits the row.
        /// </summary>
        internal CategoryStatsScope AppliedStatsScope { get; private set; } = CategoryStatsScope.Own;

        /// <summary>
        /// Swaps the row's live stat properties to the given snapshot in place. Every setter the
        /// grid binds raises change notification, so the published row repaints without any list
        /// churn. No-op when the scope already matches or the snapshots were never stashed.
        /// </summary>
        internal void ApplyStats(CategoryStatsScope scope)
        {
            if (scope == AppliedStatsScope)
            {
                return;
            }

            var stats = scope == CategoryStatsScope.Subtree ? SubtreeStats : OwnStats;
            if (stats == null)
            {
                return;
            }

            stats.ApplyTo(this);
            IsCompleted = scope == CategoryStatsScope.Subtree ? SubtreeIsCompleted : OwnIsCompleted;
            AppliedStatsScope = scope;
        }

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
