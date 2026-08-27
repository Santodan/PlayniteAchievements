using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Playnite.SDK;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models.Friends;
using PlayniteAchievements.Services.Friends;
using PlayniteAchievements.ViewModels.Items;

using ObservableObject = PlayniteAchievements.Common.ObservableObject;

namespace PlayniteAchievements.ViewModels
{
    /// <summary>
    /// Compare-friend selection for the friends surfaces: the dropdown lists the other friends
    /// with cached rows for the same game, plus the current user when they have cached progress
    /// for that game, and the chosen side's unlock state is applied onto the selected friend's
    /// rows. Selection is session-only; the caller clears it whenever the friend (or game) being
    /// viewed changes.
    /// </summary>
    public sealed class FriendVsFriendCompareController : ObservableObject, IGridCompareSource
    {
        // Friend option keys always contain '|' (FriendOverviewProjection.BuildFriendKey) and the
        // all-friends scope key is "All", so this sentinel cannot collide with either.
        internal const string SelfOptionKey = "self";

        private readonly Func<FriendSummaryItem> _getSelectedFriend;
        private readonly Func<IReadOnlyList<FriendSummaryItem>> _getCandidates;
        private readonly Func<FriendAchievementDisplayItem, bool> _isRowInScope;
        private readonly Func<IReadOnlyList<FriendIdentity>> _loadCurrentUserIdentities;
        private readonly ILogger _logger;
        private readonly List<FriendAchievementDisplayItem> _appliedItems =
            new List<FriendAchievementDisplayItem>();

        private FriendSummaryItem _compareFriend;
        private bool _selfSelected;
        private bool _selfOptionAvailable;
        private int _identityLoadVersion;
        private Dictionary<string, FriendIdentity> _currentUsersByProvider =
            new Dictionary<string, FriendIdentity>(StringComparer.OrdinalIgnoreCase);
        private IReadOnlyList<FriendAchievementDisplayItem> _rowPool;
        private IReadOnlyList<FriendAchievementDisplayItem> _targetRows;
        private IReadOnlyList<FriendSummaryItem> _candidates;

        internal Task SelfIdentityLoadTask { get; private set; }

        /// <param name="getSelectedFriend">The friend whose rows the comparison is applied to.</param>
        /// <param name="getCandidates">
        /// The friends offered as comparison targets. Callers exclude the selected friend and any
        /// friend without cached rows for the game in scope.
        /// </param>
        /// <param name="isRowInScope">
        /// Optional filter for callers whose row pool spans more than the game being compared.
        /// Omit when the pool is already scoped to one game.
        /// </param>
        /// <param name="loadCurrentUserIdentities">
        /// The signed-in account per provider, used to label the current user's side of a self
        /// comparison. Omit to leave the self option out of the dropdown.
        /// </param>
        public FriendVsFriendCompareController(
            Func<FriendSummaryItem> getSelectedFriend,
            Func<IReadOnlyList<FriendSummaryItem>> getCandidates,
            Func<FriendAchievementDisplayItem, bool> isRowInScope = null,
            Func<IReadOnlyList<FriendIdentity>> loadCurrentUserIdentities = null,
            ILogger logger = null)
        {
            _getSelectedFriend = getSelectedFriend ?? throw new ArgumentNullException(nameof(getSelectedFriend));
            _getCandidates = getCandidates ?? throw new ArgumentNullException(nameof(getCandidates));
            _isRowInScope = isRowInScope;
            _loadCurrentUserIdentities = loadCurrentUserIdentities;
            _logger = logger;
            BeginIdentityLoad();
        }

        public bool IsCompareAvailable =>
            _getSelectedFriend() != null && (Candidates.Count > 0 || _selfOptionAvailable);

        public string CompareSelectionText => _selfSelected
            ? GetSelfLabel()
            : _compareFriend?.DisplayName
              ?? ResourceProvider.GetString("LOCPlayAch_Filter_CompareSelectorPlaceholder");

        public IEnumerable<string> OptionKeys => _selfOptionAvailable
            ? new[] { SelfOptionKey }.Concat(Candidates.Select(FriendOverviewProjection.GetFriendScopeKey))
            : Candidates.Select(FriendOverviewProjection.GetFriendScopeKey);

        // The dropdown reads availability on every control-bar refresh, and building the candidate
        // list walks every friend, so it is cached until Refresh or a row rebuild invalidates it.
        private IReadOnlyList<FriendSummaryItem> Candidates =>
            _candidates ?? (_candidates = _getCandidates() ?? Array.Empty<FriendSummaryItem>());

        public bool IsKeySelected(string key)
        {
            if (IsSelfKey(key))
            {
                return _selfSelected;
            }

            return _compareFriend != null && MatchesKey(_compareFriend, key);
        }

        public string GetDisplayNameForKey(string key)
        {
            if (IsSelfKey(key))
            {
                return GetSelfLabel();
            }

            return FindCandidate(key)?.DisplayName ?? key;
        }

        public bool IsKeyFavorite(string key)
        {
            return FindCandidate(key)?.IsFavorite == true;
        }

        // Single-select semantics over checkable menu items: checking a friend or the self entry
        // replaces any other selection; unchecking the selected entry clears the comparison.
        public void SelectKey(string key, bool isSelected)
        {
            if (IsSelfKey(key))
            {
                SelectSelf(isSelected);
                return;
            }

            if (!isSelected)
            {
                if (IsKeySelected(key))
                {
                    Select(null);
                }

                return;
            }

            Select(FindCandidate(key));
        }

        public void ClearSelection()
        {
            Select(null);
        }

        /// <summary>
        /// (Re)applies the comparison to <paramref name="targetRows"/>, reading the compare
        /// friend's unlock state from <paramref name="rowPool"/>. Achievements the compare friend
        /// has no row for (or has locked) render as locked. Call after every rebuild of the rows,
        /// since the display items are replaced rather than mutated in place.
        /// </summary>
        public void UpdateRows(
            IReadOnlyList<FriendAchievementDisplayItem> rowPool,
            IReadOnlyList<FriendAchievementDisplayItem> targetRows)
        {
            _rowPool = rowPool;
            _targetRows = targetRows;
            _candidates = null;
            UpdateSelfAvailability();
            ApplySelection();
        }

        // Re-applies the current selection to the rows last handed to UpdateRows, so picking a
        // friend takes effect immediately instead of waiting for the next rebuild.
        private void ApplySelection()
        {
            ClearApplied();

            var rowPool = _rowPool;
            var targetRows = _targetRows;
            var selectedFriend = _getSelectedFriend();
            if (selectedFriend == null || targetRows == null)
            {
                return;
            }

            if (_selfSelected)
            {
                ApplySelfComparison(targetRows, selectedFriend);
                return;
            }

            if (_compareFriend == null || rowPool == null)
            {
                return;
            }

            // A friend can carry more than one row per achievement (merged accounts); an unlocked
            // row wins so the comparison reflects their best state.
            var compareRows = new Dictionary<string, FriendAchievementDisplayItem>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in rowPool)
            {
                if (row == null ||
                    string.IsNullOrWhiteSpace(row.ApiName) ||
                    !FriendOverviewProjection.IsSameFriend(row, _compareFriend) ||
                    !IsInScope(row))
                {
                    continue;
                }

                if (!compareRows.TryGetValue(row.ApiName, out var existing) ||
                    (row.Unlocked && !existing.Unlocked))
                {
                    compareRows[row.ApiName] = row;
                }
            }

            var compareName = _compareFriend.DisplayName;
            var compareAvatar = _compareFriend.AvatarPath;
            var ownerName = selectedFriend.DisplayName;
            var ownerAvatar = selectedFriend.AvatarPath;
            foreach (var item in targetRows)
            {
                if (item == null ||
                    !FriendOverviewProjection.IsSameFriend(item, selectedFriend) ||
                    !IsInScope(item))
                {
                    continue;
                }

                FriendAchievementDisplayItem compareRow = null;
                if (!string.IsNullOrWhiteSpace(item.ApiName))
                {
                    compareRows.TryGetValue(item.ApiName, out compareRow);
                }

                // The rows belong to the selected friend, so that friend owns the comparison's own
                // side rather than the current user.
                item.ApplyComparison(
                    compareName,
                    compareAvatar ?? compareRow?.FriendAvatarPath,
                    compareRow?.UnlockTimeUtc,
                    compareRow?.Unlocked == true,
                    ownerName ?? item.FriendName,
                    ownerAvatar ?? item.FriendAvatarPath);
                _appliedItems.Add(item);
            }
        }

        // The rows belong to the selected friend, so that friend owns the comparison's own side
        // and the current user is the compare side; the self unlock state rides on the rows
        // themselves rather than a pool lookup.
        private void ApplySelfComparison(
            IReadOnlyList<FriendAchievementDisplayItem> targetRows,
            FriendSummaryItem selectedFriend)
        {
            foreach (var item in targetRows)
            {
                if (item == null ||
                    !FriendOverviewProjection.IsSameFriend(item, selectedFriend) ||
                    !IsInScope(item))
                {
                    continue;
                }

                // The self identity follows each row's provider, so a merged friend spanning
                // providers compares against the matching account per row.
                var self = ResolveSelfIdentity(item.ProviderKey);
                item.ApplyComparison(
                    GetSelfLabel(item.ProviderKey),
                    self?.AvatarPath,
                    item.SelfUnlockTimeUtc,
                    item.UnlockedBySelf,
                    selectedFriend.DisplayName ?? item.FriendName,
                    selectedFriend.AvatarPath ?? item.FriendAvatarPath);
                _appliedItems.Add(item);
            }
        }

        private bool IsInScope(FriendAchievementDisplayItem row)
        {
            return _isRowInScope == null || _isRowInScope(row);
        }

        /// <summary>
        /// Re-raises the dropdown's bindings after the candidate list changes without the
        /// selection changing (e.g. a refresh brings new friends into scope).
        /// </summary>
        public void Refresh()
        {
            _candidates = null;
            UpdateSelfAvailability();
            NotifyCompareStateChanged();
            BeginIdentityLoad();
        }

        private void Select(FriendSummaryItem friend)
        {
            var next = friend != null && Candidates.Any(candidate =>
                FriendOverviewProjection.IsSameFriend(candidate, friend))
                ? friend
                : null;
            if (ReferenceEquals(_compareFriend, next) && !_selfSelected)
            {
                return;
            }

            _selfSelected = false;
            _compareFriend = next;
            NotifyCompareStateChanged();
            ApplySelection();
        }

        private void SelectSelf(bool isSelected)
        {
            if (!isSelected)
            {
                if (_selfSelected)
                {
                    _selfSelected = false;
                    NotifyCompareStateChanged();
                    ApplySelection();
                }

                return;
            }

            if (!_selfOptionAvailable || _selfSelected)
            {
                return;
            }

            _selfSelected = true;
            _compareFriend = null;
            NotifyCompareStateChanged();
            ApplySelection();
        }

        // The self entry is offered only when the selected friend's in-scope rows belong to a
        // game the current user has cached progress for, so the comparison never renders a
        // misleading all-locked column for a game the user simply has not refreshed.
        private void UpdateSelfAvailability()
        {
            var available = ComputeSelfAvailability();
            if (available == _selfOptionAvailable)
            {
                return;
            }

            _selfOptionAvailable = available;
            if (!available && _selfSelected)
            {
                _selfSelected = false;
            }

            NotifyCompareStateChanged();
        }

        private bool ComputeSelfAvailability()
        {
            if (_loadCurrentUserIdentities == null)
            {
                return false;
            }

            var targetRows = _targetRows;
            var selectedFriend = _getSelectedFriend();
            if (targetRows == null || selectedFriend == null)
            {
                return false;
            }

            return targetRows.Any(row => row != null &&
                row.SelfHasGameData &&
                FriendOverviewProjection.IsSameFriend(row, selectedFriend) &&
                IsInScope(row));
        }

        private void BeginIdentityLoad()
        {
            if (_loadCurrentUserIdentities == null)
            {
                return;
            }

            var version = Interlocked.Increment(ref _identityLoadVersion);
            SelfIdentityLoadTask = Task.Run(() =>
            {
                var map = new Dictionary<string, FriendIdentity>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    foreach (var identity in _loadCurrentUserIdentities() ?? new List<FriendIdentity>())
                    {
                        if (identity != null && !string.IsNullOrWhiteSpace(identity.ProviderKey))
                        {
                            map[identity.ProviderKey] = identity;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger?.Debug(ex, "Failed to load current user identities for compare.");
                }

                void Apply()
                {
                    if (version != Volatile.Read(ref _identityLoadVersion))
                    {
                        return;
                    }

                    _currentUsersByProvider = map;
                    NotifyCompareStateChanged();
                    if (_selfSelected)
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

        // The dropdown label is the user's own username for the game's platform; the localized
        // "Me" fallback covers providers with no stored account name.
        private string GetSelfLabel(string providerKey = null)
        {
            var identity = ResolveSelfIdentity(providerKey ?? FirstInScopeProviderKey());
            if (identity != null && IsRealIdentityName(identity))
            {
                return identity.DisplayName.Trim();
            }

            return ResourceProvider.GetString("LOCPlayAch_Filter_CompareSelfOption");
        }

        private FriendIdentity ResolveSelfIdentity(string providerKey)
        {
            if (string.IsNullOrWhiteSpace(providerKey))
            {
                return null;
            }

            _currentUsersByProvider.TryGetValue(providerKey, out var identity);
            return identity;
        }

        private string FirstInScopeProviderKey()
        {
            var targetRows = _targetRows;
            var selectedFriend = _getSelectedFriend();
            if (targetRows == null || selectedFriend == null)
            {
                return null;
            }

            return targetRows.FirstOrDefault(row => row != null &&
                !string.IsNullOrWhiteSpace(row.ProviderKey) &&
                FriendOverviewProjection.IsSameFriend(row, selectedFriend) &&
                IsInScope(row))?.ProviderKey;
        }

        // "unmapped" is the store's placeholder for providers with no stored account, and a
        // display name equal to the external id is a raw account id (e.g. a SteamID64 before the
        // persona is fetched); neither reads as a username.
        private static bool IsRealIdentityName(FriendIdentity identity)
        {
            var name = identity.DisplayName?.Trim();
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }

            if (string.Equals(name, "unmapped", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return !string.Equals(name, identity.ExternalUserId?.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsSelfKey(string key)
        {
            return string.Equals(key, SelfOptionKey, StringComparison.OrdinalIgnoreCase);
        }

        private FriendSummaryItem FindCandidate(string key)
        {
            return Candidates.FirstOrDefault(candidate => MatchesKey(candidate, key));
        }

        private static bool MatchesKey(FriendSummaryItem friend, string key)
        {
            return friend != null && string.Equals(
                FriendOverviewProjection.GetFriendScopeKey(friend),
                key,
                StringComparison.OrdinalIgnoreCase);
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
