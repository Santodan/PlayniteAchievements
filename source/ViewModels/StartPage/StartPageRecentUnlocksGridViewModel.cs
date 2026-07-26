using System.Collections.Generic;
using System.Linq;
using Playnite.SDK;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.Overview;
using PlayniteAchievements.Services.Search;
using PlayniteAchievements.Services.StartPage;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.ViewModels.StartPage
{
    public sealed class StartPageRecentUnlocksGridViewModel : StartPageWidgetViewModelBase
    {
        private readonly SearchTextIndex<AchievementDisplayItem> _searchIndex =
            new SearchTextIndex<AchievementDisplayItem>(item =>
                SearchTextBuilder.ForRecentAchievement(item?.GameName, item?.DisplayName));
        private List<AchievementDisplayItem> _sourceItems = new List<AchievementDisplayItem>();
        private string _searchText = string.Empty;

        public StartPageRecentUnlocksGridViewModel(
            StartPageDataCoordinator dataCoordinator,
            PlayniteAchievementsSettings settings,
            ILogger logger)
            : base(dataCoordinator, settings, logger)
        {
            ControlBar = new GridControlBarViewModel
            {
                Search = new GridSearchControl(
                    this,
                    nameof(SearchText),
                    () => SearchText,
                    value => SearchText = value,
                    L("LOCPlayAch_Filter_Achievements"),
                    () => SearchText = string.Empty)
            };
        }

        public BulkObservableCollection<AchievementDisplayItem> Items { get; } =
            new BulkObservableCollection<AchievementDisplayItem>();

        public GridControlBarViewModel ControlBar { get; }

        public string SearchText
        {
            get => _searchText;
            set
            {
                var normalized = value ?? string.Empty;
                if (string.Equals(_searchText, normalized, System.StringComparison.Ordinal))
                {
                    return;
                }

                _searchText = normalized;
                OnPropertyChanged(nameof(SearchText));
                ApplyCurrentItems();
            }
        }

        private StartPageRecentUnlocksGridSettings WidgetSettings =>
            PersistedSettings?.StartPageRecentUnlocksGrid ?? new StartPageRecentUnlocksGridSettings();

        public bool UseCoverImages => WidgetSettings.UseCoverImages;

        public bool ShowRarityGlow => WidgetSettings.ShowRarityGlow;

        public bool ColorNamesByRarity => WidgetSettings.ColorNamesByRarity;

        public bool ColorRarityColumnsByRarity => WidgetSettings.ColorRarityColumnsByRarity;

        public bool ShowColumnHeaders => WidgetSettings.ShowColumnHeaders;

        public bool ShowControlBar => WidgetSettings.ShowControlBar;

        public double? RowHeight => WidgetSettings.RowHeight;

        protected override void ApplySnapshot(OverviewDataSnapshot snapshot)
        {
            _sourceItems = (snapshot?.RecentAchievements ?? new List<AchievementDisplayItem>())
                .Where(item => item != null)
                .ToList();
            ApplyCurrentItems();
            OnPropertyChanged(nameof(UseCoverImages));
            OnPropertyChanged(nameof(ShowRarityGlow));
            OnPropertyChanged(nameof(ColorNamesByRarity));
            OnPropertyChanged(nameof(ColorRarityColumnsByRarity));
            OnPropertyChanged(nameof(ShowColumnHeaders));
            OnPropertyChanged(nameof(ShowControlBar));
            OnPropertyChanged(nameof(RowHeight));
        }

        private void ApplyCurrentItems()
        {
            Items.ReplaceAll(StartPageWidgetProjection.ProjectRecentUnlocks(
                StartPageWidgetProjection.FilterRecentUnlocksBySearch(_sourceItems, _searchIndex, SearchText),
                PersistedSettings,
                appearanceSettings: Settings));
        }

        protected override void OnPersistedSettingsChanged(string propertyName)
        {
            if (string.IsNullOrEmpty(propertyName) ||
                IsWidgetSettingsProperty(propertyName, nameof(StartPageRecentUnlocksGridSettings.UseCoverImages)))
            {
                OnPropertyChanged(nameof(UseCoverImages));
            }

            if (string.IsNullOrEmpty(propertyName) ||
                IsWidgetSettingsProperty(propertyName, nameof(StartPageRecentUnlocksGridSettings.ShowRarityGlow)))
            {
                OnPropertyChanged(nameof(ShowRarityGlow));
            }

            if (string.IsNullOrEmpty(propertyName) ||
                IsWidgetSettingsProperty(propertyName, nameof(StartPageRecentUnlocksGridSettings.ColorNamesByRarity)))
            {
                OnPropertyChanged(nameof(ColorNamesByRarity));
            }

            if (string.IsNullOrEmpty(propertyName) ||
                IsWidgetSettingsProperty(propertyName, nameof(StartPageRecentUnlocksGridSettings.ColorRarityColumnsByRarity)))
            {
                OnPropertyChanged(nameof(ColorRarityColumnsByRarity));
            }

            if (string.IsNullOrEmpty(propertyName) ||
                IsWidgetSettingsProperty(propertyName, nameof(StartPageRecentUnlocksGridSettings.ShowColumnHeaders)))
            {
                OnPropertyChanged(nameof(ShowColumnHeaders));
            }

            if (string.IsNullOrEmpty(propertyName) ||
                IsWidgetSettingsProperty(propertyName, nameof(StartPageRecentUnlocksGridSettings.ShowControlBar)))
            {
                OnPropertyChanged(nameof(ShowControlBar));
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

        private static string L(string key)
        {
            return ResourceProvider.GetString(key);
        }
    }
}
