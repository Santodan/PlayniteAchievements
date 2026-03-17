using System.Windows;
using PlayniteAchievements.Views.ThemeIntegration.Base;

namespace PlayniteAchievements.Views.ThemeIntegration.Desktop
{
    /// <summary>
    /// Desktop PlayniteAchievements button control for theme integration.
    /// Displays progress information with detailed and non-detailed display modes.
    /// Supports ThemeDataOverride through inherited bindings for settings previews.
    /// </summary>
    public partial class AchievementButtonControl : ThemeControlBase
    {
        /// <summary>
        /// Identifies the <see cref="DisplayDetails"/> dependency property.
        /// </summary>
        public static readonly DependencyProperty DisplayDetailsProperty =
            DependencyProperty.Register(nameof(DisplayDetails), typeof(bool), typeof(AchievementButtonControl),
                new PropertyMetadata(false));

        /// <summary>
        /// Gets or sets a value indicating whether to display detailed progress information.
        /// When true, shows "Achievements" label, count, and progress bar.
        /// When false, shows only a trophy icon.
        /// </summary>
        public bool DisplayDetails
        {
            get => (bool)GetValue(DisplayDetailsProperty);
            set => SetValue(DisplayDetailsProperty, value);
        }

        public AchievementButtonControl()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Handles the button click event.
        /// Opens the per-game achievements view for the currently selected game.
        /// </summary>
        private void PART_PluginButton_Click(object sender, RoutedEventArgs e)
        {
            var game = Plugin?.Settings?.SelectedGame;
            if (game != null)
            {
                Plugin?.OpenSingleGameAchievementsView(game.Id);
            }
        }
    }
}
