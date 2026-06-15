using System;
using System.Collections.Generic;
using System.Linq;
using Playnite.SDK;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.StartPage;
using PlayniteAchievements.Services.Overview;

namespace PlayniteAchievements.ViewModels.StartPage
{
    public sealed class StartPagePieWidgetViewModel : StartPageWidgetViewModelBase
    {
        private readonly StartPageWidgetKind _widgetKind;

        public StartPagePieWidgetViewModel(
            StartPageWidgetKind widgetKind,
            StartPageDataCoordinator dataCoordinator,
            PlayniteAchievementsSettings settings,
            ILogger logger)
            : base(dataCoordinator, settings, logger)
        {
            _widgetKind = widgetKind;
            Title = GetTitle(widgetKind);
        }

        public string Title { get; }

        public PieChartViewModel Chart { get; } = new PieChartViewModel();

        private StartPagePieWidgetSettings WidgetSettings =>
            PersistedSettings?.StartPagePieCharts ?? new StartPagePieWidgetSettings();

        public bool ShowCenterPercentage => WidgetSettings.ShowCenterPercentage;

        protected override void ApplySnapshot(OverviewDataSnapshot snapshot)
        {
            Chart.SmallSliceMode = WidgetSettings.SmallSliceMode;

            switch (_widgetKind)
            {
                case StartPageWidgetKind.CompletedGamesPie:
                    ApplyCompletedGames(snapshot);
                    break;
                case StartPageWidgetKind.ProviderPie:
                    ApplyProvider(snapshot);
                    break;
                case StartPageWidgetKind.RarityPie:
                    ApplyRarity(snapshot);
                    break;
                case StartPageWidgetKind.TrophyPie:
                    ApplyTrophy(snapshot);
                    break;
            }

            OnPropertyChanged(nameof(ShowCenterPercentage));
        }

        protected override void OnPersistedSettingsChanged(string propertyName)
        {
            if (string.IsNullOrEmpty(propertyName) ||
                IsWidgetSettingsProperty(propertyName, nameof(StartPagePieWidgetSettings.ShowCenterPercentage)))
            {
                OnPropertyChanged(nameof(ShowCenterPercentage));
            }
        }

        protected override bool ShouldRefreshForPersistedSettingsChanged(string propertyName)
        {
            if (IsWidgetSettingsProperty(propertyName, nameof(StartPagePieWidgetSettings.SmallSliceMode)))
            {
                return true;
            }

            if (IsWidgetSettingsProperty(propertyName))
            {
                return false;
            }

            return base.ShouldRefreshForPersistedSettingsChanged(propertyName);
        }

        private bool IsWidgetSettingsProperty(string propertyName, string childPropertyName = null)
        {
            if (string.IsNullOrEmpty(propertyName))
            {
                return true;
            }

            var parentPropertyName = nameof(PersistedSettings.StartPagePieCharts);
            var prefix = parentPropertyName + ".";
            if (!propertyName.StartsWith(prefix))
            {
                return propertyName == parentPropertyName;
            }

            return string.IsNullOrEmpty(childPropertyName) ||
                   string.Equals(
                       propertyName.Substring(prefix.Length),
                       childPropertyName,
                       StringComparison.Ordinal);
        }

        private void ApplyCompletedGames(OverviewDataSnapshot snapshot)
        {
            Chart.SetGameData(
                snapshot?.TotalGames ?? 0,
                snapshot?.CompletedGames ?? 0,
                L("LOCPlayAch_Filter_Complete", "Complete"),
                L("LOCPlayAch_Overview_Incomplete", "Incomplete"));
        }

        private void ApplyProvider(OverviewDataSnapshot snapshot)
        {
            var games = snapshot?.GameSummaries ?? new List<GameSummaryItem>();
            var unlockedByProvider = snapshot?.UnlockedByProvider ?? BuildProviderUnlockedCounts(games);
            var totalByProvider = snapshot?.TotalByProvider ?? BuildProviderTotalCounts(games);
            var totalLocked = snapshot?.TotalLocked ?? Math.Max(0, totalByProvider.Values.Sum() - unlockedByProvider.Values.Sum());
            var providerLookup = BuildProviderLookup(games);
            var providerDisplayNames = BuildProviderDisplayNames(games);

            Chart.SetProviderData(
                unlockedByProvider,
                totalByProvider,
                totalLocked,
                L("LOCPlayAch_Common_Locked", "Locked"),
                providerLookup,
                providerDisplayNames);
        }

        private void ApplyRarity(OverviewDataSnapshot snapshot)
        {
            Chart.SetRarityData(
                snapshot?.TotalCommon ?? 0,
                snapshot?.TotalUncommon ?? 0,
                snapshot?.TotalRare ?? 0,
                snapshot?.TotalUltraRare ?? 0,
                snapshot?.TotalLocked ?? Math.Max(0, (snapshot?.TotalAchievements ?? 0) - (snapshot?.TotalUnlocked ?? 0)),
                snapshot?.TotalCommonPossible ?? 0,
                snapshot?.TotalUncommonPossible ?? 0,
                snapshot?.TotalRarePossible ?? 0,
                snapshot?.TotalUltraRarePossible ?? 0,
                L("LOCPlayAch_Rarity_Common", "Common"),
                L("LOCPlayAch_Rarity_Uncommon", "Uncommon"),
                L("LOCPlayAch_Rarity_Rare", "Rare"),
                L("LOCPlayAch_Rarity_UltraRare", "Ultra Rare"),
                L("LOCPlayAch_Common_Locked", "Locked"),
                PersistedSettings?.UseUniformRarityBadges ?? false);
        }

        private void ApplyTrophy(OverviewDataSnapshot snapshot)
        {
            var games = snapshot?.GameSummaries ?? new List<GameSummaryItem>();
            Chart.SetTrophyData(
                games.Sum(game => game?.TrophyPlatinumCount ?? 0),
                games.Sum(game => game?.TrophyGoldCount ?? 0),
                games.Sum(game => game?.TrophySilverCount ?? 0),
                games.Sum(game => game?.TrophyBronzeCount ?? 0),
                games.Sum(game => game?.TrophyPlatinumTotal ?? 0),
                games.Sum(game => game?.TrophyGoldTotal ?? 0),
                games.Sum(game => game?.TrophySilverTotal ?? 0),
                games.Sum(game => game?.TrophyBronzeTotal ?? 0),
                L("LOCPlayAch_Trophy_Platinum", "Platinum"),
                L("LOCPlayAch_Trophy_Gold", "Gold"),
                L("LOCPlayAch_Trophy_Silver", "Silver"),
                L("LOCPlayAch_Trophy_Bronze", "Bronze"),
                L("LOCPlayAch_Common_Locked", "Locked"));
        }

        private static Dictionary<string, int> BuildProviderUnlockedCounts(IEnumerable<GameSummaryItem> games)
        {
            return BuildProviderCounts(games, game => game?.UnlockedAchievements ?? 0);
        }

        private static Dictionary<string, int> BuildProviderTotalCounts(IEnumerable<GameSummaryItem> games)
        {
            return BuildProviderCounts(games, game => game?.TotalAchievements ?? 0);
        }

        private static Dictionary<string, int> BuildProviderCounts(
            IEnumerable<GameSummaryItem> games,
            Func<GameSummaryItem, int> selector)
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var game in games ?? Enumerable.Empty<GameSummaryItem>())
            {
                var providerKey = StartPageWidgetProjection.NormalizeProviderKey(game?.ProviderKey);
                counts[providerKey] = counts.TryGetValue(providerKey, out var current)
                    ? current + selector(game)
                    : selector(game);
            }

            return counts;
        }

        private static Dictionary<string, (string iconKey, string colorHex)> BuildProviderLookup(
            IEnumerable<GameSummaryItem> games)
        {
            var lookup = new Dictionary<string, (string iconKey, string colorHex)>(StringComparer.OrdinalIgnoreCase);
            foreach (var game in games ?? Enumerable.Empty<GameSummaryItem>())
            {
                var providerKey = StartPageWidgetProjection.NormalizeProviderKey(game?.ProviderKey);
                if (!lookup.ContainsKey(providerKey))
                {
                    lookup[providerKey] = (game?.ProviderIconKey ?? string.Empty, game?.ProviderColorHex ?? "#888888");
                }
            }

            return lookup;
        }

        private static Dictionary<string, string> BuildProviderDisplayNames(IEnumerable<GameSummaryItem> games)
        {
            var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var game in games ?? Enumerable.Empty<GameSummaryItem>())
            {
                var providerKey = StartPageWidgetProjection.NormalizeProviderKey(game?.ProviderKey);
                if (!names.ContainsKey(providerKey))
                {
                    names[providerKey] = string.IsNullOrWhiteSpace(game?.Provider)
                        ? providerKey
                        : game.Provider;
                }
            }

            return names;
        }

        private static string GetTitle(StartPageWidgetKind widgetKind)
        {
            return widgetKind switch
            {
                StartPageWidgetKind.CompletedGamesPie => L("LOCPlayAch_Overview_GamesPieChart", "Completed Games"),
                StartPageWidgetKind.ProviderPie => L("LOCPlayAch_Overview_ProviderDistribution", "Achievements by Platform"),
                StartPageWidgetKind.RarityPie => L("LOCPlayAch_Overview_RarityPieChart", "Achievements by Rarity"),
                StartPageWidgetKind.TrophyPie => L("LOCPlayAch_Overview_TrophyPieChart", "Achievements by Trophy"),
                _ => string.Empty
            };
        }

        private static string L(string key, string fallback)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return fallback;
            }

            var value = ResourceProvider.GetString(key);
            return string.IsNullOrWhiteSpace(value) || string.Equals(value, key, StringComparison.Ordinal)
                ? fallback
                : value;
        }
    }
}
