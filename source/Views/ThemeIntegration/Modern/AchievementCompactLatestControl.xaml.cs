using System;
using System.Collections.Generic;
using System.Windows;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.ThemeIntegration;
using PlayniteAchievements.Views.Helpers;

namespace PlayniteAchievements.Views.ThemeIntegration.Modern
{
    /// <summary>
    /// Modern theme integration control showing the selected game's most recently unlocked
    /// achievement as a single compact icon, with the same rarity glow as the compact lists.
    /// Themes lay out the name, description and date beside it from LatestAchievementData.
    /// </summary>
    public partial class AchievementCompactLatestControl : AchievementCompactListControlBase
    {
        private const double DefaultIconSize = 48.0;
        private const double MinimumIconSize = 16.0;

        // Matches the item Margin in the XAML on each side.
        private const double ItemMargin = 4.0;

        private AchievementDetail _lastLatest;
        private List<AchievementDetail> _lastLatestList = new List<AchievementDetail>();

        public AchievementCompactLatestControl()
        {
            InitializeComponent();
        }

        /// <summary>
        /// A single icon has nothing to scroll, so the wheel stays with the page.
        /// </summary>
        protected override WheelScrollAxis? WheelClaimAxis => null;

        protected override bool FilterAchievement(AchievementDetail achievement) => achievement.Unlocked;

        /// <summary>
        /// Returns the newest unlock as a one-item list. The list is reused while the latest
        /// achievement is unchanged so the base class can skip rebuilding.
        /// </summary>
        protected override List<AchievementDetail> GetOrderedAchievements(ModernThemeBindings theme)
        {
            var latest = theme?.LatestAchievementData;
            if (!ReferenceEquals(latest, _lastLatest))
            {
                _lastLatest = latest;
                _lastLatestList = latest == null
                    ? new List<AchievementDetail>()
                    : new List<AchievementDetail> { latest };
            }

            return _lastLatestList;
        }

        protected override bool ShouldHandleThemeDataChange(string propertyName)
        {
            return propertyName == nameof(ModernThemeBindings.LatestAchievementData) ||
                   base.ShouldHandleThemeDataChange(propertyName);
        }

        protected override void RefreshItemsSource()
        {
            if (AchievementsList != null)
            {
                AchievementsList.ItemsSource = DisplayItems;
            }
        }

        /// <summary>
        /// Sizes the icon to the height the theme gives the control, since a theme cannot set
        /// IconSize on a control the plugin creates. An unconstrained height keeps the default
        /// size; a finite width only caps it, so a wide vertical host does not inflate the icon.
        /// The size is applied as IconSize rather than by scaling, so the bitmap stays crisp.
        /// </summary>
        protected override Size MeasureOverride(Size constraint)
        {
            var iconSize = IsFinite(constraint.Height)
                ? constraint.Height - (ItemMargin * 2)
                : DefaultIconSize;
            if (IsFinite(constraint.Width))
            {
                iconSize = Math.Min(iconSize, constraint.Width - (ItemMargin * 2));
            }

            iconSize = Math.Max(MinimumIconSize, Math.Floor(iconSize));

            if (!IconSize.Equals(iconSize))
            {
                IconSize = iconSize;
            }

            return base.MeasureOverride(constraint);
        }

        private static bool IsFinite(double value) => !double.IsInfinity(value) && !double.IsNaN(value);
    }
}
