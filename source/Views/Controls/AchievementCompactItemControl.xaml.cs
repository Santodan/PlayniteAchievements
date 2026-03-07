using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PlayniteAchievements.ViewModels;

namespace PlayniteAchievements.Views.Controls
{
    /// <summary>
    /// Reusable compact achievement item control with icon, progress bar, and rarity glow.
    /// Designed for horizontal scrolling lists in theme integration.
    /// </summary>
    public partial class AchievementCompactItemControl : UserControl
    {
        public static readonly DependencyProperty IconSizeProperty =
            DependencyProperty.Register(nameof(IconSize), typeof(double), typeof(AchievementCompactItemControl),
                new PropertyMetadata(56.0, OnIconSizeChanged));

        /// <summary>
        /// Gets or sets the size of the achievement icon (both width and height).
        /// Default is 56 to allow space for glow effect around the icon.
        /// </summary>
        public double IconSize
        {
            get => (double)GetValue(IconSizeProperty);
            set => SetValue(IconSizeProperty, value);
        }

        private static void OnIconSizeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is AchievementCompactItemControl control && e.NewValue is double size)
            {
                // Add 8px padding for glow effect visibility
                control.Width = size + 8;
                control.Height = size + 8;
            }
        }

        public AchievementCompactItemControl()
        {
            InitializeComponent();
            Width = IconSize;
            Height = IconSize;

            // Handle click to reveal hidden achievements
            MouseLeftButtonDown += OnMouseLeftButtonDown;
        }

        private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (DataContext is AchievementDisplayItem item && item.CanReveal)
            {
                item.ToggleReveal();
                e.Handled = true;
            }
        }
    }
}
