using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Friends;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.Database;
using PlayniteAchievements.Services.Friends;
using PlayniteAchievements.Services.Showcase;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.ViewModels.Showcase.Widgets
{
    /// <summary>
    /// The friend unlocks widget: the picked friends' unlocks, newest first until the grid sorts
    /// otherwise, each with whether the user has it too.
    /// </summary>
    public sealed class FriendUnlocksWidgetViewModel
        : FriendGridWidgetViewModelBase<AchievementDisplayItem, FriendUnlocksControlBarAdapter>
    {
        private ShowcaseRows _rows;

        // Read off the UI thread with each row read; replaced, never changed.
        private volatile RowDecoration _decoration = RowDecoration.None;

        protected override string BaseSurfaceKey => ShowcaseGridSurfaces.FriendAchievements;

        internal override IEnumerable<AchievementDisplayItem> SelectFriendItems(
            ShowcaseWidgetProjection projection,
            FriendsOverviewLists lists,
            FriendUnlocksControlBarAdapter bar)
        {
            if (Lists.Source?.Reads == null)
            {
                return Array.Empty<AchievementDisplayItem>();
            }

            var accountKeys = ShowcaseWidgetOptions.GetFriendAccountKeys(projection?.Instance);
            var query = AchievementQuery.AllFriends
                .WhereUnlocked(true)
                .WhereUnlockTimeKnown()
                .WithFriendNames(lists.FriendNames);
            if (!FriendWidgetLists.IsEveryFriend(accountKeys, bar.FavoritesOnly))
            {
                query = query.ForFriendAccounts(FriendWidgetLists
                    .ScopeFriends(lists, accountKeys, bar.FavoritesOnly)
                    .SelectMany(FriendsOverviewLists.AccountsOf)
                    .ToList());
            }

            if (!string.IsNullOrWhiteSpace(bar.SearchText))
            {
                query = query.WhereSearch(bar.SearchText.Trim());
            }

            var spec = SortSpec();
            query = !spec.PreservesSourceOrder && AchievementQuery.SortOfColumn(spec.SortMemberPath) is AchievementSort sort
                ? query.OrderBy(sort, spec.Direction == ListSortDirection.Descending)
                : query.OrderBy(AchievementSort.UnlockTime, descending: true);
            var cap = PersistedSettings.NormalizeGridMaxRows((GridOptions as GridCommonOptions)?.MaxRows);

            _decoration = new RowDecoration(lists, projection?.Snapshot?.CurrentUserIdentities);
            _rows ??= new ShowcaseRows(RefreshItems, () => Lists.Source?.Reads?.Feed, LoadRows);
            return _rows.For(cap.HasValue ? query.Page(cap.Value) : query);
        }

        // Runs off the UI thread.
        private IReadOnlyList<AchievementDisplayItem> LoadRows(AchievementQuery query)
        {
            var reads = Lists.Source?.Reads;
            if (reads == null)
            {
                return Array.Empty<AchievementDisplayItem>();
            }

            var rows = reads.Rows(query);
            _decoration.Apply(rows);
            return rows;
        }

        private AchievementSortSpec SortSpec()
        {
            var options = GridOptions as AchievementGridOptions;
            return new AchievementSortSpec(
                options?.SortColumn ?? GridSortColumns.SourceOrder,
                options?.SortDescending == false
                    ? ListSortDirection.Ascending
                    : ListSortDirection.Descending);
        }

        protected override bool ShouldRefreshItemsFor(string propertyName)
        {
            return base.ShouldRefreshItemsFor(propertyName) ||
                propertyName == nameof(AchievementGridOptions.SortColumn) ||
                propertyName == nameof(AchievementGridOptions.SortDescending);
        }

        public override void Dispose()
        {
            _rows?.Dispose();
            base.Dispose();
        }

        /// <summary>
        /// What each read row is given beyond its columns: the friend's merged identity, and the
        /// user's own state of the achievement as the comparison columns.
        /// </summary>
        private sealed class RowDecoration
        {
            public static readonly RowDecoration None = new RowDecoration(null, null);

            private readonly FriendsOverviewLists _lists;
            private readonly Dictionary<string, FriendIdentity> _self;

            public RowDecoration(FriendsOverviewLists lists, IEnumerable<FriendIdentity> currentUsers)
            {
                _lists = lists;
                _self = new Dictionary<string, FriendIdentity>(StringComparer.OrdinalIgnoreCase);
                foreach (var identity in currentUsers ?? Enumerable.Empty<FriendIdentity>())
                {
                    if (!string.IsNullOrWhiteSpace(identity?.ProviderKey))
                    {
                        _self[identity.ProviderKey] = identity;
                    }
                }
            }

            public void Apply(IReadOnlyList<FriendAchievementDisplayItem> rows)
            {
                _lists?.StampIdentity(rows, PlayniteAchievementsPlugin.Instance?.Settings?.Persisted);
                foreach (var row in rows ?? Array.Empty<FriendAchievementDisplayItem>())
                {
                    _self.TryGetValue(row.ProviderKey ?? string.Empty, out var self);
                    // A game the user has no progress for reads N/A, not Locked.
                    row.ApplyComparison(
                        FriendSelfName.Of(self),
                        self?.AvatarPath,
                        row.SelfUnlockTimeUtc,
                        row.UnlockedBySelf,
                        row.FriendName,
                        row.FriendAvatarPath,
                        row.SelfHasGameData);
                }
            }
        }
    }
}
