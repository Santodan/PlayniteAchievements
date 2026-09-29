using System.Collections.ObjectModel;
using System.Windows;
using LiveCharts;
using Playnite.SDK;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Models.ThemeIntegration;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.Views.ThemeIntegration.Base;

namespace PlayniteAchievements.Views.ThemeIntegration.Modern
{
    /// <summary>
    /// Modern PlayniteAchievements pie chart control for theme integration.
    /// Displays rarity distribution as a pie chart with radial badge icons.
    /// Uses the effective theme source so settings previews can inject mock data.
    /// </summary>
    /// <remarks>
    /// How the pie renders is set by the theme through this control's own dependency properties,
    /// not read from the Overview's pie settings: those govern the Overview window only, the same
    /// way showcase and start page widgets carry their own per-widget options. The global rarity
    /// appearance settings still apply, because the slices carry the <c>BadgeRarity*</c> aliases.
    /// </remarks>
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

        public static readonly DependencyProperty IncludeLockedProperty =
            DependencyProperty.Register(nameof(IncludeLocked), typeof(bool), typeof(AchievementPieChartControl),
                new PropertyMetadata(true, OnRenderOptionChanged));

        /// <summary>
        /// Whether the locked achievements appear as their own slice.
        /// </summary>
        public bool IncludeLocked
        {
            get => (bool)GetValue(IncludeLockedProperty);
            set => SetValue(IncludeLockedProperty, value);
        }

        public static readonly DependencyProperty SmallSliceModeProperty =
            DependencyProperty.Register(nameof(SmallSliceMode), typeof(OverviewPieSmallSliceMode), typeof(AchievementPieChartControl),
                new PropertyMetadata(OverviewPieSmallSliceMode.Round, OnRenderOptionChanged));

        /// <summary>
        /// What happens to a slice too small to render as its exact share.
        /// </summary>
        public OverviewPieSmallSliceMode SmallSliceMode
        {
            get => (OverviewPieSmallSliceMode)GetValue(SmallSliceModeProperty);
            set => SetValue(SmallSliceModeProperty, value);
        }

        public static readonly DependencyProperty ShowCenterPercentageProperty =
            DependencyProperty.Register(nameof(ShowCenterPercentage), typeof(bool), typeof(AchievementPieChartControl),
                new PropertyMetadata(true, OnRenderOptionChanged));

        /// <summary>
        /// Whether the completion percentage is drawn in the middle of the pie.
        /// </summary>
        public bool ShowCenterPercentage
        {
            get => (bool)GetValue(ShowCenterPercentageProperty);
            set => SetValue(ShowCenterPercentageProperty, value);
        }

        public AchievementPieChartControl()
        {
            InitializeComponent();

            // The view model subscribes to the process-lifetime appearance event, so a control
            // the theme discards would stay rooted through it. Detach while unloaded and
            // re-attach on reload (themes re-template, so both fire more than once); the
            // attach/detach pair is idempotent.
            Loaded += (_, __) => _viewModel.AttachAppearance();
            Unloaded += (_, __) => _viewModel.Dispose();
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

        protected override bool ShouldHandleSettingsDataChange(string propertyName)
        {
            // The badge shapes come from the BadgeRarity* aliases, which are repointed when this
            // flips, so the chart only needs to repaint.
            return propertyName == nameof(PersistedSettings.UseUniformRarityBadges);
        }

        /// <summary>
        /// Re-pushes the data when a render option changes, because the small-slice mode and the
        /// locked slice are baked in by <see cref="PieChartViewModel.SetRarityData"/>.
        /// </summary>
        private static void OnRenderOptionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            (d as AchievementPieChartControl)?.OnThemeDataUpdated();
        }

        /// <summary>
        /// Called when theme data changes. Updates the pie chart.
        /// </summary>
        protected override void OnThemeDataUpdated()
        {
            var theme = EffectiveTheme;
            if (theme == null) return;

            // All three are applied by SetRarityData, so they must be assigned before the data.
            _viewModel.IncludeLocked = IncludeLocked;
            _viewModel.SmallSliceMode = SmallSliceMode;
            _viewModel.ShowCenterPercentageRequested = ShowCenterPercentage;
            _viewModel.SetRarityData(
                theme.Common.Unlocked, theme.Uncommon.Unlocked, theme.Rare.Unlocked, theme.UltraRare.Unlocked, theme.LockedCount,
                theme.Common.Total, theme.Uncommon.Total, theme.Rare.Total, theme.UltraRare.Total,
                ResourceProvider.GetString("LOCPlayAch_Rarity_Common"),
                ResourceProvider.GetString("LOCPlayAch_Rarity_Uncommon"),
                ResourceProvider.GetString("LOCPlayAch_Rarity_Rare"),
                ResourceProvider.GetString("LOCPlayAch_Rarity_UltraRare"),
                ResourceProvider.GetString("LOCPlayAch_Common_Locked"));
        }
    }
}
