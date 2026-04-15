using System;
using System.Windows;
using System.Windows.Controls;
using PlayniteAchievements.Models.Achievements;

namespace PlayniteAchievements.Views.ThemeIntegration.Legacy.Controls
{
    /// <summary>
    /// Achievement image control matching SuccessStory's AchievementImage.
    /// Displays achievement icon with rarity badge overlay instead of colored glow.
    /// </summary>
    public partial class AchievementImage : UserControl
    {
        #region Properties

        public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
            nameof(Icon),
            typeof(string),
            typeof(AchievementImage),
            new FrameworkPropertyMetadata(null, IconChanged)
        );
        public string Icon
        {
            get => (string)GetValue(IconProperty);
            set => SetValue(IconProperty, value);
        }
        private static void IconChanged(DependencyObject obj, DependencyPropertyChangedEventArgs args)
        {
            try
            {
                AchievementImage control = (AchievementImage)obj;
                control.NewProperty();
            }
            catch
            {
                // Ignore errors loading icons
            }
        }

        public static readonly DependencyProperty EnableRaretyIndicatorProperty = DependencyProperty.Register(
            nameof(EnableRaretyIndicator),
            typeof(bool),
            typeof(AchievementImage),
            new FrameworkPropertyMetadata(true, PercentUiChanged)
        );
        public bool EnableRaretyIndicator
        {
            get => (bool)GetValue(EnableRaretyIndicatorProperty);
            set => SetValue(EnableRaretyIndicatorProperty, value);
        }

        public static readonly DependencyProperty ShowRarityGlowProperty = DependencyProperty.Register(
            nameof(ShowRarityGlow),
            typeof(bool),
            typeof(AchievementImage),
            new FrameworkPropertyMetadata(true)
        );
        public bool ShowRarityGlow
        {
            get => (bool)GetValue(ShowRarityGlowProperty);
            set => SetValue(ShowRarityGlowProperty, value);
        }

        public static readonly DependencyProperty DisplayRaretyValueProperty = DependencyProperty.Register(
            nameof(DisplayRaretyValue),
            typeof(bool),
            typeof(AchievementImage),
            new FrameworkPropertyMetadata(true, PercentUiChanged)
        );
        public bool DisplayRaretyValue
        {
            get => (bool)GetValue(DisplayRaretyValueProperty);
            set => SetValue(DisplayRaretyValueProperty, value);
        }

        public static readonly DependencyProperty ShowGlobalPercentBarProperty = DependencyProperty.Register(
            nameof(ShowGlobalPercentBar),
            typeof(bool),
            typeof(AchievementImage),
            new FrameworkPropertyMetadata(true, PercentUiChanged)
        );
        public bool ShowGlobalPercentBar
        {
            get => (bool)GetValue(ShowGlobalPercentBarProperty);
            set => SetValue(ShowGlobalPercentBarProperty, value);
        }

        public static readonly DependencyProperty PercentProperty = DependencyProperty.Register(
            nameof(Percent),
            typeof(double),
            typeof(AchievementImage),
            new FrameworkPropertyMetadata(default(double), PercentUiChanged)
        );
        public double Percent
        {
            get => (double)GetValue(PercentProperty);
            set => SetValue(PercentProperty, value);
        }

        public static readonly DependencyProperty HasRarityPercentProperty = DependencyProperty.Register(
            nameof(HasRarityPercent),
            typeof(bool),
            typeof(AchievementImage),
            new FrameworkPropertyMetadata(true, PercentUiChanged)
        );
        public bool HasRarityPercent
        {
            get => (bool)GetValue(HasRarityPercentProperty);
            set => SetValue(HasRarityPercentProperty, value);
        }

        public static readonly DependencyProperty RarityProperty = DependencyProperty.Register(
            nameof(Rarity),
            typeof(RarityTier?),
            typeof(AchievementImage),
            new FrameworkPropertyMetadata(null, PercentUiChanged)
        );
        public RarityTier? Rarity
        {
            get => (RarityTier?)GetValue(RarityProperty);
            set => SetValue(RarityProperty, value);
        }

        public static readonly DependencyProperty RarityTextProperty = DependencyProperty.Register(
            nameof(RarityText),
            typeof(string),
            typeof(AchievementImage),
            new FrameworkPropertyMetadata(string.Empty, PercentUiChanged)
        );
        public string RarityText
        {
            get => (string)GetValue(RarityTextProperty);
            set => SetValue(RarityTextProperty, value);
        }

        private static void PercentUiChanged(DependencyObject obj, DependencyPropertyChangedEventArgs args)
        {
            try
            {
                var control = (AchievementImage)obj;
                control.UpdatePercentUi();
            }
            catch
            {
                // Ignore errors
            }
        }

        public static readonly DependencyProperty IsLockedProperty = DependencyProperty.Register(
            nameof(IsLocked),
            typeof(bool),
            typeof(AchievementImage),
            new FrameworkPropertyMetadata(false, PropertyChanged)
        );
        public bool IsLocked
        {
            get => (bool)GetValue(IsLockedProperty);
            set => SetValue(IsLockedProperty, value);
        }

        public static readonly DependencyProperty IconTextProperty = DependencyProperty.Register(
            nameof(IconText),
            typeof(string),
            typeof(AchievementImage),
            new FrameworkPropertyMetadata(string.Empty)
        );
        public string IconText
        {
            get => (string)GetValue(IconTextProperty);
            set => SetValue(IconTextProperty, value);
        }

        public static readonly DependencyProperty IconCustomProperty = DependencyProperty.Register(
            nameof(IconCustom),
            typeof(string),
            typeof(AchievementImage),
            new FrameworkPropertyMetadata(null, PropertyChanged)
        );
        public string IconCustom
        {
            get => (string)GetValue(IconCustomProperty);
            set => SetValue(IconCustomProperty, value);
        }

        public static readonly DependencyProperty UltraRareThresholdProperty = DependencyProperty.Register(
            nameof(UltraRareThreshold),
            typeof(double),
            typeof(AchievementImage),
            new FrameworkPropertyMetadata(5.0)
        );
        public double UltraRareThreshold
        {
            get => (double)GetValue(UltraRareThresholdProperty);
            set => SetValue(UltraRareThresholdProperty, value);
        }

        public static readonly DependencyProperty RareThresholdProperty = DependencyProperty.Register(
            nameof(RareThreshold),
            typeof(double),
            typeof(AchievementImage),
            new FrameworkPropertyMetadata(20.0)
        );
        public double RareThreshold
        {
            get => (double)GetValue(RareThresholdProperty);
            set => SetValue(RareThresholdProperty, value);
        }

        public static readonly DependencyProperty UncommonThresholdProperty = DependencyProperty.Register(
            nameof(UncommonThreshold),
            typeof(double),
            typeof(AchievementImage),
            new FrameworkPropertyMetadata(50.0)
        );
        public double UncommonThreshold
        {
            get => (double)GetValue(UncommonThresholdProperty);
            set => SetValue(UncommonThresholdProperty, value);
        }

        private static void PropertyChanged(DependencyObject obj, DependencyPropertyChangedEventArgs args)
        {
            try
            {
                AchievementImage control = (AchievementImage)obj;
                control.NewProperty();
            }
            catch
            {
                // Ignore errors
            }
        }
        #endregion

        public AchievementImage()
        {
            InitializeComponent();
            NewProperty();
            UpdatePercentUi();
        }

        private void Image_ImageFailed(object sender, ExceptionRoutedEventArgs e)
        {
            // Do not fall back to disk resources. Keep the image empty on failure.
            ((Image)sender).Source = null;
        }

        private void Image_Loaded(object sender, RoutedEventArgs e)
        {
            UpdatePercentUi();
        }

        private void UpdatePercentUi()
        {
            try
            {
                if (!Dispatcher.CheckAccess())
                {
                    // Never block the calling thread waiting for UI.
                    // This control can be updated from background contexts during data refresh.
                    Dispatcher.BeginInvoke(new Action(UpdatePercentUi));
                    return;
                }

                var rounded = Math.Round(Percent, 1);
                var overlayText = !string.IsNullOrWhiteSpace(RarityText)
                    ? RarityText
                    : HasRarityPercent
                        ? $"{rounded:F1}%"
                        : string.Empty;
                var showOverlay = EnableRaretyIndicator &&
                                  DisplayRaretyValue &&
                                  !string.IsNullOrWhiteSpace(overlayText);

                if (PART_RarityOverlay != null)
                {
                    PART_RarityOverlay.Visibility = showOverlay ? Visibility.Visible : Visibility.Collapsed;
                }

                if (PART_Label != null)
                {
                    PART_Label.Content = overlayText;
                }

                if (PART_ProgressBar != null)
                {
                    PART_ProgressBar.Value = rounded;
                    PART_ProgressBar.Visibility = showOverlay && HasRarityPercent && ShowGlobalPercentBar
                        ? Visibility.Visible
                        : Visibility.Collapsed;
                }
            }
            catch
            {
                // Ignore
            }
        }

        /// <summary>
        /// Updates UI state based on IsLocked/IconCustom.
        /// </summary>
        private void NewProperty()
        {
            if (PART_IconText != null)
            {
                var hasExplicitLockedIcon = IsLocked &&
                    AchievementIconResolver.HasExplicitLockedIcon(IconCustom, Icon);

                PART_IconText.Visibility = hasExplicitLockedIcon
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
        }

        // Icon selection is handled in XAML via MultiBinding to avoid breaking bindings when data updates.
    }
}
