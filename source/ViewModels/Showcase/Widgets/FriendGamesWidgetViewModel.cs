using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using Playnite.SDK;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.Database;
using PlayniteAchievements.Services.Friends;
using PlayniteAchievements.Services.Showcase;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.ViewModels.Showcase.Widgets
{
    /// <summary>
    /// The friends' games widget: the games the picked friends have unlocks in or own, with
    /// their unlocks, most recent friend unlock first until the grid sorts otherwise.
    /// </summary>
    public sealed class FriendGamesWidgetViewModel
        : ShowcaseGridWidgetViewModelBase<GameSummaryItem>, IDisposable
    {
        private static readonly ILogger Logger = LogManager.GetLogger();

        // The widget instance's shared adapter, so the filters survive view model swaps.
        private readonly ShowcaseControlBarSlot<FriendGamesControlBarAdapter> _controlBarSlot;
        private readonly FriendWidgetLists _lists;

        // Several friends' games count each achievement once, which the shared lists' per-friend
        // totals cannot; their unlocks are read here.
        private LiveQuery<IReadOnlyList<AchievementTotalsRecord>> _totals;
        private string _totalsKey;

        public FriendGamesWidgetViewModel()
        {
            _controlBarSlot = new ShowcaseControlBarSlot<FriendGamesControlBarAdapter>(RefreshItems);
            _lists = new FriendWidgetLists(RefreshItems);
        }

        protected override string BaseSurfaceKey => ShowcaseGridSurfaces.FriendGames;

        protected override IEnumerable<GameSummaryItem> SelectItems(ShowcaseWidgetProjection projection)
        {
            var bar = Bar();
            var lists = _lists.Current;
            if (lists == null)
            {
                return Array.Empty<GameSummaryItem>();
            }

            IEnumerable<GameSummaryItem> games = GamesOf(lists, projection?.Instance, bar.FavoritesOnly);
            var search = bar.SearchText?.Trim();
            if (!string.IsNullOrEmpty(search))
            {
                games = games.Where(game =>
                    (game?.GameName ?? string.Empty).IndexOf(search, StringComparison.CurrentCultureIgnoreCase) >= 0);
            }

            return games;
        }

        private IReadOnlyList<GameSummaryItem> GamesOf(
            FriendsOverviewLists lists,
            ShowcaseWidgetInstanceSettings instance,
            bool favoritesOnly)
        {
            var accountKeys = ShowcaseWidgetOptions.GetFriendAccountKeys(instance);
            if (FriendWidgetLists.IsEveryFriend(accountKeys, favoritesOnly))
            {
                ReleaseTotals();
                return lists.Games;
            }

            var friends = FriendWidgetLists.ScopeFriends(lists, accountKeys, favoritesOnly);
            if (friends.Count <= 1)
            {
                ReleaseTotals();
                return friends.Count == 0 ? Array.Empty<GameSummaryItem>() : lists.GamesOf(friends[0]);
            }

            var accounts = friends.SelectMany(FriendsOverviewLists.AccountsOf).ToList();
            var totals = ReadTotals(accounts);
            return totals == null ? Array.Empty<GameSummaryItem>() : lists.GamesOf(accounts, totals);
        }

        // The friends' unlocked achievements per game, each counted once; null until read.
        private IReadOnlyList<AchievementTotalsRecord> ReadTotals(IReadOnlyList<FriendAccountRef> accounts)
        {
            var source = _lists.Source;
            var reads = source?.Reads;
            var dispatcher = Application.Current?.Dispatcher;
            if (reads == null || dispatcher == null)
            {
                return null;
            }

            var query = AchievementQuery.AllFriends
                .ForFriendAccounts(accounts)
                .WhereUnlocked(true)
                .CountDistinctAchievements();
            var key = AchievementQuerySql.KeyOf(query);
            if (_totals == null)
            {
                _totals = new LiveQuery<IReadOnlyList<AchievementTotalsRecord>>(
                    reads.Feed,
                    query,
                    read => reads.Totals(read, AchievementGroup.ProviderGame),
                    new DispatcherReadScheduler(dispatcher),
                    source.Window,
                    ex => Logger.Warn(ex, "Failed to read a friends' games widget's games."));
                _totals.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(LiveQuery<object>.Result))
                    {
                        RefreshItems();
                    }
                };
            }
            else if (!string.Equals(key, _totalsKey, StringComparison.Ordinal))
            {
                _totals.SetQuery(query);
            }

            _totalsKey = key;
            return _totals.Result;
        }

        private void ReleaseTotals()
        {
            _totals?.Dispose();
            _totals = null;
            _totalsKey = null;
        }

        protected override IEnumerable<GameSummaryItem> OrderItems(IEnumerable<GameSummaryItem> items)
        {
            var list = (items ?? Enumerable.Empty<GameSummaryItem>()).Where(item => item != null).ToList();
            var options = GridOptions as GameSummaryGridOptions;
            GameSummariesSortHelper.SortByColumn(
                list,
                options?.SortColumn ?? GridSortColumns.FromGameSummariesMode(GameSummariesSortMode.RecentUnlock),
                options?.SortDescending == false ? ListSortDirection.Ascending : ListSortDirection.Descending);
            return list;
        }

        protected override bool ShouldRefreshItemsFor(string propertyName)
        {
            return base.ShouldRefreshItemsFor(propertyName) ||
                propertyName == nameof(GameSummaryGridOptions.SortColumn) ||
                propertyName == nameof(GameSummaryGridOptions.SortDescending);
        }

        private FriendGamesControlBarAdapter Bar()
        {
            _controlBarSlot.Bind(Projection?.Instance?.InstanceId);
            _controlBarSlot.Adapter.EnableSort();
            _controlBarSlot.UpdateShownBar(Projection?.Instance, LegacyShowControlBar);
            ControlBar = _controlBarSlot.ShownBar;
            return _controlBarSlot.Adapter;
        }

        public void Dispose()
        {
            ReleaseTotals();
            _lists.Dispose();
        }
    }
}
