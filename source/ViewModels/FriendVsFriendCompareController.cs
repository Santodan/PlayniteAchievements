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
    /// Compare-friend selection for the friends surfaces: the dropdown lists the friends the
    /// caller offers, plus the current user when they have cached progress for a listed game, and
    /// the chosen side's unlock state is applied onto the selected friend's rows, or onto every
    /// friend's rows when no friend is selected. The caller clears the selection whenever the friend (or game) being viewed changes,
    /// and a remembered entry is selected again on each pair it is offered for.
    /// </summary>
    public sealed class FriendVsFriendCompareController :
        FriendCompareControllerBase<FriendAchievementDisplayItem>, IGridCompareSource
    {
        // Friend option keys always contain '|' (FriendOverviewProjection.BuildFriendKey) and the
        // all-friends scope key is "All", so this sentinel cannot collide with either.
        internal const string SelfOptionKey = "self";

        private readonly Func<FriendSummaryItem> _getSelectedFriend;
        private readonly Func<IReadOnlyList<FriendSummaryItem>> _getCandidates;
        private readonly Func<FriendAchievementDisplayItem, bool> _isRowInScope;
        private readonly Func<IReadOnlyList<FriendIdentity>> _loadCurrentUserIdentities;
        private readonly Func<FriendSummaryItem, FriendAchievementDisplayItem, bool> _hasGameData;
        private readonly ILogger _logger;

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

        /// <param name="getSelectedFriend">
        /// The friend whose rows the comparison is applied to; null applies it to every friend's rows.
        /// </param>
        /// <param name="getCandidates">
        /// The friends offered as comparison targets: every friend but the selected one, whether
        /// or not they have data for the game in scope.
        /// </param>
        /// <param name="isRowInScope">
        /// Optional filter for callers whose row pool spans more than the game being compared.
        /// Omit when the pool is already scoped to one game.
        /// </param>
        /// <param name="loadCurrentUserIdentities">
        /// The signed-in account per provider, used to label the current user's side of a self
        /// comparison. Omit to leave the self option out of the dropdown.
        /// </param>
        /// <param name="hasGameData">
        /// Whether a friend has anything for a row's game, for callers that know of games a friend
        /// owns without a row in the pool. A friend with a pool row for the game always counts.
        /// </param>
        public FriendVsFriendCompareController(
            Func<FriendSummaryItem> getSelectedFriend,
            Func<IReadOnlyList<FriendSummaryItem>> getCandidates,
            Func<FriendAchievementDisplayItem, bool> isRowInScope = null,
            Func<IReadOnlyList<FriendIdentity>> loadCurrentUserIdentities = null,
            ILogger logger = null,
            Func<FriendSummaryItem, FriendAchievementDisplayItem, bool> hasGameData = null)
        {
            _getSelectedFriend = getSelectedFriend ?? throw new ArgumentNullException(nameof(getSelectedFriend));
            _getCandidates = getCandidates ?? throw new ArgumentNullException(nameof(getCandidates));
            _isRowInScope = isRowInScope;
            _loadCurrentUserIdentities = loadCurrentUserIdentities;
            _hasGameData = hasGameData;
            _logger = logger;
            _selfOptionAvailable = loadCurrentUserIdentities != null;
            BeginIdentityLoad();
        }

        // The list does not depend on the game: every candidate is offered, and one with no data
        // for a row's game reads N/A there. A remembered entry that is no longer a candidate
        // keeps the dropdown shown and selected, and no comparison is drawn.
        public bool IsCompareAvailable =>
            Candidates.Count > 0 || _selfOptionAvailable || RememberedKey != null;

        public string CompareSelectionText => _selfSelected
            ? GetSelfLabel()
            : _compareFriend?.DisplayName
              ?? (RememberedKey != null ? GetDisplayNameForKey(RememberedKey) : null)
              ?? ResourceProvider.GetString("LOCPlayAch_Filter_CompareSelectorPlaceholder");

        public IEnumerable<string> OptionKeys
        {
            get
            {
                var keys = _selfOptionAvailable
                    ? new[] { SelfOptionKey }.Concat(Candidates.Select(FriendOverviewProjection.GetFriendScopeKey))
                    : Candidates.Select(FriendOverviewProjection.GetFriendScopeKey);
                var remembered = RememberedKey;
                return remembered != null &&
                       !(IsSelfKey(remembered) ? _selfOptionAvailable : FindCandidate(remembered) != null)
                    ? keys.Concat(new[] { remembered })
                    : keys;
            }
        }

        // The dropdown reads availability on every control-bar refresh, and building the candidate
        // list walks every friend, so it is cached until Refresh or a row rebuild invalidates it.
        private IReadOnlyList<FriendSummaryItem> Candidates =>
            _candidates ?? (_candidates = _getCandidates() ?? Array.Empty<FriendSummaryItem>());

        public bool IsKeySelected(string key)
        {
            if (!_selfSelected && _compareFriend == null)
            {
                // Nothing is applied; the remembered entry still reads as the selection.
                return IsRememberedKey(key);
            }

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

            return FindCandidate(key)?.DisplayName
                   ?? (IsRememberedKey(key) ? RememberedName : null)
                   ?? key;
        }

        public bool IsKeyFavorite(string key)
        {
            return FindCandidate(key)?.IsFavorite == true;
        }

        // Single-select semantics over checkable menu items: checking a friend or the self entry
        // replaces any other selection; unchecking the selected entry clears the comparison.
        public void SelectKey(string key, bool isSelected)
        {
            SelectKeyCore(key, isSelected);
            Remember(
                _selfSelected
                    ? SelfOptionKey
                    : _compareFriend != null
                        ? FriendOverviewProjection.GetFriendScopeKey(_compareFriend)
                        : null,
                _selfSelected ? null : _compareFriend?.DisplayName);
            NotifyCompareStateChanged();
        }

        private protected override bool TryRestoreRememberedSelection()
        {
            var key = RememberedKey;
            if (key == null || _selfSelected || _compareFriend != null)
            {
                return false;
            }

            if (IsSelfKey(key))
            {
                if (!_selfOptionAvailable)
                {
                    return false;
                }

                _selfSelected = true;
            }
            else
            {
                var candidate = FindCandidate(key);
                if (candidate == null)
                {
                    return false;
                }

                _compareFriend = candidate;
            }

            NotifyCompareStateChanged();
            ApplySelection();
            return true;
        }

        private void SelectKeyCore(string key, bool isSelected)
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
        /// has no row for (or has locked) render as locked, and as N/A when they have nothing for
        /// the game. Call after every rebuild of the rows,
        /// since the display items are replaced rather than mutated in place.
        /// </summary>
        public void UpdateRows(
            IReadOnlyList<FriendAchievementDisplayItem> rowPool,
            IReadOnlyList<FriendAchievementDisplayItem> targetRows)
        {
            _rowPool = rowPool;
            _targetRows = targetRows;
            _candidates = null;
            if (!TryRestoreRememberedSelection())
            {
                ApplySelection();
            }
        }

        // Re-applies the current selection to the rows last handed to UpdateRows, so picking a
        // friend takes effect immediately instead of waiting for the next rebuild.
        private void ApplySelection()
        {
            ClearApplied();

            var rowPool = _rowPool;
            var targetRows = _targetRows;
            var selectedFriend = _getSelectedFriend();
            if (targetRows == null)
            {
                return;
            }

            if (_selfSelected)
            {
                ApplySelfComparison(targetRows, selectedFriend);
                return;
            }

            if (_compareFriend == null)
            {
                return;
            }

            // The rows can span games, so the game joins the ApiName in the key.
            var compareFriend = _compareFriend;
            var compareRows = BuildBestRowsByApiName(
                rowPool ?? Array.Empty<FriendAchievementDisplayItem>(),
                row => FriendOverviewProjection.IsSameFriend(row, compareFriend) && IsInScope(row),
                GetGameAchievementKey);
            // A game counts as theirs with an unlock in it or known ownership; rows scraped for a
            // game they may not own list every achievement locked.
            var compareGames = new HashSet<string>(
                compareRows.Values.Where(row => row.Unlocked || row.FriendOwnsGame).Select(GetGameKey),
                StringComparer.OrdinalIgnoreCase);

            var compareName = _compareFriend.DisplayName;
            var compareAvatar = _compareFriend.AvatarPath;
            foreach (var item in targetRows)
            {
                // A friend is not compared with themself: with no friend selected the compare
                // friend's own rows are listed too, and stay as they are.
                if (!IsTargetRow(item, selectedFriend) ||
                    FriendOverviewProjection.IsSameFriend(item, compareFriend))
                {
                    continue;
                }

                FriendAchievementDisplayItem compareRow = null;
                if (!string.IsNullOrWhiteSpace(item.ApiName))
                {
                    compareRows.TryGetValue(GetGameAchievementKey(item), out compareRow);
                }

                // A game the compare friend has nothing for reads N/A, not Locked.
                var applicable = compareGames.Contains(GetGameKey(item)) ||
                                 _hasGameData?.Invoke(compareFriend, item) == true;

                // The row's own friend owns the comparison's own side rather than the current user.
                item.ApplyComparison(
                    compareName,
                    compareAvatar ?? compareRow?.FriendAvatarPath,
                    compareRow?.UnlockTimeUtc,
                    compareRow?.Unlocked == true,
                    selectedFriend?.DisplayName ?? item.FriendName,
                    selectedFriend?.AvatarPath ?? item.FriendAvatarPath,
                    applicable);
                AppliedItems.Add(item);
            }
        }

        // The row's own friend owns the comparison's own side and the current user is the compare
        // side; the self unlock state rides on the rows themselves rather than a pool lookup.
        private void ApplySelfComparison(
            IReadOnlyList<FriendAchievementDisplayItem> targetRows,
            FriendSummaryItem selectedFriend)
        {
            foreach (var item in targetRows)
            {
                if (!IsTargetRow(item, selectedFriend))
                {
                    continue;
                }

                // The self identity follows each row's provider, so a merged friend spanning
                // providers compares against the matching account per row. A game the user has
                // no cached progress for reads N/A, not Locked.
                var self = ResolveSelfIdentity(item.ProviderKey);
                item.ApplyComparison(
                    GetSelfLabel(item.ProviderKey),
                    self?.AvatarPath,
                    item.SelfUnlockTimeUtc,
                    item.UnlockedBySelf,
                    selectedFriend?.DisplayName ?? item.FriendName,
                    selectedFriend?.AvatarPath ?? item.FriendAvatarPath,
                    item.SelfHasGameData);
                AppliedItems.Add(item);
            }
        }

        // A row the comparison is drawn on: in scope, and the selected friend's when there is one.
        private bool IsTargetRow(FriendAchievementDisplayItem row, FriendSummaryItem selectedFriend)
        {
            return row != null &&
                   (selectedFriend == null || FriendOverviewProjection.IsSameFriend(row, selectedFriend)) &&
                   IsInScope(row);
        }

        private static string GetGameKey(FriendAchievementDisplayItem row)
        {
            return FriendOverviewProjection.BuildGameUnlockKey(
                row.ProviderKey, row.ProviderGameKey, row.AppId, row.PlayniteGameId) ?? string.Empty;
        }

        private static string GetGameAchievementKey(FriendAchievementDisplayItem row)
        {
            return GetGameKey(row) + "|ach:" + row.ApiName;
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

        private void BeginIdentityLoad()
        {
            if (_loadCurrentUserIdentities == null)
            {
                return;
            }

            var version = Interlocked.Increment(ref _identityLoadVersion);
            SelfIdentityLoadTask = Task.Run(() =>
            {
                var map = LoadIdentitiesByProvider(_loadCurrentUserIdentities, _logger);

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
            if (targetRows == null)
            {
                return null;
            }

            return targetRows.FirstOrDefault(row => row != null &&
                !string.IsNullOrWhiteSpace(row.ProviderKey) &&
                IsTargetRow(row, selectedFriend))?.ProviderKey;
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
    }
}
