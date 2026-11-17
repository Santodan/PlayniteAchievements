using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Playnite.SDK;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Friends;
using PlayniteAchievements.Services.Friends;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.ViewModels
{
    /// <summary>
    /// Compare-friend selection for a grid of the user's own achievements across games (the
    /// Overview achievements grid). The dropdown lists every friend; picking one loads that
    /// friend's unlocked rows for all their games and applies their unlock state onto the listed
    /// items. A game the friend neither owns nor has an unlock in reads N/A. Only the picked
    /// friend's rows are held, never the whole friends data set.
    /// </summary>
    public sealed class LibraryFriendCompareController :
        FriendCompareControllerBase<AchievementDisplayItem>, IGridCompareSource
    {
        private readonly IFriendCacheManager _friendCache;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly ILogger _logger;

        private List<FriendSummaryItem> _friends = new List<FriendSummaryItem>();
        private FriendSummaryItem _selected;
        private Dictionary<string, FriendAchievementDisplayItem> _selectedRows =
            new Dictionary<string, FriendAchievementDisplayItem>(StringComparer.OrdinalIgnoreCase);
        // The games the picked friend has anything for: an unlock or an ownership link.
        private HashSet<Guid> _selectedGames = new HashSet<Guid>();
        private bool _selectedRowsLoaded;
        private Dictionary<string, FriendIdentity> _currentUsersByProvider =
            new Dictionary<string, FriendIdentity>(StringComparer.OrdinalIgnoreCase);
        private IReadOnlyList<AchievementDisplayItem> _targetItems;
        private int _friendsLoadVersion;
        private int _rowsLoadVersion;

        internal LibraryFriendCompareController(
            IFriendCacheManager friendCache,
            PlayniteAchievementsSettings settings,
            ILogger logger)
        {
            _friendCache = friendCache;
            _settings = settings;
            _logger = logger;
        }

        /// <summary>The last friend list or row load, for callers that wait on it.</summary>
        internal Task LoadTask { get; private set; } = Task.CompletedTask;

        // A remembered friend keeps the dropdown shown and selected while they are not listed.
        public bool IsCompareAvailable => _friends.Count > 0 || RememberedKey != null;

        public string CompareSelectionText => _selected?.DisplayName
            ?? (RememberedKey != null ? RememberedName : null)
            ?? ResourceProvider.GetString("LOCPlayAch_Filter_CompareSelectorPlaceholder");

        public IEnumerable<string> OptionKeys
        {
            get
            {
                var keys = _friends.Select(FriendOverviewProjection.GetFriendScopeKey);
                return RememberedKey != null && FindFriend(RememberedKey) == null
                    ? keys.Concat(new[] { RememberedKey })
                    : keys;
            }
        }

        public bool IsKeySelected(string key)
        {
            return _selected != null ? MatchesKey(_selected, key) : IsRememberedKey(key);
        }

        public string GetDisplayNameForKey(string key)
        {
            return FindFriend(key)?.DisplayName
                   ?? (IsRememberedKey(key) ? RememberedName : null)
                   ?? key;
        }

        public bool IsKeyFavorite(string key)
        {
            return FindFriend(key)?.IsFavorite == true;
        }

        // Single-select semantics over checkable menu items: checking a friend replaces any
        // other selection; unchecking the selected friend clears the comparison.
        public void SelectKey(string key, bool isSelected)
        {
            if (!isSelected)
            {
                if (IsKeySelected(key))
                {
                    Select(null);
                    Remember(null, null);
                    NotifyCompareStateChanged();
                }

                return;
            }

            Select(FindFriend(key));
            Remember(
                _selected != null ? FriendOverviewProjection.GetFriendScopeKey(_selected) : null,
                _selected?.DisplayName);
        }

        /// <summary>
        /// Loads the friend list off-thread, and with it the remembered friend's rows. Call when
        /// the grid's data loads, so a friend refresh since the last load is picked up.
        /// </summary>
        public void Refresh()
        {
            var version = Interlocked.Increment(ref _friendsLoadVersion);
            if (_friendCache == null || _settings?.Persisted?.EnableFriendsFeatures != true)
            {
                ApplyFriends(version, new List<FriendSummaryItem>(), _currentUsersByProvider);
                return;
            }

            LoadTask = Task.Run(() =>
            {
                var friends = new List<FriendSummaryItem>();
                try
                {
                    // No scopes: the friend list alone, without any achievement rows.
                    var data = _friendCache.LoadFriendsOverviewPatchData(null);
                    friends = new FriendOverviewProjection(data, _settings?.Persisted).Friends
                        .Where(friend => friend != null &&
                                         !FriendOverviewProjection.IsAllScope(
                                             FriendOverviewProjection.GetFriendScopeKey(friend)))
                        .OrderByDescending(friend => friend.IsFavorite)
                        .ThenBy(friend => friend.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                        .ToList();
                }
                catch (Exception ex)
                {
                    _logger?.Error(ex, "Failed to load friends for the library compare.");
                }

                var currentUsers = LoadIdentitiesByProvider(
                    () => _friendCache.LoadCurrentUserIdentities(),
                    _logger);
                RunOnUi(() => ApplyFriends(version, friends, currentUsers));
            });
        }

        /// <summary>Sets the items the comparison is drawn on and applies the current pick.</summary>
        public void SetTargetItems(IReadOnlyList<AchievementDisplayItem> targetItems)
        {
            _targetItems = targetItems;
            ApplySelection();
        }

        /// <summary>
        /// Drops the picked friend's rows and the comparison on the listed items. The remembered
        /// pick stays, and the next <see cref="Refresh"/> loads it again.
        /// </summary>
        public void Release()
        {
            Interlocked.Increment(ref _friendsLoadVersion);
            Interlocked.Increment(ref _rowsLoadVersion);
            ClearApplied();
            _targetItems = null;
            _selected = null;
            _selectedRows = new Dictionary<string, FriendAchievementDisplayItem>(StringComparer.OrdinalIgnoreCase);
            _selectedGames = new HashSet<Guid>();
            _selectedRowsLoaded = false;
            NotifyCompareStateChanged();
        }

        private protected override bool TryRestoreRememberedSelection()
        {
            var key = RememberedKey;
            if (key == null || _selected != null)
            {
                return false;
            }

            var friend = FindFriend(key);
            if (friend == null)
            {
                return false;
            }

            Select(friend);
            return true;
        }

        private void ApplyFriends(
            int version,
            List<FriendSummaryItem> friends,
            Dictionary<string, FriendIdentity> currentUsers)
        {
            if (version != Volatile.Read(ref _friendsLoadVersion))
            {
                return;
            }

            _friends = friends;
            _currentUsersByProvider = currentUsers;

            // The friend rows are new instances; keep the pick when the friend is still listed,
            // and load their rows again so a refresh of that friend shows.
            var selectedKey = _selected != null ? FriendOverviewProjection.GetFriendScopeKey(_selected) : null;
            _selected = null;
            OnPropertyChanged(nameof(OptionKeys));
            NotifyCompareStateChanged();

            var friend = selectedKey != null ? FindFriend(selectedKey) : null;
            if (friend != null)
            {
                Select(friend);
            }
            else if (!TryRestoreRememberedSelection())
            {
                Select(null);
            }
        }

        private void Select(FriendSummaryItem friend)
        {
            var next = friend != null && _friends.Contains(friend) ? friend : null;
            var version = Interlocked.Increment(ref _rowsLoadVersion);
            _selected = next;
            _selectedRows = new Dictionary<string, FriendAchievementDisplayItem>(StringComparer.OrdinalIgnoreCase);
            _selectedGames = new HashSet<Guid>();
            _selectedRowsLoaded = false;
            NotifyCompareStateChanged();
            ApplySelection();
            if (next == null)
            {
                return;
            }

            // One scope per account: a merged friend's rows come from each of its accounts.
            var accounts = next.MemberAccounts != null && next.MemberAccounts.Count > 0
                ? next.MemberAccounts
                    .Where(account => account != null)
                    .Select(account => FriendCacheChange.ForFriendOwnership(account.ProviderKey, account.ExternalUserId))
                    .ToList()
                : new List<FriendCacheChange>
                {
                    FriendCacheChange.ForFriendOwnership(next.ProviderKey, next.ExternalUserId)
                };

            LoadTask = Task.Run(() =>
            {
                var rows = new List<FriendAchievementDisplayItem>();
                var links = new List<FriendGameLinkItem>();
                try
                {
                    var data = _friendCache.LoadFriendsOverviewPatchData(accounts);
                    rows = data?.AllAchievements ?? rows;
                    links = data?.FriendGameLinks ?? links;
                }
                catch (Exception ex)
                {
                    _logger?.Error(ex, "Failed to load a friend's rows for the library compare.");
                }

                // The index is built here, off the UI thread; a row with no library game cannot
                // match a listed item.
                var index = BuildBestRowsByApiName(
                    rows,
                    row => row.PlayniteGameId.HasValue && row.PlayniteGameId.Value != Guid.Empty,
                    row => BuildKey(row.PlayniteGameId.Value, row.ApiName));
                var games = new HashSet<Guid>(index.Values.Select(row => row.PlayniteGameId.Value));

                // A game the friend owns with nothing unlocked reads Locked rather than N/A.
                var accountKeys = new HashSet<string>(
                    accounts.Select(account => FriendOverviewProjection.BuildFriendKey(
                        account.ProviderKey,
                        account.ExternalUserId)),
                    StringComparer.OrdinalIgnoreCase);
                foreach (var link in links)
                {
                    if (link?.PlayniteGameId != null &&
                        link.OwnershipConfirmed &&
                        link.PlayniteGameId.Value != Guid.Empty &&
                        accountKeys.Contains(FriendOverviewProjection.BuildFriendKey(
                            link.ProviderKey,
                            link.ExternalUserId) ?? string.Empty))
                    {
                        games.Add(link.PlayniteGameId.Value);
                    }
                }

                RunOnUi(() =>
                {
                    if (version != Volatile.Read(ref _rowsLoadVersion))
                    {
                        return;
                    }

                    _selectedRows = index;
                    _selectedGames = games;
                    _selectedRowsLoaded = true;
                    ApplySelection();
                });
            });
        }

        private void ApplySelection()
        {
            ClearApplied();
            var items = _targetItems;
            // Nothing is drawn while the friend's rows load; every row would read N/A until then.
            if (_selected == null || items == null || items.Count == 0 || !_selectedRowsLoaded)
            {
                return;
            }

            foreach (var item in items)
            {
                if (item == null)
                {
                    continue;
                }

                // A game the friend has nothing for reads N/A, not Locked.
                var gameId = item.PlayniteGameId ?? Guid.Empty;
                var applicable = _selectedGames.Contains(gameId);
                FriendAchievementDisplayItem compareRow = null;
                if (applicable && !string.IsNullOrWhiteSpace(item.ApiName))
                {
                    _selectedRows.TryGetValue(BuildKey(gameId, item.ApiName), out compareRow);
                }

                // The own-row identity follows the achievement's provider.
                FriendIdentity self = null;
                if (!string.IsNullOrWhiteSpace(item.ProviderKey))
                {
                    _currentUsersByProvider.TryGetValue(item.ProviderKey, out self);
                }

                item.ApplyComparison(
                    _selected.DisplayName,
                    _selected.AvatarPath ?? compareRow?.FriendAvatarPath,
                    compareRow?.UnlockTimeUtc,
                    compareRow?.Unlocked == true,
                    self?.DisplayName,
                    self?.AvatarPath,
                    applicable);
                AppliedItems.Add(item);
            }
        }

        private static string BuildKey(Guid gameId, string apiName)
        {
            return gameId.ToString("N") + "|" + apiName;
        }

        private FriendSummaryItem FindFriend(string key)
        {
            return _friends.FirstOrDefault(friend => MatchesKey(friend, key));
        }

        private static bool MatchesKey(FriendSummaryItem friend, string key)
        {
            return friend != null && string.Equals(
                FriendOverviewProjection.GetFriendScopeKey(friend),
                key,
                StringComparison.OrdinalIgnoreCase);
        }

        private static void RunOnUi(Action action)
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher != null)
            {
                dispatcher.InvokeIfNeeded(action, System.Windows.Threading.DispatcherPriority.DataBind);
            }
            else
            {
                action();
            }
        }
    }
}
