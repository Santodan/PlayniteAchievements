using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Playnite.SDK;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Friends;
using PlayniteAchievements.ViewModels.Items;

using ObservableObject = PlayniteAchievements.Common.ObservableObject;

namespace PlayniteAchievements.ViewModels
{
    /// <summary>
    /// Compare-friend selection for the self achievement surfaces (Overview selected game and
    /// the single-game achievements window): loads every friend's cached rows for one game,
    /// exposes the friends that have data as dropdown options, and applies the selected
    /// friend's unlock state onto the self display items' comparison fields. Selection is
    /// session-only and clears whenever the game changes.
    /// </summary>
    public sealed class FriendCompareController : ObservableObject
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
        private List<Option> _options = new List<Option>();
        private Option _selected;
        private IReadOnlyList<AchievementDisplayItem> _targetItems;
        private readonly List<AchievementDisplayItem> _appliedItems = new List<AchievementDisplayItem>();
        private bool _availabilityPending;
        private bool _indexedAvailability;

        internal FriendCompareController(
            IFriendCacheManager friendCache,
            PlayniteAchievementsSettings settings,
            ILogger logger)
        {
            _friendCache = friendCache;
            _settings = settings;
            _logger = logger;
            WarmAvailabilityIndex();
        }

        // Stay visible while the cheap shared availability index is warming. Once warm, the
        // index answers immediately while the heavier per-game option rows continue loading.
        public bool IsCompareAvailable =>
            _availabilityPending || _indexedAvailability || _options.Count > 0;

        public string CompareSelectionText => _selected?.DisplayName
            ?? ResourceProvider.GetString("LOCPlayAch_Filter_CompareSelectorPlaceholder");

        // Key-based accessors for the control-bar dropdown (its options are plain strings).

        public IEnumerable<string> OptionKeys => _options.Select(option => option.Key);

        public bool IsKeySelected(string key)
        {
            return _selected != null &&
                   string.Equals(_selected.Key, key, StringComparison.OrdinalIgnoreCase);
        }

        public string GetDisplayNameForKey(string key)
        {
            return _options.FirstOrDefault(option =>
                string.Equals(option.Key, key, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? key;
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
                }

                return;
            }

            Select(_options.FirstOrDefault(option =>
                string.Equals(option.Key, key, StringComparison.OrdinalIgnoreCase)));
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
                _indexedAvailability = false;
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
            BeginAvailabilityLoad(targetGameId, version);
            Task.Run(() =>
            {
                List<FriendAchievementDisplayItem> rows = null;
                try
                {
                    rows = _friendCache.LoadFriendGameAchievementData(targetGameId)?.AllAchievements;
                }
                catch (Exception ex)
                {
                    _logger?.Error(ex, $"Failed to load friend rows for compare, game {targetGameId}.");
                }

                void Apply()
                {
                    if (version != Volatile.Read(ref _loadVersion))
                    {
                        return;
                    }

                    _friendRows = rows ?? new List<FriendAchievementDisplayItem>();
                    _options = BuildOptions(_friendRows, _settings?.Persisted);
                    _indexedAvailability = _options.Count > 0;
                    _availabilityPending = false;
                    OnPropertyChanged(nameof(OptionKeys));
                    OnPropertyChanged(nameof(IsCompareAvailable));
                    ApplySelection();
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

        private void WarmAvailabilityIndex()
        {
            if (_friendCache == null || _settings?.Persisted?.EnableFriendsFeatures != true)
            {
                return;
            }

            Task.Run(() =>
            {
                try
                {
                    _friendCache.LoadFriendDataPlayniteGameIds();
                }
                catch (Exception ex)
                {
                    _logger?.Error(ex, "Failed to warm the friend compare availability index.");
                }
            });
        }

        private void BeginAvailabilityLoad(Guid gameId, int version)
        {
            Task.Run(() =>
            {
                bool isAvailable;
                try
                {
                    isAvailable = (_friendCache.LoadFriendDataPlayniteGameIds() ??
                                   Array.Empty<Guid>()).Contains(gameId);
                }
                catch (Exception ex)
                {
                    _logger?.Error(ex, $"Failed to load friend compare availability for game {gameId}.");
                    isAvailable = false;
                }

                void Apply()
                {
                    if (version != Volatile.Read(ref _loadVersion))
                    {
                        return;
                    }

                    _indexedAvailability = isAvailable;
                    _availabilityPending = false;
                    OnPropertyChanged(nameof(IsCompareAvailable));
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

        private static List<Option> BuildOptions(List<FriendAchievementDisplayItem> rows, PersistedSettings persisted)
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
            foreach (var row in rows)
            {
                if (row == null || string.IsNullOrWhiteSpace(row.FriendName))
                {
                    continue;
                }

                var key = FriendOverviewProjection.GetFriendScopeKey(row);
                if (FriendOverviewProjection.IsAllScope(key) || byKey.ContainsKey(key))
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

            // Favorites first, then alphabetical within each group.
            return byKey.Values
                .OrderByDescending(option => option.IsFavorite)
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

        private void ApplySelection()
        {
            ClearApplied();
            var items = _targetItems;
            if (_selected == null || items == null || items.Count == 0)
            {
                return;
            }

            var compareRows = new Dictionary<string, FriendAchievementDisplayItem>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in _friendRows)
            {
                if (row == null || string.IsNullOrWhiteSpace(row.ApiName))
                {
                    continue;
                }

                var key = FriendOverviewProjection.GetFriendScopeKey(row);
                if (!string.Equals(key, _selected.Key, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!compareRows.TryGetValue(row.ApiName, out var existing) ||
                    (row.Unlocked && !existing.Unlocked))
                {
                    compareRows[row.ApiName] = row;
                }
            }

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

                item.ApplyComparison(
                    _selected.DisplayName,
                    _selected.AvatarPath ?? compareRow?.FriendAvatarPath,
                    compareRow?.UnlockTimeUtc,
                    compareRow?.Unlocked == true);
                _appliedItems.Add(item);
            }
        }

        private void ClearApplied()
        {
            if (_appliedItems.Count == 0)
            {
                return;
            }

            foreach (var item in _appliedItems)
            {
                item?.ClearComparison();
            }

            _appliedItems.Clear();
        }

        private void NotifyCompareStateChanged()
        {
            OnPropertyChanged(nameof(CompareSelectionText));
            OnPropertyChanged(nameof(IsCompareAvailable));
        }
    }
}
