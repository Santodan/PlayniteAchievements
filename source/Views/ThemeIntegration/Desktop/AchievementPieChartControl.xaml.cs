using System.Collections.ObjectModel;
using System.Windows;
using LiveCharts;
using PlayniteAchievements.Models;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.Views.ThemeIntegration.Base;

namespace PlayniteAchievements.Views.ThemeIntegration.Desktop
{
    /// <summary>
    /// Desktop PlayniteAchievements pie chart control for theme integration.
    /// Displays rarity distribution as a pie chart with radial badge icons.
    /// </summary>
    public partial class AchievementPieChartControl : ThemeControlBase
    {
        private readonly PieChartViewModel _viewModel = new PieChartViewModel();

        /// <summary>
        /// Gets the pie series collection for the chart.
        /// </summary>
        public SeriesCollection PieSeries => _viewModel.PieSeries;

        /// <summary>
        /// Gets the legend items for the chart.
        /// </summary>
        public ObservableCollection<LegendItem> LegendItems => _viewModel.LegendItems;

        /// <summary>
        /// Gets a value indicating whether this control should subscribe to theme data change notifications.
        /// </summary>
        protected override bool EnableAutomaticThemeDataUpdates => true;

        public AchievementPieChartControl()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Called when theme data changes and the pie chart needs to be refreshed.
        /// </summary>
        protected override void OnThemeDataUpdated()
        {
            var theme = Plugin.Settings.Theme;
            if (theme == null)
            {
                return;
            }

            var commonUnlocked = theme.Common.Unlocked;
            var uncommonUnlocked = theme.Uncommon.Unlocked;
            var rareUnlocked = theme.Rare.Unlocked;
            var ultraRareUnlocked = theme.UltraRare.Unlocked;
            var locked = theme.LockedCount;

            var commonTotal = theme.Common.Total;
            var uncommonTotal = theme.Uncommon.Total;
            var rareTotal = theme.Rare.Total;
            var ultraRareTotal = theme.UltraRare.Total;

            _viewModel.SetRarityData(
                commonUnlocked, uncommonUnlocked, rareUnlocked, ultraRareUnlocked, locked,
                commonTotal, uncommonTotal, rareTotal, ultraRareTotal,
                "Common", "Uncommon", "Rare", "Ultra Rare", "Locked");
        }
    }
}
