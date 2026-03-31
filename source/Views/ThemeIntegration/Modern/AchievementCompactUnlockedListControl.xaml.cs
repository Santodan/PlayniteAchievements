using System.Collections.Generic;
using System.Windows.Controls;
using PlayniteAchievements.Models.Achievements;

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
        /// Mirrors legacy PluginCompactUnlocked behavior: newest unlocked achievements first.
        /// </summary>
        protected override List<AchievementDetail> GetOrderedAchievements(Models.ThemeIntegration.ModernThemeBindings theme)
        {
            return theme?.AchievementsNewestFirst ?? base.GetOrderedAchievements(theme);
        }

        protected override string GetOrderedAchievementsPropertyName()
        {
            return nameof(Models.ThemeIntegration.ModernThemeBindings.AchievementsNewestFirst);
        }

        /// <summary>
        /// Refreshes the ItemsControl ItemsSource binding.
        /// </summary>
        protected override void RefreshItemsSource()
        {
            if (AchievementsList != null)
            {
                // Direct reassignment without null first - WPF detects collection changes
                // and only updates what's needed rather than rebuilding entire visual tree
                AchievementsList.ItemsSource = DisplayItems;
            }
        }
    }
}

