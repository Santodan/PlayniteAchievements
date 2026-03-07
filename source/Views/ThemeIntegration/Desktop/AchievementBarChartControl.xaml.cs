using System.Collections.Generic;
using System.Linq;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.Views.ThemeIntegration.Base;

namespace PlayniteAchievements.Views.ThemeIntegration.Desktop
{
    /// <summary>
    /// Desktop PlayniteAchievements bar chart control for theme integration.
    /// Displays unlock timeline using LiveCharts with the same TimelineViewModel as the sidebar.
    /// Binds directly to Plugin.Settings.Theme properties.
    /// </summary>
    public partial class AchievementBarChartControl : ThemeControlBase
    {
        /// <summary>
        /// Gets a value indicating whether this control should subscribe to theme data change notifications.
        /// </summary>
        protected override bool EnableAutomaticThemeDataUpdates => true;

        /// <summary>
        /// Gets the timeline view model that manages chart data and state.
        /// Shared with the sidebar for consistent behavior and styling.
        /// </summary>
        public TimelineViewModel TimelineViewModel { get; } = new TimelineViewModel();

        public AchievementBarChartControl()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Determines whether a change raised from ThemeData should trigger a refresh.
        /// </summary>
        protected override bool ShouldHandleThemeDataChange(string propertyName)
        {
            return propertyName == nameof(Models.ThemeIntegration.ThemeData.AllAchievements);
        }

        /// <summary>
        /// Called when theme data changes. Updates the chart.
        /// </summary>
        protected override void OnThemeDataUpdated()
        {
            var allAchievements = Plugin?.Settings?.Theme?.AllAchievements;
            if (allAchievements == null || !allAchievements.Any())
            {
                TimelineViewModel.SetCounts(null);
                return;
            }

            // Build counts by date from unlocked achievements
            var countsByDate = allAchievements
                .Where(a => a.Unlocked && a.UnlockTimeUtc.HasValue)
                .GroupBy(a => a.UnlockTimeUtc.Value.Date)
                .ToDictionary(g => g.Key, g => g.Count());

            TimelineViewModel.SetCounts(countsByDate);
        }
    }
}
