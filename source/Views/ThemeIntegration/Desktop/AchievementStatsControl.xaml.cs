using Playnite.SDK.Controls;
using PlayniteAchievements.Views.ThemeIntegration.Base;

namespace PlayniteAchievements.Views.ThemeIntegration.Desktop
{
    /// <summary>
    /// Desktop PlayniteAchievements stats control for theme integration.
    /// Displays rarity statistics breakdown in a 4-row grid.
    /// </summary>
    public partial class AchievementStatsControl : ThemeControlBase
    {
        /// <summary>
        /// Gets a value indicating whether this control should subscribe to theme data change notifications.
        /// </summary>
        protected override bool EnableAutomaticThemeDataUpdates => true;

        public AchievementStatsControl()
        {
            InitializeComponent();
        }
    }
}
