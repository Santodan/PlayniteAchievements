using System.Collections.Generic;
using System.Windows.Controls;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Models.ThemeIntegration;

namespace PlayniteAchievements.Views.ThemeIntegration.Modern
{
    /// <summary>
    /// Modern theme integration control displaying all achievements in a horizontal scrolling row.
    /// Shows compact achievement icons with progress bars and rarity glow effects.
    /// Inherits filtering and data loading from AchievementCompactListControlBase.
    /// </summary>
    public partial class AchievementCompactListControl : AchievementCompactListControlBase
    {
        public AchievementCompactListControl()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Returns true to show all achievements (no filtering).
        /// </summary>
        protected override bool FilterAchievement(AchievementDetail achievement) => true;

        /// <summary>
        /// Selects the pre-sorted achievement collection based on CompactListSortMode and direction.
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

            switch (settings.CompactListSortMode)
            {
                case CompactListSortMode.UnlockTime:
                    return settings.CompactListSortDescending
                        ? theme.AchievementsNewestFirst ?? theme.AllAchievements
                        : theme.AchievementsOldestFirst ?? theme.AllAchievements;
                case CompactListSortMode.Rarity:
                    return settings.CompactListSortDescending
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

            switch (settings.CompactListSortMode)
            {
                case CompactListSortMode.UnlockTime:
                    return settings.CompactListSortDescending
                        ? nameof(ModernThemeBindings.AchievementsNewestFirst)
                        : nameof(ModernThemeBindings.AchievementsOldestFirst);
                case CompactListSortMode.Rarity:
                    return settings.CompactListSortDescending
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

