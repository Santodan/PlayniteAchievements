using System.Collections.Generic;
using System.Linq;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Models.ThemeIntegration;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.Views.ThemeIntegration.Modern
{
    /// <summary>
    /// Modern theme integration control displaying unlocked achievements in a horizontal scrolling row.
    /// Shows compact achievement icons with progress bars and rarity glow effects.
    /// Filters to show only unlocked achievements.
    /// </summary>
    public partial class AchievementCompactUnlockedListControl : AchievementCompactListControlBase
    {
        public static readonly System.Windows.DependencyProperty FeaturedItemProperty =
            System.Windows.DependencyProperty.Register(
                nameof(FeaturedItem),
                typeof(AchievementDisplayItem),
                typeof(AchievementCompactUnlockedListControl),
                new System.Windows.PropertyMetadata(null));

        public AchievementCompactUnlockedListControl()
        {
            InitializeComponent();
        }

        public AchievementDisplayItem FeaturedItem
        {
            get => (AchievementDisplayItem)GetValue(FeaturedItemProperty);
            private set => SetValue(FeaturedItemProperty, value);
        }

        /// <summary>
        /// Returns true only for unlocked achievements.
        /// </summary>
        protected override bool FilterAchievement(AchievementDetail achievement) => achievement.Unlocked;

        /// <summary>
        /// Uses the shared selected-game sort source, then filters to unlocked achievements.
        /// </summary>
        protected override AchievementSortSurface SortSurface => AchievementSortSurface.CompactUnlockedList;

        protected override List<AchievementDetail> GetOrderedAchievements(ModernThemeBindings theme)
        {
            var persisted = EffectiveSettings?.Persisted;
            if (persisted == null || persisted.CompactUnlockedListSortMode == CompactListSortMode.None)
            {
                return theme?.AchievementsNewestFirst ?? base.GetOrderedAchievements(theme);
            }

            return base.GetOrderedAchievements(theme);
        }

        /// <summary>
        /// Refreshes the ItemsControl ItemsSource binding.
        /// </summary>
        protected override void RefreshItemsSource()
        {
            FeaturedItem = DisplayItems.FirstOrDefault();

            if (AchievementsList != null)
            {
                AchievementsList.ItemsSource = DisplayItems.Skip(1).ToList();
            }
        }
    }
}


