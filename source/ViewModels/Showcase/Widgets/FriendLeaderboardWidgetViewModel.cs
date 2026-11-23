using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.Overview;
using PlayniteAchievements.Services.Showcase;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.ViewModels.Showcase.Widgets
{
    /// <summary>
    /// The friends leaderboard: the picked friends and the user, ranked by the grid's sort. With
    /// one friend picked it compares that friend with the user.
    /// </summary>
    public sealed class FriendLeaderboardWidgetViewModel
        : ShowcaseGridWidgetViewModelBase<FriendSummaryItem>, IDisposable
    {
        // The widget instance's shared adapter, so the filters survive view model swaps.
        private readonly ShowcaseControlBarSlot<FriendLeaderboardControlBarAdapter> _controlBarSlot;
        private readonly FriendWidgetLists _lists;

        // One row for the user, updated in place, so a new snapshot does not replace the row.
        private readonly FriendSummaryItem _self = new FriendSummaryItem();

        public FriendLeaderboardWidgetViewModel()
        {
            _controlBarSlot = new ShowcaseControlBarSlot<FriendLeaderboardControlBarAdapter>(RefreshItems);
            _lists = new FriendWidgetLists(RefreshItems);
        }

        protected override string BaseSurfaceKey => ShowcaseGridSurfaces.FriendSummaries;

        // Each read of the friend data builds new rows.
        protected override bool ReplacesRowsInPlace => true;

        protected override IEnumerable<FriendSummaryItem> SelectItems(ShowcaseWidgetProjection projection)
        {
            var bar = Bar();
            var lists = _lists.Current;
            if (lists == null)
            {
                return Array.Empty<FriendSummaryItem>();
            }

            var friends = FriendWidgetLists.ScopeFriends(
                lists,
                ShowcaseWidgetOptions.GetFriendAccountKeys(projection?.Instance),
                bar.FavoritesOnly);
            var search = bar.SearchText?.Trim();
            if (!string.IsNullOrEmpty(search))
            {
                friends = friends
                    .Where(friend => (friend.DisplayName ?? string.Empty).IndexOf(search, StringComparison.CurrentCultureIgnoreCase) >= 0)
                    .ToList();
            }

            UpdateSelf(projection?.Snapshot);
            return friends.Concat(new[] { _self });
        }

        protected override IEnumerable<FriendSummaryItem> OrderItems(IEnumerable<FriendSummaryItem> items)
        {
            var list = (items ?? Enumerable.Empty<FriendSummaryItem>()).Where(item => item != null).ToList();
            var options = GridOptions as FriendSummaryGridOptions;
            var spec = new FriendSummariesSortSpec(
                options?.SortMode ?? FriendSummariesSortMode.CollectionScore,
                options?.SortDescending == false ? ListSortDirection.Ascending : ListSortDirection.Descending);
            FriendSummarySortHelper.SortRanked(list, spec.SortMemberPath, spec.Direction);
            return list;
        }

        protected override bool ShouldRefreshItemsFor(string propertyName)
        {
            return base.ShouldRefreshItemsFor(propertyName) ||
                propertyName == nameof(FriendSummaryGridOptions.SortMode) ||
                propertyName == nameof(FriendSummaryGridOptions.SortDescending);
        }

        private FriendLeaderboardControlBarAdapter Bar()
        {
            _controlBarSlot.Bind(Projection?.Instance?.InstanceId);
            _controlBarSlot.Adapter.EnableSort();
            _controlBarSlot.UpdateShownBar(Projection?.Instance, LegacyShowControlBar);
            ControlBar = _controlBarSlot.ShownBar;
            return _controlBarSlot.Adapter;
        }

        // The user's row: their library's totals, under their account name and avatar.
        private void UpdateSelf(OverviewDataSnapshot snapshot)
        {
            if (snapshot == null)
            {
                return;
            }

            var identity = snapshot.CurrentUserIdentities?.FirstOrDefault(candidate =>
                !string.IsNullOrWhiteSpace(candidate?.AvatarPath)) ?? snapshot.CurrentUserIdentities?.FirstOrDefault();
            _self.DisplayName = FriendSelfName.Of(null);
            _self.AvatarPath = identity?.AvatarPath;
            _self.SharedGamesCount = snapshot.TotalGames;
            _self.GamesWithUnlocksCount = snapshot.GameSummaries?.Count(game => game?.UnlockedAchievements > 0) ?? 0;
            _self.CompletedGamesCount = snapshot.CompletedGames;
            _self.UnlockedAchievementsCount = snapshot.TotalUnlocked;
            _self.CollectionScore = snapshot.CollectorScore;
            _self.PrestigeScore = snapshot.PrestigeScore;
            _self.CollectionLevel = snapshot.CollectorLevel;
            _self.PrestigeLevel = snapshot.PrestigeLevel;
            _self.CommonCount = snapshot.TotalCommon;
            _self.UncommonCount = snapshot.TotalUncommon;
            _self.RareCount = snapshot.TotalRare;
            _self.UltraRareCount = snapshot.TotalUltraRare;
            _self.TrophyPlatinumCount = snapshot.TotalPlatinum;
            _self.TrophyGoldCount = snapshot.TotalGold;
            _self.TrophySilverCount = snapshot.TotalSilver;
            _self.TrophyBronzeCount = snapshot.TotalBronze;
            _self.LastUnlockUtc = snapshot.GameSummaries?
                .Select(game => game?.LastUnlockUtc)
                .Where(time => time.HasValue)
                .DefaultIfEmpty()
                .Max();
        }

        public void Dispose() => _lists.Dispose();
    }
}
