using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.Overview;
using PlayniteAchievements.Services.Search;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.ViewModels.Items;

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

            return ProjectFilteredGameSummaries(list, widgetSettings, rowLimit);
        }

        public static List<GameSummaryItem> ProjectFilteredGameSummaries(
            IEnumerable<GameSummaryItem> items,
            PersistedSettings settings,
            int? rowLimit = null)
        {
            var widgetSettings = settings?.StartPageGameSummariesGrid ?? new StartPageGameSummariesGridSettings();
            return ProjectFilteredGameSummaries(items, widgetSettings, rowLimit);
        }

        private static List<GameSummaryItem> ProjectFilteredGameSummaries(
            IEnumerable<GameSummaryItem> items,
            StartPageGameSummariesGridSettings widgetSettings,
            int? rowLimit)
        {
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

        public static List<AchievementDisplayItem> FilterRecentUnlocksBySearch(
            IEnumerable<AchievementDisplayItem> items,
            SearchTextIndex<AchievementDisplayItem> searchIndex,
            string searchText)
        {
            var list = (items ?? Enumerable.Empty<AchievementDisplayItem>())
                .Where(item => item != null)
                .ToList();
            var query = SearchQuery.From(searchText);
            if (!query.HasValue)
            {
                return list;
            }

            var index = searchIndex ?? new SearchTextIndex<AchievementDisplayItem>(item =>
                SearchTextBuilder.ForRecentAchievement(item?.GameName, item?.DisplayName));
            index.Rebuild(list);
            return list.Where(item => index.Matches(item, query)).ToList();
        }

        public static List<FriendAchievementDisplayItem> ProjectFriendRecentUnlocks(
            IEnumerable<FriendAchievementDisplayItem> items,
            PersistedSettings settings,
            int? rowLimit = null,
            PlayniteAchievementsSettings appearanceSettings = null)
        {
            var widgetSettings = settings?.StartPageFriendsRecentUnlocksGrid ??
                new StartPageFriendsRecentUnlocksGridSettings();

            var list = (items ?? Enumerable.Empty<FriendAchievementDisplayItem>())
                .Where(item => item != null)
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

            var limited = DisplayGridRowLimitHelper.Limit(
                list,
                rowLimit ?? widgetSettings.MaxRows);

            var appearanceByGameId = appearanceSettings != null
                ? new Dictionary<Guid?, AchievementDisplayItem.AppearanceSettingsSnapshot>()
                : null;

            return limited
                .Select(item => CloneFriendAchievement(item, appearanceSettings, appearanceByGameId))
                .Where(item => item != null)
                .ToList();
        }

        public static List<FriendAchievementDisplayItem> FilterFriendRecentUnlocksBySearch(
            IEnumerable<FriendAchievementDisplayItem> items,
            SearchTextIndex<FriendAchievementDisplayItem> searchIndex,
            string searchText)
        {
            var list = (items ?? Enumerable.Empty<FriendAchievementDisplayItem>())
                .Where(item => item != null)
                .ToList();
            var query = SearchQuery.From(searchText);
            if (!query.HasValue)
            {
                return list;
            }

            var index = searchIndex ?? new SearchTextIndex<FriendAchievementDisplayItem>(item =>
                SearchTextBuilder.FromValues(item?.GameName, item?.FriendName, item?.DisplayName));
            index.Rebuild(list);
            return list.Where(item => index.Matches(item, query)).ToList();
        }

        private static FriendAchievementDisplayItem CloneFriendAchievement(
            FriendAchievementDisplayItem item,
            PlayniteAchievementsSettings appearanceSettings,
            IDictionary<Guid?, AchievementDisplayItem.AppearanceSettingsSnapshot> appearanceByGameId)
        {
            if (item == null)
            {
                return null;
            }

            var clone = new FriendAchievementDisplayItem();
            clone.UpdateFrom(
                item.Source,
                item.GameName,
                item.PlayniteGameId,
                item.ShowHiddenIcon,
                item.ShowHiddenTitle,
                item.ShowHiddenDescription,
                item.ShowHiddenSuffix,
                item.ShowLockedIcon,
                item.UseSeparateLockedIconsWhenAvailable,
                item.ShowRarityBar,
                item.SortingName,
                item.GameIconPath,
                item.GameCoverPath,
                item.CategoryOrderIndex,
                item.CategoryArtPath);
            clone.FriendName = item.FriendName;
            clone.FriendExternalUserId = item.FriendExternalUserId;
            clone.FriendAvatarPath = item.FriendAvatarPath;
            clone.ProviderKey = item.ProviderKey;
            clone.PointsValue = item.PointsValue;
            clone.CategoryType = item.CategoryType;
            clone.CategoryLabel = item.CategoryLabel;
            clone.IsRevealed = item.IsRevealed;
            clone.AppId = item.AppId;
            clone.ProviderGameKey = item.ProviderGameKey;
            clone.FriendGroupId = item.FriendGroupId;
            clone.UnlockedBySelf = item.UnlockedBySelf;
            clone.ShowFriendSpoilers = item.ShowFriendSpoilers;
            clone.SetDynamicAchievementsGameCommand = item.SetDynamicAchievementsGameCommand;
            clone.FilterDynamicLibraryAchievementsByProviderCommand = item.FilterDynamicLibraryAchievementsByProviderCommand;
            clone.OpenViewAchievementsWindow = item.OpenViewAchievementsWindow;
            clone.OpenManageAchievementsWindow = item.OpenManageAchievementsWindow;
            clone.SetDynamicFriendScopeProviderCommand = item.SetDynamicFriendScopeProviderCommand;
            clone.SetDynamicFriendScopeUserCommand = item.SetDynamicFriendScopeUserCommand;
            clone.SetDynamicFriendScopeGameCommand = item.SetDynamicFriendScopeGameCommand;

            if (appearanceSettings != null)
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
        }

        public static string NormalizeProviderKey(string providerKey)
        {
            return string.IsNullOrWhiteSpace(providerKey)
                ? "Unknown"
                : providerKey.Trim();
        }

    }
}
