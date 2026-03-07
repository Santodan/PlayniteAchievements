using PlayniteAchievements.Views.ThemeIntegration.Base;

namespace PlayniteAchievements.Views.ThemeIntegration.Desktop
{
    /// <summary>
    /// Desktop PlayniteAchievements stats control for theme integration.
    /// Displays rarity statistics breakdown in a 4-row grid.
    /// Binds directly to Plugin.Settings.Theme properties.
    /// </summary>
    public partial class AchievementStatsControl : ThemeControlBase
    {
        public AchievementStatsControl()
        {
            InitializeComponent();
        }
    }
}
