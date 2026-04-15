using System.Collections.Generic;
using System.Windows.Controls;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Models.ThemeIntegration;

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
            InitializeComponent();
        }

        /// <summary>
        /// Returns true only for locked achievements (not unlocked).
        /// </summary>
        protected override bool FilterAchievement(AchievementDetail achievement) => !achievement.Unlocked;

        /// <summary>
        /// Selects the pre-sorted achievement collection based on CompactLockedListSortMode and direction.
        /// None (default) returns provider order.
        /// </summary>
        protected override List<AchievementDetail> GetOrderedAchievements(ModernThemeBindings theme)
        {
            if (theme == null)
            {
                return new List<AchievementDetail>();
            }

            var settings = EffectiveSettings?.Persisted;
            if (settings == null)
            {
                return theme.AllAchievements ?? new List<AchievementDetail>();
            }

            switch (settings.CompactLockedListSortMode)
            {
                case CompactListSortMode.UnlockTime:
                    return settings.CompactLockedListSortDescending
                        ? theme.AchievementsNewestFirst ?? theme.AllAchievements
                        : theme.AchievementsOldestFirst ?? theme.AllAchievements;
                case CompactListSortMode.Rarity:
                    return settings.CompactLockedListSortDescending
                        ? theme.AchievementsRarityDesc ?? theme.AllAchievements
                        : theme.AchievementsRarityAsc ?? theme.AllAchievements;
                default:
                    return theme.AllAchievements ?? new List<AchievementDetail>();
            }
        }

        protected override string GetOrderedAchievementsPropertyName()
        {
            var settings = EffectiveSettings?.Persisted;
            if (settings == null)
            {
                return nameof(ModernThemeBindings.AllAchievements);
            }

            switch (settings.CompactLockedListSortMode)
            {
                case CompactListSortMode.UnlockTime:
                    return settings.CompactLockedListSortDescending
                        ? nameof(ModernThemeBindings.AchievementsNewestFirst)
                        : nameof(ModernThemeBindings.AchievementsOldestFirst);
                case CompactListSortMode.Rarity:
                    return settings.CompactLockedListSortDescending
                        ? nameof(ModernThemeBindings.AchievementsRarityDesc)
                        : nameof(ModernThemeBindings.AchievementsRarityAsc);
                default:
                    return nameof(ModernThemeBindings.AllAchievements);
            }
        }

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

