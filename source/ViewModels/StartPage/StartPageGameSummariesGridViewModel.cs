using System;
using System.Collections.Generic;
using System.Linq;
using Playnite.SDK;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Overview;
using PlayniteAchievements.Services.StartPage;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.ViewModels.StartPage
{
    public sealed class StartPageGameSummariesGridViewModel : StartPageWidgetViewModelBase
    {
        private readonly GameSummaryGridControlBarAdapter _controlBarAdapter =
            new GameSummaryGridControlBarAdapter();
        private List<GameSummaryItem> _sourceItems = new List<GameSummaryItem>();

        public StartPageGameSummariesGridViewModel(
            StartPageDataCoordinator dataCoordinator,
            PlayniteAchievementsSettings settings,
            ILogger logger)
            : base(dataCoordinator, settings, logger)
        {
            _controlBarAdapter.FilterChanged += ControlBarAdapter_FilterChanged;
        }

        public BulkObservableCollection<GameSummaryItem> Items { get; } =
            new BulkObservableCollection<GameSummaryItem>();

        public GridControlBarViewModel ControlBar => _controlBarAdapter.ControlBar;

        private StartPageGameSummariesGridSettings WidgetSettings =>
            PersistedSettings?.StartPageGameSummariesGrid ?? new StartPageGameSummariesGridSettings();

        public bool ShowMetadataPlatform => WidgetSettings.ShowMetadataPlatform;

        public bool ShowMetadataPlaytime => WidgetSettings.ShowMetadataPlaytime;

        public bool ShowMetadataRegion => WidgetSettings.ShowMetadataRegion;

        public bool UseCoverImages => WidgetSettings.UseCoverImages;

        public bool ShowCompletionGlow => WidgetSettings.ShowCompletionGlow;

        public bool ShowColumnHeaders => WidgetSettings.ShowColumnHeaders;

        public bool ShowControlBar => WidgetSettings.ShowControlBar;

        public double? RowHeight => WidgetSettings.RowHeight;

        protected override void ApplySnapshot(OverviewDataSnapshot snapshot)
        {
            _sourceItems = (snapshot?.GameSummaries ?? new List<GameSummaryItem>())
                .Where(item => item != null)
                .ToList();
            ApplyCurrentItems();
            OnPropertyChanged(nameof(ShowMetadataPlatform));
            OnPropertyChanged(nameof(ShowMetadataPlaytime));
            OnPropertyChanged(nameof(ShowMetadataRegion));
            OnPropertyChanged(nameof(UseCoverImages));
            OnPropertyChanged(nameof(ShowCompletionGlow));
            OnPropertyChanged(nameof(ShowColumnHeaders));
            OnPropertyChanged(nameof(ShowControlBar));
            OnPropertyChanged(nameof(RowHeight));
        }

        private void ApplyCurrentItems()
        {
            var baseline = StartPageWidgetProjection.FilterGameSummariesForStartPage(
                _sourceItems,
                PersistedSettings,
                includeProgressScope: true);
            _controlBarAdapter.UpdateOptions(baseline);
            Items.ReplaceAll(StartPageWidgetProjection.ProjectFilteredGameSummaries(
                _controlBarAdapter.Apply(baseline),
                PersistedSettings));
        }

        private void ControlBarAdapter_FilterChanged(object sender, EventArgs e)
        {
            ApplyCurrentItems();
        }

        protected override void OnPersistedSettingsChanged(string propertyName)
        {
            if (string.IsNullOrEmpty(propertyName) ||
                IsWidgetSettingsProperty(propertyName, nameof(StartPageGameSummariesGridSettings.ShowMetadataPlatform)))
            {
                OnPropertyChanged(nameof(ShowMetadataPlatform));
            }

            if (string.IsNullOrEmpty(propertyName) ||
                IsWidgetSettingsProperty(propertyName, nameof(StartPageGameSummariesGridSettings.ShowMetadataPlaytime)))
            {
                OnPropertyChanged(nameof(ShowMetadataPlaytime));
            }

            if (string.IsNullOrEmpty(propertyName) ||
                IsWidgetSettingsProperty(propertyName, nameof(StartPageGameSummariesGridSettings.ShowMetadataRegion)))
            {
                OnPropertyChanged(nameof(ShowMetadataRegion));
            }

            if (string.IsNullOrEmpty(propertyName) ||
                IsWidgetSettingsProperty(propertyName, nameof(StartPageGameSummariesGridSettings.UseCoverImages)))
            {
                OnPropertyChanged(nameof(UseCoverImages));
            }

            if (string.IsNullOrEmpty(propertyName) ||
                IsWidgetSettingsProperty(propertyName, nameof(StartPageGameSummariesGridSettings.ShowCompletionGlow)))
            {
                OnPropertyChanged(nameof(ShowCompletionGlow));
            }

            if (string.IsNullOrEmpty(propertyName) ||
                IsWidgetSettingsProperty(propertyName, nameof(StartPageGameSummariesGridSettings.ShowColumnHeaders)))
            {
                OnPropertyChanged(nameof(ShowColumnHeaders));
            }

            if (string.IsNullOrEmpty(propertyName) ||
                IsWidgetSettingsProperty(propertyName, nameof(StartPageGameSummariesGridSettings.ShowControlBar)))
            {
                OnPropertyChanged(nameof(ShowControlBar));
            }

            if (string.IsNullOrEmpty(propertyName) ||
                IsWidgetSettingsProperty(propertyName, nameof(StartPageGameSummariesGridSettings.RowHeight)) ||
                propertyName == nameof(PersistedSettings.StartPageGameSummariesGridRowHeight))
            {
                OnPropertyChanged(nameof(RowHeight));
            }
        }

        protected override bool ShouldRefreshForPersistedSettingsChanged(string propertyName)
        {
            if (IsWidgetSettingsProperty(propertyName, nameof(StartPageGameSummariesGridSettings.SortMode)) ||
                IsWidgetSettingsProperty(propertyName, nameof(StartPageGameSummariesGridSettings.SortDescending)) ||
                IsWidgetSettingsProperty(propertyName, nameof(StartPageGameSummariesGridSettings.MaxRows)) ||
                propertyName == nameof(PersistedSettings.StartPageGameSummariesGridMaxRows))
            {
                return true;
            }

            if (IsWidgetSettingsProperty(propertyName))
            {
                return false;
            }

            return base.ShouldRefreshForPersistedSettingsChanged(propertyName);
        }

        private static bool IsWidgetSettingsProperty(string propertyName, string childPropertyName = null)
        {
            if (string.IsNullOrEmpty(propertyName))
            {
                return true;
            }

            const string prefix = nameof(PersistedSettings.StartPageGameSummariesGrid) + ".";
            if (!propertyName.StartsWith(prefix))
            {
                return propertyName == nameof(PersistedSettings.StartPageGameSummariesGrid);
            }

            return string.IsNullOrEmpty(childPropertyName) ||
                   string.Equals(
                       propertyName.Substring(prefix.Length),
                       childPropertyName,
                       System.StringComparison.Ordinal);
        }
    }
}
