using Playnite.SDK;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Overview;
using PlayniteAchievements.Services.StartPage;

namespace PlayniteAchievements.ViewModels.StartPage
{
    public sealed class StartPageGameSummariesGridViewModel : StartPageWidgetViewModelBase
    {
        public StartPageGameSummariesGridViewModel(
            StartPageDataCoordinator dataCoordinator,
            PlayniteAchievementsSettings settings,
            ILogger logger)
            : base(dataCoordinator, settings, logger)
        {
        }

        public BulkObservableCollection<GameSummaryItem> Items { get; } =
            new BulkObservableCollection<GameSummaryItem>();

        private StartPageGameSummariesGridSettings WidgetSettings =>
            PersistedSettings?.StartPageGameSummariesGrid ?? new StartPageGameSummariesGridSettings();

        public bool ShowGameMetadata => WidgetSettings.ShowGameMetadata;

        public bool UseCoverImages => WidgetSettings.UseCoverImages;

        public bool ShowCompletionBorder => WidgetSettings.ShowCompletionBorder;

        public bool ShowColumnHeaders => WidgetSettings.ShowColumnHeaders;

        public double? RowHeight => WidgetSettings.RowHeight;

        protected override void ApplySnapshot(OverviewDataSnapshot snapshot)
        {
            Items.ReplaceAll(StartPageWidgetProjection.ProjectGameSummaries(
                snapshot?.GameSummaries,
                PersistedSettings));
            OnPropertyChanged(nameof(ShowGameMetadata));
            OnPropertyChanged(nameof(UseCoverImages));
            OnPropertyChanged(nameof(ShowCompletionBorder));
            OnPropertyChanged(nameof(ShowColumnHeaders));
            OnPropertyChanged(nameof(RowHeight));
        }

        protected override void OnPersistedSettingsChanged(string propertyName)
        {
            if (string.IsNullOrEmpty(propertyName) ||
                IsWidgetSettingsProperty(propertyName, nameof(StartPageGameSummariesGridSettings.ShowGameMetadata)))
            {
                OnPropertyChanged(nameof(ShowGameMetadata));
            }

            if (string.IsNullOrEmpty(propertyName) ||
                IsWidgetSettingsProperty(propertyName, nameof(StartPageGameSummariesGridSettings.UseCoverImages)))
            {
                OnPropertyChanged(nameof(UseCoverImages));
            }

            if (string.IsNullOrEmpty(propertyName) ||
                IsWidgetSettingsProperty(propertyName, nameof(StartPageGameSummariesGridSettings.ShowCompletionBorder)))
            {
                OnPropertyChanged(nameof(ShowCompletionBorder));
            }

            if (string.IsNullOrEmpty(propertyName) ||
                IsWidgetSettingsProperty(propertyName, nameof(StartPageGameSummariesGridSettings.ShowColumnHeaders)))
            {
                OnPropertyChanged(nameof(ShowColumnHeaders));
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
