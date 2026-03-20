using System.Collections.ObjectModel;
using System.Windows;
using LiveCharts;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.ThemeIntegration;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.Views.ThemeIntegration.Base;

namespace PlayniteAchievements.Views.ThemeIntegration.Desktop
{
    /// <summary>
    /// Desktop PlayniteAchievements pie chart control for theme integration.
    /// Displays rarity distribution as a pie chart with radial badge icons.
    /// Uses the effective theme source so settings previews can inject mock data.
    /// </summary>
    public partial class AchievementPieChartControl : ThemeControlBase
    {
        /// <summary>
        /// Gets a value indicating whether this control should subscribe to theme data change notifications.
        /// </summary>
        protected override bool EnableAutomaticThemeDataUpdates => true;
        protected override bool UsesThemeBindings => true;

        private readonly PieChartViewModel _viewModel = new PieChartViewModel();

        /// <summary>
        /// Gets the pie series collection for the chart.
        /// </summary>
        public SeriesCollection PieSeries => _viewModel.PieSeries;

        /// <summary>
        /// Gets the legend items for the chart.
        /// </summary>
        public ObservableCollection<LegendItem> LegendItems => _viewModel.LegendItems;

        public AchievementPieChartControl()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Determines whether a change raised from modern theme bindings should trigger a refresh.
        /// </summary>
        protected override bool ShouldHandleThemeDataChange(string propertyName)
        {
            return propertyName == nameof(ModernThemeBindings.Common) ||
                   propertyName == nameof(ModernThemeBindings.Uncommon) ||
                   propertyName == nameof(ModernThemeBindings.Rare) ||
                   propertyName == nameof(ModernThemeBindings.UltraRare) ||
                   propertyName == nameof(ModernThemeBindings.LockedCount);
        }

        /// <summary>
        /// Called when theme data changes. Updates the pie chart.
        /// </summary>
        protected override void OnThemeDataUpdated()
        {
            var theme = EffectiveTheme;
            if (theme == null) return;

            _viewModel.SetRarityData(
                theme.Common.Unlocked, theme.Uncommon.Unlocked, theme.Rare.Unlocked, theme.UltraRare.Unlocked, theme.LockedCount,
                theme.Common.Total, theme.Uncommon.Total, theme.Rare.Total, theme.UltraRare.Total,
                "Common", "Uncommon", "Rare", "Ultra Rare", "Locked");
        }
    }
}

