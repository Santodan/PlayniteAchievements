using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Playnite.SDK;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Friends;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Friends;
using PlayniteAchievements.ViewModels.Items;

using ObservableObject = PlayniteAchievements.Common.ObservableObject;

namespace PlayniteAchievements.ViewModels
{
    /// <summary>
    /// Compare-friend selection for the self achievement surfaces (Overview selected game and
    /// the single-game achievements window): loads every friend's cached rows for one game,
    /// exposes every friend as a dropdown option, those with data for the game first, and
    /// applies the selected friend's unlock state onto the self display items' comparison
    /// fields. A friend with no data for the game reads N/A on every row. The selection
    /// clears whenever the game changes, and a remembered friend is selected again on each game
    /// they have data for.
    /// </summary>
    public sealed class FriendCompareController :
        FriendCompareControllerBase<AchievementDisplayItem>, IGridCompareSource
    {
        public sealed class Option
        {
            public string Key { get; set; }
            public string DisplayName { get; set; }
            public string AvatarPath { get; set; }
            public bool IsFavorite { get; set; }
        }

        private readonly IFriendCacheManager _friendCache;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly ILogger _logger;

        private Guid? _gameId;
        private int _loadVersion;
        private List<FriendAchievementDisplayItem> _friendRows = new List<FriendAchievementDisplayItem>();
        private Dictionary<string, FriendIdentity> _currentUsersByProvider =
            new Dictionary<string, FriendIdentity>(StringComparer.OrdinalIgnoreCase);
        private List<Option> _options = new List<Option>();
        private Option _selected;
        private IReadOnlyList<AchievementDisplayItem> _targetItems;
        private bool _availabilityPending;
        // Every friend, loaded with the first game and kept: the list does not depend on the game.
        private List<FriendSummaryItem> _allFriends;

        internal FriendCompareController(
            IFriendCacheManager friendCache,
            PlayniteAchievementsSettings settings,
            ILogger logger)
        {
            _friendCache = friendCache;
            _settings = settings;
            _logger = logger;
        }

        // Shown while the friend list loads, and from then on whenever there is a friend to pick.
        // A remembered friend who is no longer listed keeps the dropdown shown and selected, and
        // no comparison is drawn.
        public bool IsCompareAvailable =>
            _availabilityPending || _options.Count > 0 || RememberedKey != null;

        public string CompareSelectionText => _selected?.DisplayName
            ?? (RememberedKey != null ? RememberedName : null)
            ?? ResourceProvider.GetString("LOCPlayAch_Filter_CompareSelectorPlaceholder");

        // Key-based accessors for the control-bar dropdown (its options are plain strings).

        public IEnumerable<string> OptionKeys
        {
            get
            {
                var keys = _options.Select(option => option.Key);
                return RememberedKey != null && !_options.Any(option => IsRememberedKey(option.Key))
                    ? keys.Concat(new[] { RememberedKey })
                    : keys;
            }
        }

        public bool IsKeySelected(string key)
        {
            return _selected != null
                ? string.Equals(_selected.Key, key, StringComparison.OrdinalIgnoreCase)
                : IsRememberedKey(key);
        }

        public string GetDisplayNameForKey(string key)
        {
            return _options.FirstOrDefault(option =>
                       string.Equals(option.Key, key, StringComparison.OrdinalIgnoreCase))?.DisplayName
                   ?? (IsRememberedKey(key) ? RememberedName : null)
                   ?? key;
        }

        public bool IsKeyFavorite(string key)
        {
            return _options.FirstOrDefault(option =>
                string.Equals(option.Key, key, StringComparison.OrdinalIgnoreCase))?.IsFavorite == true;
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

            Select(_options.FirstOrDefault(option =>
                string.Equals(option.Key, key, StringComparison.OrdinalIgnoreCase)));
            Remember(_selected?.Key, _selected?.DisplayName);
        }

        private protected override bool TryRestoreRememberedSelection()
        {
            var key = RememberedKey;
            if (key == null || _selected != null)
            {
                return false;
            }

            var option = _options.FirstOrDefault(candidate =>
                string.Equals(candidate.Key, key, StringComparison.OrdinalIgnoreCase));
            if (option == null)
            {
                return false;
            }

            _selected = option;
            NotifyCompareStateChanged();
            ApplySelection();
            return true;
        }

        /// <summary>
        /// Sets the game whose friend rows feed the comparison and the self items to enrich.
        /// A game change drops the selection and reloads friend rows off-thread; a same-game
        /// call retargets the current selection onto the new item instances.
        /// </summary>
        public void SetGame(Guid? gameId, IReadOnlyList<AchievementDisplayItem> targetItems)
        {
            var gameChanged = _gameId != gameId;
            _gameId = gameId;
            _targetItems = targetItems;

            if (gameChanged)
            {
                ClearApplied();
                _selected = null;
                _friendRows = new List<FriendAchievementDisplayItem>();
                _options = new List<Option>();
                _availabilityPending = CanLoad(gameId);
                NotifyCompareStateChanged();
                BeginLoad(gameId);
                return;
            }

            ApplySelection();
        }

        public void SetTargetItems(IReadOnlyList<AchievementDisplayItem> targetItems)
        {
            _targetItems = targetItems;
            ApplySelection();
        }

        private void Select(Option option)
        {
            var next = option != null && _options.Contains(option) ? option : null;
            if (ReferenceEquals(_selected, next))
            {
                return;
            }

            _selected = next;
            NotifyCompareStateChanged();
            ApplySelection();
        }

        private void BeginLoad(Guid? gameId)
        {
            var version = Interlocked.Increment(ref _loadVersion);
            if (!CanLoad(gameId))
            {
                return;
            }

            var targetGameId = gameId.Value;
            var knownFriends = _allFriends;
            Task.Run(() =>
            {
                List<FriendAchievementDisplayItem> rows = null;
                try
                {
                    rows = FriendOverviewProjection.ApplyMergeIdentity(
                        _friendCache.LoadFriendGameAchievementData(targetGameId),
                        _settings?.Persisted);
                }
                catch (Exception ex)
                {
                    _logger?.Error(ex, $"Failed to load friend rows for compare, game {targetGameId}.");
                }

                rows = rows ?? new List<FriendAchievementDisplayItem>();
                var friends = knownFriends;
                if (friends == null || HasUnlistedFriend(rows, friends))
                {
                    friends = LoadAllFriends();
                }

                var currentUsers = LoadCurrentUsersByProvider();
                var options = BuildOptions(rows, friends, _settings?.Persisted);

                void Apply()
                {
                    if (version != Volatile.Read(ref _loadVersion))
                    {
                        return;
                    }

                    _friendRows = rows;
                    _allFriends = friends;
                    _currentUsersByProvider = currentUsers;
                    _options = options;
                    _availabilityPending = false;
                    OnPropertyChanged(nameof(OptionKeys));
                    OnPropertyChanged(nameof(IsCompareAvailable));
                    if (!TryRestoreRememberedSelection())
                    {
                        ApplySelection();
                    }
                }

                var dispatcher = System.Windows.Application.Current?.Dispatcher;
                if (dispatcher != null)
                {
                    dispatcher.InvokeIfNeeded(
                        Apply,
                        System.Windows.Threading.DispatcherPriority.DataBind);
                }
                else
                {
                    Apply();
                }
            });
        }

        private bool CanLoad(Guid? gameId)
        {
            return _friendCache != null &&
                   gameId.HasValue &&
                   gameId.Value != Guid.Empty &&
                   _settings?.Persisted?.EnableFriendsFeatures == true;
        }

        // The friend list alone, without achievement rows. A failed load is logged and yields an
        // empty list, so the options fall back to the friends found in the game's rows.
        private List<FriendSummaryItem> LoadAllFriends()
        {
            try
            {
                return new FriendOverviewProjection(
                        _friendCache.LoadFriendsOverviewPatchData(null),
                        _settings?.Persisted)
                    .Friends
                    .Where(friend => friend != null)
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Failed to load the friend list for compare.");
                return new List<FriendSummaryItem>();
            }
        }

        // A friend added since the list was loaded shows up in a game's rows first.
        private static bool HasUnlistedFriend(
            List<FriendAchievementDisplayItem> rows,
            List<FriendSummaryItem> friends)
        {
            var listed = new HashSet<string>(
                friends.Select(FriendOverviewProjection.GetFriendScopeKey),
                StringComparer.OrdinalIgnoreCase);
            return rows.Any(row => row != null && !listed.Contains(FriendOverviewProjection.GetFriendScopeKey(row)));
        }

        // Every friend, those with rows for the game first, then favorites, then by name. A friend
        // found only in the rows (the list failed to load) is listed from the row.
        private static List<Option> BuildOptions(
            List<FriendAchievementDisplayItem> rows,
            List<FriendSummaryItem> friends,
            PersistedSettings persisted)
        {
            var favoriteAccountKeys = new HashSet<string>(
                (persisted?.Friends ?? Enumerable.Empty<FriendSettingsEntry>())
                    .Where(entry => entry != null && entry.IsFavorite)
                    .Select(entry => FriendAccountRef.BuildKey(entry.ProviderKey, entry.ExternalUserId))
                    .Where(key => !string.IsNullOrWhiteSpace(key)),
                StringComparer.OrdinalIgnoreCase);
            var groupsById = (persisted?.GetFriendMergeGroups() ?? Enumerable.Empty<FriendMergeGroup>())
                .Where(group => !string.IsNullOrWhiteSpace(group?.Id))
                .GroupBy(group => group.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

            var byKey = new Dictionary<string, Option>(StringComparer.OrdinalIgnoreCase);
            foreach (var friend in friends ?? new List<FriendSummaryItem>())
            {
                var key = FriendOverviewProjection.GetFriendScopeKey(friend);
                if (FriendOverviewProjection.IsAllScope(key) ||
                    string.IsNullOrWhiteSpace(friend.DisplayName) ||
                    byKey.ContainsKey(key))
                {
                    continue;
                }

                byKey[key] = new Option
                {
                    Key = key,
                    DisplayName = friend.DisplayName,
                    AvatarPath = friend.AvatarPath,
                    IsFavorite = friend.IsFavorite
                };
            }

            var withData = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows)
            {
                if (row == null || string.IsNullOrWhiteSpace(row.FriendName))
                {
                    continue;
                }

                var key = FriendOverviewProjection.GetFriendScopeKey(row);
                if (FriendOverviewProjection.IsAllScope(key))
                {
                    continue;
                }

                // Scraped rows alone do not put a friend among those with the game.
                if (row.Unlocked || row.FriendOwnsGame)
                {
                    withData.Add(key);
                }

                if (byKey.ContainsKey(key))
                {
                    continue;
                }

                byKey[key] = new Option
                {
                    Key = key,
                    DisplayName = row.FriendName,
                    AvatarPath = row.FriendAvatarPath,
                    IsFavorite = IsRowFavorite(row, favoriteAccountKeys, groupsById)
                };
            }

            return byKey.Values
                .OrderByDescending(option => withData.Contains(option.Key))
                .ThenByDescending(option => option.IsFavorite)
                .ThenBy(option => option.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        private static bool IsRowFavorite(
            FriendAchievementDisplayItem row,
            HashSet<string> favoriteAccountKeys,
            Dictionary<string, FriendMergeGroup> groupsById)
        {
            if (favoriteAccountKeys.Count == 0)
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(row.FriendGroupId) &&
                groupsById.TryGetValue(row.FriendGroupId, out var group))
            {
                return (group.Members ?? new List<FriendAccountRef>())
                    .Any(member => member != null &&
                                   favoriteAccountKeys.Contains(
                                       FriendAccountRef.BuildKey(member.ProviderKey, member.ExternalUserId)));
            }

            return favoriteAccountKeys.Contains(
                FriendAccountRef.BuildKey(row.ProviderKey, row.FriendExternalUserId));
        }

        // The signed-in account per provider, used to identify the user's own side of the
        // comparison. Reloaded on the same background pass as the friend rows so a freshly cached
        // avatar appears without a restart.
        private Dictionary<string, FriendIdentity> LoadCurrentUsersByProvider()
        {
            return LoadIdentitiesByProvider(() => _friendCache?.LoadCurrentUserIdentities(), _logger);
        }

        private void ApplySelection()
        {
            ClearApplied();
            var items = _targetItems;
            if (_selected == null || items == null || items.Count == 0)
            {
                return;
            }

            var selectedKey = _selected.Key;
            var compareRows = BuildBestRowsByApiName(
                _friendRows,
                row => string.Equals(
                    FriendOverviewProjection.GetFriendScopeKey(row),
                    selectedKey,
                    StringComparison.OrdinalIgnoreCase));

            // A friend with no unlock in this game and no known ownership of it reads N/A on every
            // row; rows scraped for a game they may not own list every achievement locked.
            var applicable = compareRows.Values.Any(row => row.Unlocked || row.FriendOwnsGame);
            foreach (var item in items)
            {
                if (item == null)
                {
                    continue;
                }

                FriendAchievementDisplayItem compareRow = null;
                if (!string.IsNullOrWhiteSpace(item.ApiName))
                {
                    compareRows.TryGetValue(item.ApiName, out compareRow);
                }

                // The own-row identity follows the achievement's provider, so a Steam game shows the
                // Steam account even when the compare friend came from another provider.
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
    }
}
