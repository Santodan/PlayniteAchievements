using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using LiveCharts;
using PlayniteAchievements.Models;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.Views.ThemeIntegration.Base;

namespace PlayniteAchievements.Views.ThemeIntegration.Desktop
{
    /// <summary>
    /// Desktop PlayniteAchievements pie chart control for theme integration.
    /// Displays achievement distribution as a pie chart with radial badge icons.
    /// Supports Rarity mode (default) and Trophy mode for PSN-style display.
    /// </summary>
    public partial class AchievementPieChartControl : ThemeControlBase
    {
        /// <summary>
        /// Gets a value indicating whether this control should subscribe to theme data change notifications.
        /// </summary>
        protected override bool EnableAutomaticThemeDataUpdates => true;

        private readonly PieChartViewModel _viewModel = new PieChartViewModel();

        #region DisplayMode Property

        /// <summary>
        /// Display mode for the pie chart. "Rarity" (default) or "Trophy".
        /// </summary>
        public static readonly DependencyProperty DisplayModeProperty =
            DependencyProperty.Register(
                nameof(DisplayMode),
                typeof(string),
                typeof(AchievementPieChartControl),
                new PropertyMetadata("Rarity", OnDisplayModeChanged));

        public string DisplayMode
        {
            get => (string)GetValue(DisplayModeProperty);
            set => SetValue(DisplayModeProperty, value);
        }

        private static void OnDisplayModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is AchievementPieChartControl control)
            {
                control.OnThemeDataUpdated();
            }
        }

        #endregion

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
        /// Determines whether a change raised from ThemeData should trigger a refresh.
        /// </summary>
        protected override bool ShouldHandleThemeDataChange(string propertyName)
        {
            // Rarity mode watches these properties
            if (DisplayMode == "Trophy")
            {
                return propertyName == nameof(Models.ThemeIntegration.ThemeData.AllAchievements) ||
                       propertyName == nameof(Models.ThemeIntegration.ThemeData.LockedCount);
            }

            return propertyName == nameof(Models.ThemeIntegration.ThemeData.Common) ||
                   propertyName == nameof(Models.ThemeIntegration.ThemeData.Uncommon) ||
                   propertyName == nameof(Models.ThemeIntegration.ThemeData.Rare) ||
                   propertyName == nameof(Models.ThemeIntegration.ThemeData.UltraRare) ||
                   propertyName == nameof(Models.ThemeIntegration.ThemeData.LockedCount);
        }

        /// <summary>
        /// Called when theme data changes. Updates the pie chart.
        /// </summary>
        protected override void OnThemeDataUpdated()
        {
            var theme = Plugin?.Settings?.Theme;
            if (theme == null) return;

            if (DisplayMode == "Trophy")
            {
                UpdateTrophyMode(theme);
            }
            else
            {
                UpdateRarityMode(theme);
            }
        }

        private void UpdateRarityMode(Models.ThemeIntegration.ThemeData theme)
        {
            _viewModel.SetRarityData(
                theme.Common.Unlocked, theme.Uncommon.Unlocked, theme.Rare.Unlocked, theme.UltraRare.Unlocked, theme.LockedCount,
                theme.Common.Total, theme.Uncommon.Total, theme.Rare.Total, theme.UltraRare.Total,
                "Common", "Uncommon", "Rare", "Ultra Rare", "Locked");
        }

        private void UpdateTrophyMode(Models.ThemeIntegration.ThemeData theme)
        {
            var achievements = theme.AllAchievements;
            if (achievements == null || achievements.Count == 0)
            {
                _viewModel.SetTrophyData(0, 0, 0, 0, 0, 0, 0, 0, "Platinum", "Gold", "Silver", "Bronze", "Locked");
                return;
            }

            // Count trophies by type
            int platinumUnlocked = 0, goldUnlocked = 0, silverUnlocked = 0, bronzeUnlocked = 0;
            int platinumTotal = 0, goldTotal = 0, silverTotal = 0, bronzeTotal = 0;

            foreach (var achievement in achievements)
            {
                var trophyType = achievement.TrophyType?.ToLowerInvariant();
                var isUnlocked = achievement.Unlocked;

                switch (trophyType)
                {
                    case "platinum":
                        platinumTotal++;
                        if (isUnlocked) platinumUnlocked++;
                        break;
                    case "gold":
                        goldTotal++;
                        if (isUnlocked) goldUnlocked++;
                        break;
                    case "silver":
                        silverTotal++;
                        if (isUnlocked) silverUnlocked++;
                        break;
                    case "bronze":
                        bronzeTotal++;
                        if (isUnlocked) bronzeUnlocked++;
                        break;
                }
            }

            _viewModel.SetTrophyData(
                platinumUnlocked, goldUnlocked, silverUnlocked, bronzeUnlocked,
                platinumTotal, goldTotal, silverTotal, bronzeTotal,
                "Platinum", "Gold", "Silver", "Bronze", "Locked");
        }
    }
}
