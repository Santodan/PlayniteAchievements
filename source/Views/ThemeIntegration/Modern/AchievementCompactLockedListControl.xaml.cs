using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Services;

namespace PlayniteAchievements.Views.ThemeIntegration.Modern
{
    /// <summary>
    /// Modern theme integration control displaying locked achievements in a horizontal scrolling row.
    /// Shows compact achievement icons with progress bars and rarity glow effects.
    /// Filters to show only locked (not yet unlocked) achievements.
    /// </summary>
    public partial class AchievementCompactLockedListControl : AchievementCompactListControlBase
    {
        public AchievementCompactLockedListControl()
        {
            VisibleCount = 5;
            InitializeComponent();
        }

        /// <summary>
        /// Returns true only for locked achievements (not unlocked).
        /// </summary>
        protected override bool FilterAchievement(AchievementDetail achievement) => !achievement.Unlocked;

        /// <summary>
        /// Uses the shared selected-game sort source, then filters to locked achievements.
        /// </summary>
        protected override AchievementSortSurface SortSurface => AchievementSortSurface.CompactLockedList;

        protected override bool UseAdaptiveOverflowPreview => false;

        /// <summary>
        /// Refreshes the ItemsControl ItemsSource binding.
        /// </summary>
        protected override void RefreshItemsSource()
        {
            if (AchievementsList != null)
            {
                AchievementsList.ItemsSource = DisplayItems;
            }
        }
    }
}


