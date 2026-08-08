using System;
using System.Collections.Generic;
using System.Linq;
using Playnite.SDK;
using PlayniteAchievements.Services.Friends;
using PlayniteAchievements.ViewModels.Items;

using ObservableObject = PlayniteAchievements.Common.ObservableObject;

namespace PlayniteAchievements.ViewModels
{
    /// <summary>
    /// Compare-friend selection for the friends surfaces, where both sides of the comparison are
    /// friends: the dropdown lists the other friends with cached rows for the same game, and the
    /// chosen friend's unlock state is applied onto the selected friend's rows. Selection is
    /// session-only; the caller clears it whenever the friend (or game) being viewed changes.
    /// </summary>
    public sealed class FriendVsFriendCompareController : ObservableObject, IGridCompareSource
    {
        private readonly Func<FriendSummaryItem> _getSelectedFriend;
        private readonly Func<IReadOnlyList<FriendSummaryItem>> _getCandidates;
        private readonly List<FriendAchievementDisplayItem> _appliedItems =
            new List<FriendAchievementDisplayItem>();

        private FriendSummaryItem _compareFriend;

        /// <param name="getSelectedFriend">The friend whose rows the comparison is applied to.</param>
        /// <param name="getCandidates">
        /// The friends offered as comparison targets. Callers exclude the selected friend and any
        /// friend without cached rows for the game in scope.
        /// </param>
        public FriendVsFriendCompareController(
            Func<FriendSummaryItem> getSelectedFriend,
            Func<IReadOnlyList<FriendSummaryItem>> getCandidates)
        {
            _getSelectedFriend = getSelectedFriend ?? throw new ArgumentNullException(nameof(getSelectedFriend));
            _getCandidates = getCandidates ?? throw new ArgumentNullException(nameof(getCandidates));
        }

        public bool IsCompareAvailable => _getSelectedFriend() != null && Candidates.Count > 0;

        public string CompareSelectionText => _compareFriend?.DisplayName
            ?? ResourceProvider.GetString("LOCPlayAch_Filter_CompareSelectorPlaceholder");

        public IEnumerable<string> OptionKeys =>
            Candidates.Select(FriendOverviewProjection.GetFriendScopeKey);

        private IReadOnlyList<FriendSummaryItem> Candidates =>
            _getCandidates() ?? Array.Empty<FriendSummaryItem>();

        public bool IsKeySelected(string key)
        {
            return _compareFriend != null && MatchesKey(_compareFriend, key);
        }

        public string GetDisplayNameForKey(string key)
        {
            return FindCandidate(key)?.DisplayName ?? key;
        }

        public bool IsKeyFavorite(string key)
        {
            return FindCandidate(key)?.IsFavorite == true;
        }

        // Single-select semantics over checkable menu items: checking a friend replaces any other
        // selection; unchecking the selected friend clears the comparison.
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
            ClearApplied();

            var selectedFriend = _getSelectedFriend();
            if (_compareFriend == null || selectedFriend == null || rowPool == null || targetRows == null)
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
                    !FriendOverviewProjection.IsSameFriend(row, _compareFriend))
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
            foreach (var item in targetRows)
            {
                if (item == null || !FriendOverviewProjection.IsSameFriend(item, selectedFriend))
                {
                    continue;
                }

                FriendAchievementDisplayItem compareRow = null;
                if (!string.IsNullOrWhiteSpace(item.ApiName))
                {
                    compareRows.TryGetValue(item.ApiName, out compareRow);
                }

                item.ApplyComparison(
                    compareName,
                    compareAvatar ?? compareRow?.FriendAvatarPath,
                    compareRow?.UnlockTimeUtc,
                    compareRow?.Unlocked == true);
                _appliedItems.Add(item);
            }
        }

        /// <summary>
        /// Re-raises the dropdown's bindings after the candidate list changes without the
        /// selection changing (e.g. a refresh brings new friends into scope).
        /// </summary>
        public void Refresh()
        {
            NotifyCompareStateChanged();
        }

        private void Select(FriendSummaryItem friend)
        {
            var next = friend != null && Candidates.Any(candidate =>
                FriendOverviewProjection.IsSameFriend(candidate, friend))
                ? friend
                : null;
            if (ReferenceEquals(_compareFriend, next))
            {
                return;
            }

            _compareFriend = next;
            NotifyCompareStateChanged();
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
