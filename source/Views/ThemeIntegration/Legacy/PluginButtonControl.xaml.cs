// --SUCCESSSTORY--
using System;
using System.Windows;
using System.Windows.Controls;
using Playnite.SDK.Controls;
using Playnite.SDK.Models;
using PlayniteAchievements.Views.ThemeIntegration.Base;

namespace PlayniteAchievements.Views.ThemeIntegration.Legacy
{
    /// <summary>
    /// SuccessStory-compatible button control for theme integration.
    /// Uses modern PlayniteAchievements properties (AchievementCount, UnlockedCount, IsCompleted).
    /// Matches the original SuccessStory plugin styling.
    /// </summary>
    public partial class PluginButtonControl : ThemeControlBase
    {
        #region DisplayDetails Property

        public static readonly DependencyProperty DisplayDetailsProperty =
            ThemeIntegration.Base.DependencyPropertyHelper.RegisterBoolProperty(
                nameof(DisplayDetails),
                typeof(PluginButtonControl),
                defaultValue: false);

        public bool DisplayDetails
        {
            get => (bool)GetValue(DisplayDetailsProperty);
            set => SetValue(DisplayDetailsProperty, value);
        }

        #endregion

        public PluginButtonControl()
        {
            InitializeComponent();
        }

        private void PART_PluginButton_Click(object sender, RoutedEventArgs e)
        {
            // Open the per-game achievements view for the currently selected game
            var game = Plugin?.Settings?.SelectedGame;
            if (game != null)
            {
                Plugin?.OpenSingleGameAchievementsView(game.Id);
            }
        }
    }
}
// --END SUCCESSSTORY--

