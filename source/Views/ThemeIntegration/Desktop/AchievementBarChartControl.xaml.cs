using System.Collections.Generic;
using System.Linq;
using Playnite.SDK.Controls;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.Views.ThemeIntegration.Base;

namespace PlayniteAchievements.Views.ThemeIntegration.Desktop
{
    /// <summary>
    /// Desktop PlayniteAchievements bar chart control for theme integration.
    /// Displays unlock timeline using LiveCharts with the same TimelineViewModel as the sidebar.
    /// </summary>
    public partial class AchievementBarChartControl : ThemeControlBase
    {
        /// <summary>
        /// Gets the timeline view model that manages chart data and state.
        /// Shared with the sidebar for consistent behavior and styling.
        /// </summary>
        public TimelineViewModel TimelineViewModel { get; } = new TimelineViewModel();

        /// <summary>
        /// Gets a value indicating whether this control should subscribe to theme data change notifications.
        /// </summary>
        protected override bool EnableAutomaticThemeDataUpdates => true;

        public AchievementBarChartControl()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Called when theme data changes and the chart needs to be refreshed.
        /// </summary>
        protected override void OnThemeDataUpdated()
        {
            var achievements = Plugin.Settings.Theme?.AllAchievements;
            if (achievements == null || !achievements.Any())
            {
                TimelineViewModel.SetCounts(null);
                return;
            }

            // Build counts by date from unlocked achievements
            var countsByDate = achievements
                .Where(a => a.Unlocked && a.UnlockTimeUtc.HasValue)
                .GroupBy(a => a.UnlockTimeUtc.Value.Date)
                .ToDictionary(g => g.Key, g => g.Count());

            TimelineViewModel.SetCounts(countsByDate);
        }
    }
}
