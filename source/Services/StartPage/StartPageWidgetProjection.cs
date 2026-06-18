using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Overview;
using PlayniteAchievements.ViewModels;

namespace PlayniteAchievements.Services.StartPage
{
    public static class StartPageWidgetProjection
    {
        public const int DefaultGridRowLimit = 25;

        public static List<GameSummaryItem> ProjectGameSummaries(
            IEnumerable<GameSummaryItem> items,
            PersistedSettings settings,
            int? rowLimit = null)
        {
            var widgetSettings = settings?.StartPageGameSummariesGrid ?? new StartPageGameSummariesGridSettings();
            var list = FilterGameSummariesForStartPage(items, settings, includeProgressScope: true);

            GameSummariesSortHelper.Sort(
                list,
                widgetSettings.SortMode,
                widgetSettings.SortDescending
                    ? ListSortDirection.Descending
                    : ListSortDirection.Ascending);

            return DisplayGridRowLimitHelper.Limit(
                list,
                rowLimit ?? widgetSettings.MaxRows);
        }

        public static List<GameSummaryItem> FilterGameSummariesForStartPage(
            IEnumerable<GameSummaryItem> items,
            PersistedSettings settings,
            bool includeProgressScope)
        {
            var activityScope = settings?.StartPageActivityScope ??
                PersistedSettings.DefaultStartPageActivityScope;
            var progressScope = includeProgressScope
                ? settings?.StartPageProgressScope ?? PersistedSettings.DefaultStartPageProgressScope
                : GameProgressScope.None;

            return OverviewGameSummaryFilters.ApplyActivityAndProgressFilters(
                    (items ?? Enumerable.Empty<GameSummaryItem>()).Where(item => item != null),
                    activityScope,
                    progressScope)
                .ToList();
        }

        public static List<AchievementDisplayItem> ProjectRecentUnlocks(
            IEnumerable<AchievementDisplayItem> items,
            PersistedSettings settings,
            int? rowLimit = null,
            PlayniteAchievementsSettings appearanceSettings = null)
        {
            var widgetSettings = settings?.StartPageRecentUnlocksGrid ?? new StartPageRecentUnlocksGridSettings();

            // The source items carry the appearance flags (hidden suffix, hidden title/icon, etc.)
            // captured when the cached snapshot was built. Re-apply the current appearance settings
            // to each clone so a later toggle of a display setting takes effect on the start page;
            // without this the cached snapshot would keep showing the stale values. Snapshots are
            // resolved per game to avoid recomputing per-game appearance for every row.
            var appearanceByGameId = appearanceSettings != null
                ? new Dictionary<Guid?, AchievementDisplayItem.AppearanceSettingsSnapshot>()
                : null;

            var list = (items ?? Enumerable.Empty<AchievementDisplayItem>())
                .Where(item => item != null)
                .Select(item =>
                {
                    var clone = item.Clone();
                    if (appearanceByGameId != null)
                    {
                        if (!appearanceByGameId.TryGetValue(clone.PlayniteGameId, out var snapshot))
                        {
                            snapshot = AchievementDisplayItem.CreateAppearanceSettingsSnapshot(
                                appearanceSettings,
                                clone.PlayniteGameId,
                                null);
                            appearanceByGameId[clone.PlayniteGameId] = snapshot;
                        }

                        clone.ApplyAppearanceSettings(snapshot);
                    }

                    return clone;
                })
                .ToList();

            var sort = new AchievementSortSpec(
                widgetSettings.SortMode,
                widgetSettings.SortDescending
                    ? ListSortDirection.Descending
                    : ListSortDirection.Ascending);

            if (!sort.PreservesSourceOrder)
            {
                var stableOrder = AchievementSortHelper.CreateStableOrderMap(list);
                var comparison = AchievementSortHelper.GetComparison(
                    sort.SortMemberPath,
                    sort.Direction,
                    AchievementSortScope.RecentAchievements);
                if (comparison != null)
                {
                    list.Sort(AchievementSortHelper.WithStableOrder(comparison, stableOrder));
                }
            }

            return DisplayGridRowLimitHelper.Limit(
                list,
                rowLimit ?? widgetSettings.MaxRows);
        }

        public static string NormalizeProviderKey(string providerKey)
        {
            return string.IsNullOrWhiteSpace(providerKey)
                ? "Unknown"
                : providerKey.Trim();
        }

    }
}
