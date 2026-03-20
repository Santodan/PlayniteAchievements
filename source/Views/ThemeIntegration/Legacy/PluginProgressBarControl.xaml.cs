// --SUCCESSSTORY--
using System;
using System.Windows;
using Playnite.SDK.Controls;
using Playnite.SDK.Models;
using PlayniteAchievements.Views.ThemeIntegration.Base;

namespace PlayniteAchievements.Views.ThemeIntegration.Legacy
{
    /// <summary>
    /// SuccessStory-compatible progress bar control for theme integration.
    /// Uses modern PlayniteAchievements properties (AchievementCount, UnlockedCount, ProgressPercentage).
    /// Matches the original SuccessStory plugin styling.
    /// </summary>
    public partial class PluginProgressBarControl : ThemeControlBase
    {
        #region IntegrationShowProgressBarIndicator Property

        public static readonly DependencyProperty IntegrationShowProgressBarIndicatorProperty =
            PlayniteAchievements.Views.ThemeIntegration.Base.DependencyPropertyHelper.RegisterBoolProperty(
                nameof(IntegrationShowProgressBarIndicator),
                typeof(PluginProgressBarControl));

        public bool IntegrationShowProgressBarIndicator
        {
            get => (bool)GetValue(IntegrationShowProgressBarIndicatorProperty);
            set => SetValue(IntegrationShowProgressBarIndicatorProperty, value);
        }

        #endregion

        #region IntegrationShowProgressBarPercent Property

        public static readonly DependencyProperty IntegrationShowProgressBarPercentProperty =
            PlayniteAchievements.Views.ThemeIntegration.Base.DependencyPropertyHelper.RegisterBoolProperty(
                nameof(IntegrationShowProgressBarPercent),
                typeof(PluginProgressBarControl));

        public bool IntegrationShowProgressBarPercent
        {
            get => (bool)GetValue(IntegrationShowProgressBarPercentProperty);
            set => SetValue(IntegrationShowProgressBarPercentProperty, value);
        }

        #endregion

        public PluginProgressBarControl()
        {
            InitializeComponent();
        }
    }
}
// --END SUCCESSSTORY--

