using System.Windows;
using Playnite.SDK;
using Playnite.SDK.Models;
using PlayniteAchievements.Services.Logging;
using PlayniteAchievements.Views.ThemeIntegration.Base;

namespace PlayniteAchievements.Views.ThemeIntegration.Modern
{
    /// <summary>
    /// Modern view item control for theme integration.
    /// Displays achievement summary per game in grid/list views.
    /// </summary>
    public partial class AchievementViewItemControl : ThemeControlBase
    {
        private static readonly ILogger _logger = PluginLogger.GetLogger(nameof(AchievementViewItemControl));
        private readonly ViewItemAchievementDataLoader _loader;

        #region ShowProgressBar Property

        public static readonly DependencyProperty ShowProgressBarProperty =
            DependencyProperty.Register(
                nameof(ShowProgressBar),
                typeof(bool),
                typeof(AchievementViewItemControl),
                new PropertyMetadata(false, OnShowProgressBarChanged));

        public bool ShowProgressBar
        {
            get => (bool)GetValue(ShowProgressBarProperty);
            set => SetValue(ShowProgressBarProperty, value);
        }

        private static void OnShowProgressBarChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is AchievementViewItemControl control)
            {
                control.UpdateModeVisibility();
            }
        }

        #endregion

        #region UnlockedCount Property

        public static readonly DependencyProperty UnlockedCountProperty =
            DependencyProperty.Register(
                nameof(UnlockedCount),
                typeof(int),
                typeof(AchievementViewItemControl),
                new PropertyMetadata(0));

        public int UnlockedCount
        {
            get => (int)GetValue(UnlockedCountProperty);
            set => SetValue(UnlockedCountProperty, value);
        }

        #endregion

        #region AchievementCount Property

        public static readonly DependencyProperty AchievementCountProperty =
            DependencyProperty.Register(
                nameof(AchievementCount),
                typeof(int),
                typeof(AchievementViewItemControl),
                new PropertyMetadata(0));

        public int AchievementCount
        {
            get => (int)GetValue(AchievementCountProperty);
            set => SetValue(AchievementCountProperty, value);
        }

        #endregion

        #region HasData Property

        public static readonly DependencyProperty HasDataProperty =
            DependencyProperty.Register(
                nameof(HasData),
                typeof(bool),
                typeof(AchievementViewItemControl),
                new PropertyMetadata(false));

        public bool HasData
        {
            get => (bool)GetValue(HasDataProperty);
            set => SetValue(HasDataProperty, value);
        }

        #endregion

        public AchievementViewItemControl()
        {
            InitializeComponent();
            _loader = new ViewItemAchievementDataLoader(
                this, UnlockedCountProperty, AchievementCountProperty, HasDataProperty, _logger);
            Loaded += (sender, e) => UpdateModeVisibility();
        }

        private void UpdateModeVisibility()
        {
            if (LabelMode != null && ProgressBarMode != null)
            {
                LabelMode.Visibility = ShowProgressBar ? Visibility.Collapsed : Visibility.Visible;
                ProgressBarMode.Visibility = ShowProgressBar ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        public override void GameContextChanged(Game oldContext, Game newContext)
        {
            _loader.GameContextChanged(newContext);
        }
    }
}
