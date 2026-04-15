// --SUCCESSSTORY--
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Playnite.SDK;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.ThemeIntegration;
using PlayniteAchievements.Views.ThemeIntegration.Base;
using PlayniteAchievements.Views.ThemeIntegration.Legacy.Controls;

namespace PlayniteAchievements.Views.ThemeIntegration.Legacy
{
    /// <summary>
    /// SuccessStory-compatible compact locked control for theme integration.
    /// Displays locked achievements in a horizontal grid, with "+X more" in the last column.
    /// </summary>
    public partial class PluginCompactLockedControl : CompactAchievementControlBase
    {
        protected override Grid CompactViewGrid => PART_ScCompactView;

        public PluginCompactLockedControl()
        {
            InitializeComponent();
            DataContext = this;
        }

        protected override List<AchievementDetail> GetSourceAchievements()
        {
            var all = EffectiveLegacyTheme?.ListAchievements;
            if (all == null || all.Count == 0)
            {
                return new List<AchievementDetail>();
            }

            var result = new List<AchievementDetail>();
            for (int i = 0; i < all.Count; i++)
            {
                var a = all[i];
                if (a != null && !a.Unlocked)
                {
                    result.Add(a);
                }
            }

            return result;
        }

        protected override int GetTotalCount()
        {
            return EffectiveLegacyTheme?.Locked ?? 0;
        }

        protected override string GetHasAchievementPropertyName()
        {
            return nameof(HasLocked);
        }

        protected override string GetTotalCountPropertyName()
        {
            return nameof(TotalLocked);
        }

        protected override string[] GetSettingsPropertyNames()
        {
            return new[]
            {
                nameof(LegacyThemeBindings.ListAchievements),
                nameof(LegacyThemeBindings.Locked)
            };
        }

        protected override AchievementImage CreateAchievementImage(AchievementDetail achievement)
        {
            // Check if achievement is hidden and should be obscured
            bool isHiddenAndObscured = achievement.Hidden &&
                                       !achievement.Unlocked &&
                                       !ShowHiddenIcon;

            string displayIcon = isHiddenAndObscured
                ? AchievementIconResolver.GetDefaultIcon()
                : achievement.LockedIconDisplay;

            string displayToolTip = isHiddenAndObscured
                ? ResourceProvider.GetString("LOCPlayAch_Achievements_HiddenTitle")
                : achievement.DisplayName;

            var image = new AchievementImage
            {
                Width = IconHeight,
                Height = IconHeight,
                ToolTip = displayToolTip,
                Icon = isHiddenAndObscured ? displayIcon : achievement.UnlockedIconDisplay,
                IconCustom = displayIcon,
                IsLocked = true,
                Percent = achievement.RarityPercentValue,
                HasRarityPercent = achievement.HasRarityPercent,
                Rarity = achievement.Rarity,
                RarityText = achievement.RarityText,
                EnableRaretyIndicator = true,
                DisplayRaretyValue = true,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = achievement.Hidden && !achievement.Unlocked ? Cursors.Hand : null,
                Tag = achievement // Store achievement for click handler
            };

            // Bind ShowRarityGlow directly to settings - automatic updates with no manual sync needed
            var glowBinding = new Binding("Settings.Persisted.ShowRarityGlow")
            {
                Source = Plugin,
                Mode = BindingMode.OneWay,
                FallbackValue = true
            };
            image.SetBinding(AchievementImage.ShowRarityGlowProperty, glowBinding);

            // Add click handler for hidden achievements
            if (achievement.Hidden && !achievement.Unlocked)
            {
                image.MouseLeftButtonDown += HiddenAchievement_MouseLeftButtonDown;
                HiddenRevealHelper.SetIsRevealed(image, false);
            }

            return image;
        }

        private void HiddenAchievement_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is AchievementDetail achievement)
            {
                if (achievement.Hidden && !achievement.Unlocked)
                {
                    var current = HiddenRevealHelper.GetIsRevealed(fe);
                    HiddenRevealHelper.SetIsRevealed(fe, !current);

                    // Update the display
                    bool isRevealed = !current;
                    bool isHiddenAndObscured = !isRevealed;

                    string displayIcon = isHiddenAndObscured
                        ? AchievementIconResolver.GetDefaultIcon()
                        : achievement.LockedIconDisplay;

                    string displayToolTip = isHiddenAndObscured
                        ? ResourceProvider.GetString("LOCPlayAch_Achievements_HiddenTitle")
                        : achievement.DisplayName;

                    if (fe is AchievementImage image)
                    {
                        image.Icon = isHiddenAndObscured ? displayIcon : achievement.UnlockedIconDisplay;
                        image.IconCustom = displayIcon;
                        image.ToolTip = displayToolTip;
                    }

                    e.Handled = true;
                }
            }
        }

        public bool HasLocked => GetTotalCount() > 0;

        public int TotalLocked => GetTotalCount();

        // RemainingCount is provided by the base class
    }
}
// --END SUCCESSSTORY--
