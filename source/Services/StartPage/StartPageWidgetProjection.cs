using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using PlayniteAchievements.Models.Settings;
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
            var list = (items ?? Enumerable.Empty<GameSummaryItem>())
                .Where(item => item != null)
                .ToList();

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

        public static List<AchievementDisplayItem> ProjectRecentUnlocks(
            IEnumerable<AchievementDisplayItem> items,
            PersistedSettings settings,
            int? rowLimit = null)
        {
            var widgetSettings = settings?.StartPageRecentUnlocksGrid ?? new StartPageRecentUnlocksGridSettings();
            var list = (items ?? Enumerable.Empty<AchievementDisplayItem>())
                .Where(item => item != null)
                .Select(item => item.Clone())
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
