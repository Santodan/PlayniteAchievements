using Playnite.SDK;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Overview;
using PlayniteAchievements.Services.StartPage;

namespace PlayniteAchievements.ViewModels.StartPage
{
    public sealed class StartPageRecentUnlocksGridViewModel : StartPageWidgetViewModelBase
    {
        public StartPageRecentUnlocksGridViewModel(
            StartPageDataCoordinator dataCoordinator,
            PlayniteAchievementsSettings settings,
            ILogger logger)
            : base(dataCoordinator, settings, logger)
        {
        }

        public BulkObservableCollection<AchievementDisplayItem> Items { get; } =
            new BulkObservableCollection<AchievementDisplayItem>();

        private StartPageRecentUnlocksGridSettings WidgetSettings =>
            PersistedSettings?.StartPageRecentUnlocksGrid ?? new StartPageRecentUnlocksGridSettings();

        public bool UseCoverImages => WidgetSettings.UseCoverImages;

        public bool ShowColumnHeaders => WidgetSettings.ShowColumnHeaders;

        public double? RowHeight => WidgetSettings.RowHeight;

        protected override void ApplySnapshot(OverviewDataSnapshot snapshot)
        {
            Items.ReplaceAll(StartPageWidgetProjection.ProjectRecentUnlocks(
                snapshot?.RecentAchievements,
                PersistedSettings));
            OnPropertyChanged(nameof(UseCoverImages));
            OnPropertyChanged(nameof(ShowColumnHeaders));
            OnPropertyChanged(nameof(RowHeight));
        }

        protected override void OnPersistedSettingsChanged(string propertyName)
        {
            if (string.IsNullOrEmpty(propertyName) ||
                IsWidgetSettingsProperty(propertyName, nameof(StartPageRecentUnlocksGridSettings.UseCoverImages)))
            {
                OnPropertyChanged(nameof(UseCoverImages));
            }

            if (string.IsNullOrEmpty(propertyName) ||
                IsWidgetSettingsProperty(propertyName, nameof(StartPageRecentUnlocksGridSettings.ShowColumnHeaders)))
            {
                OnPropertyChanged(nameof(ShowColumnHeaders));
            }

            if (string.IsNullOrEmpty(propertyName) ||
                IsWidgetSettingsProperty(propertyName, nameof(StartPageRecentUnlocksGridSettings.RowHeight)) ||
                propertyName == nameof(PersistedSettings.StartPageRecentAchievementsGridRowHeight))
            {
                OnPropertyChanged(nameof(RowHeight));
            }
        }

        protected override bool ShouldRefreshForPersistedSettingsChanged(string propertyName)
        {
            if (IsWidgetSettingsProperty(propertyName, nameof(StartPageRecentUnlocksGridSettings.SortMode)) ||
                IsWidgetSettingsProperty(propertyName, nameof(StartPageRecentUnlocksGridSettings.SortDescending)) ||
                IsWidgetSettingsProperty(propertyName, nameof(StartPageRecentUnlocksGridSettings.MaxRows)) ||
                propertyName == nameof(PersistedSettings.StartPageRecentAchievementsGridMaxRows))
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

            const string prefix = nameof(PersistedSettings.StartPageRecentUnlocksGrid) + ".";
            if (!propertyName.StartsWith(prefix))
            {
                return propertyName == nameof(PersistedSettings.StartPageRecentUnlocksGrid);
            }

            return string.IsNullOrEmpty(childPropertyName) ||
                   string.Equals(
                       propertyName.Substring(prefix.Length),
                       childPropertyName,
                       System.StringComparison.Ordinal);
        }
    }
}
