using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.Achievements;

namespace PlayniteAchievements.Views.ThemeIntegration.Modern
{
    /// <summary>
    /// Modern theme integration control displaying unlocked achievements in a horizontal scrolling row.
    /// Shows compact achievement icons with progress bars and rarity glow effects.
    /// Filters to show only unlocked achievements.
    /// </summary>
    public partial class AchievementCompactUnlockedListControl : AchievementCompactListControlBase
    {
        public AchievementCompactUnlockedListControl()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Returns true only for unlocked achievements.
        /// </summary>
        protected override bool FilterAchievement(AchievementDetail achievement) => achievement.Unlocked;

        /// <summary>
        /// Uses the shared selected-game sort source, then filters to unlocked achievements.
        /// </summary>
        protected override AchievementSortSurface SortSurface => AchievementSortSurface.CompactUnlockedList;

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


