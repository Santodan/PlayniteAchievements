// --SUCCESSSTORY--
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Playnite.SDK.Controls;
using Playnite.SDK.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.ThemeIntegration;
using PlayniteAchievements.Views.ThemeIntegration.Base;

namespace PlayniteAchievements.Views.ThemeIntegration.Legacy
{
    /// <summary>
    /// SuccessStory-compatible compact list control for theme integration.
    /// Matches the original SuccessStory plugin styling.
    /// </summary>
    public partial class PluginCompactListControl : ThemeControlBase
    {
        #region IconHeight Property

        public static readonly DependencyProperty IconHeightProperty = DependencyProperty.Register(
            nameof(IconHeight),
            typeof(double),
            typeof(PluginCompactListControl),
            new FrameworkPropertyMetadata(48.0, OnIconHeightChanged));

        public double IconHeight
        {
            get => (double)GetValue(IconHeightProperty);
            set => SetValue(IconHeightProperty, value);
        }

        #endregion

        #region CompactHeight Property

        public static readonly DependencyProperty CompactHeightProperty = DependencyProperty.Register(
            nameof(CompactHeight),
            typeof(double),
            typeof(PluginCompactListControl),
            new FrameworkPropertyMetadata(76.0));

        public double CompactHeight
        {
            get => (double)GetValue(CompactHeightProperty);
            private set => SetValue(CompactHeightProperty, value);
        }

        #endregion

        #region ShowHiddenIcon Property

        public static readonly DependencyProperty ShowHiddenIconProperty = DependencyProperty.Register(
            nameof(ShowHiddenIcon),
            typeof(bool),
            typeof(PluginCompactListControl),
            new FrameworkPropertyMetadata(false));

        public bool ShowHiddenIcon
        {
            get => (bool)GetValue(ShowHiddenIconProperty);
            set => SetValue(ShowHiddenIconProperty, value);
        }

        #endregion

        #region ShowRarityGlow Property

        public static readonly DependencyProperty ShowRarityGlowProperty = DependencyProperty.Register(
            nameof(ShowRarityGlow),
            typeof(bool),
            typeof(PluginCompactListControl),
            new FrameworkPropertyMetadata(true));

        public bool ShowRarityGlow
        {
            get => (bool)GetValue(ShowRarityGlowProperty);
            set => SetValue(ShowRarityGlowProperty, value);
        }

        #endregion

        public PluginCompactListControl()
        {
            InitializeComponent();
            IconHeight = 48.0;
            CompactHeight = IconHeight + 28.0;
        }

        private static void OnIconHeightChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is PluginCompactListControl control)
            {
                var iconHeight = e.NewValue is double value ? value : 48.0;
                control.CompactHeight = Math.Max(0, iconHeight + 28.0);
            }
        }

        #region Hidden Achievement Reveal

        private void AchievementGrid_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is AchievementDetail achievement)
            {
                // Only toggle reveal for hidden, unlocked achievements
                if (achievement.Hidden && !achievement.Unlocked)
                {
                    var current = HiddenRevealHelper.GetIsRevealed(fe);
                    HiddenRevealHelper.SetIsRevealed(fe, !current);
                    e.Handled = true;
                }
            }
        }

        #endregion
    }
}
// --END SUCCESSSTORY--
