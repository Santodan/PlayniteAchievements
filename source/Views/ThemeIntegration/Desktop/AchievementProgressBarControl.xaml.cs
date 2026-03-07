using Playnite.SDK.Controls;
using PlayniteAchievements.Views.ThemeIntegration.Base;

namespace PlayniteAchievements.Views.ThemeIntegration.Desktop
{
    /// <summary>
    /// Desktop PlayniteAchievements progress bar control for theme integration.
    /// Displays progress bar with percentage overlay and rarity badges.
    /// </summary>
    public partial class AchievementProgressBarControl : ThemeControlBase
    {
        /// <summary>
        /// Gets a value indicating whether this control should subscribe to theme data change notifications.
        /// </summary>
        protected override bool EnableAutomaticThemeDataUpdates => true;

        public AchievementProgressBarControl()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Called when theme data changes and badges need to be refreshed.
        /// </summary>
        protected override void OnThemeDataUpdated()
        {
            Badges?.UpdateFromThemeData(Plugin.Settings.Theme);
        }
    }
}
