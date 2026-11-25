using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.Services
{
    public readonly struct FriendSummariesSortSpec
    {
        public FriendSummariesSortSpec(FriendSummariesSortMode mode, ListSortDirection direction)
        {
            Mode = mode;
            Direction = direction;
        }

        public FriendSummariesSortMode Mode { get; }

        public ListSortDirection Direction { get; }

        public string SortMemberPath => Mode switch
        {
            FriendSummariesSortMode.RecentUnlock => nameof(FriendSummaryItem.LastUnlockUtc),
            FriendSummariesSortMode.SharedGames => nameof(FriendSummaryItem.SharedGamesCount),
            FriendSummariesSortMode.UnlockedAchievements => nameof(FriendSummaryItem.UnlockedAchievementsCount),
            FriendSummariesSortMode.PrestigeScore => nameof(FriendSummaryItem.PrestigeScore),
            FriendSummariesSortMode.CollectionScore => nameof(FriendSummaryItem.CollectionScore),
            FriendSummariesSortMode.PrestigeLevel => nameof(FriendSummaryItem.PrestigeLevel),
            FriendSummariesSortMode.CollectionLevel => nameof(FriendSummaryItem.CollectionLevel),
            FriendSummariesSortMode.Alphabetical => nameof(FriendSummaryItem.DisplayName),
            FriendSummariesSortMode.CompletedGames => nameof(FriendSummaryItem.CompletedGamesCount),
            _ => nameof(FriendSummaryItem.LastUnlockUtc)
        };

        /// <summary>The sort mode whose column is <paramref name="sortMemberPath"/>; false for a column no mode sorts by.</summary>
        public static bool TryModeOf(string sortMemberPath, out FriendSummariesSortMode mode)
        {
            foreach (FriendSummariesSortMode candidate in Enum.GetValues(typeof(FriendSummariesSortMode)))
            {
                if (string.Equals(
                        new FriendSummariesSortSpec(candidate, ListSortDirection.Descending).SortMemberPath,
                        sortMemberPath,
                        StringComparison.Ordinal))
                {
                    mode = candidate;
                    return true;
                }
            }

            mode = FriendSummariesSortMode.RecentUnlock;
            return false;
        }

        public string IndicatorSortMemberPath => Mode switch
        {
            FriendSummariesSortMode.RecentUnlock => null,
            _ => SortMemberPath
        };
    }

    internal enum FriendSummariesGridSortActionKind
    {
        None,
        ApplySort,
        ResetToDefault
    }

    internal readonly struct FriendSummariesGridSortAction
    {
        private FriendSummariesGridSortAction(
            FriendSummariesGridSortActionKind kind,
            string sortMemberPath,
            ListSortDirection? direction)
        {
            Kind = kind;
            SortMemberPath = sortMemberPath;
            Direction = direction;
        }

        public FriendSummariesGridSortActionKind Kind { get; }

        public string SortMemberPath { get; }

        public ListSortDirection? Direction { get; }

        public static FriendSummariesGridSortAction None()
        {
            return new FriendSummariesGridSortAction(FriendSummariesGridSortActionKind.None, null, null);
        }

        public static FriendSummariesGridSortAction ApplySort(string sortMemberPath, ListSortDirection direction)
        {
            return new FriendSummariesGridSortAction(FriendSummariesGridSortActionKind.ApplySort, sortMemberPath, direction);
        }

        public static FriendSummariesGridSortAction ResetToDefault()
        {
            return new FriendSummariesGridSortAction(FriendSummariesGridSortActionKind.ResetToDefault, null, null);
        }
    }

    public static class FriendSummarySortHelper
    {
        public static FriendSummariesSortSpec GetConfiguredDefaultSort(PersistedSettings settings)
        {
            if (settings == null)
            {
                return new FriendSummariesSortSpec(
                    FriendSummariesSortMode.RecentUnlock,
                    ListSortDirection.Descending);
            }

            return new FriendSummariesSortSpec(
                settings.FriendsOverviewFriendSummariesGridSortMode,
                settings.FriendsOverviewFriendSummariesGridSortDescending
                    ? ListSortDirection.Descending
                    : ListSortDirection.Ascending);
        }

        public static bool IsConfiguredDefaultSortPropertyName(string propertyName)
        {
            return propertyName == nameof(PersistedSettings.FriendsOverviewFriendSummariesGridSortMode) ||
                   propertyName == nameof(PersistedSettings.FriendsOverviewFriendSummariesGridSortDescending);
        }

        public static void ApplySortIndicator(
            string currentSortPath,
            ListSortDirection? currentSortDirection,
            PersistedSettings settings,
            Action<string, ListSortDirection?> applyIndicator)
        {
            if (applyIndicator == null)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(currentSortPath) && currentSortDirection.HasValue)
            {
                applyIndicator(currentSortPath, currentSortDirection);
                return;
            }

            var configuredSort = GetConfiguredDefaultSort(settings);
            applyIndicator(
                configuredSort.IndicatorSortMemberPath,
                string.IsNullOrWhiteSpace(configuredSort.IndicatorSortMemberPath)
                    ? (ListSortDirection?)null
                    : configuredSort.Direction);
        }

        public static void SortByConfiguredDefault(List<FriendSummaryItem> items, PersistedSettings settings)
        {
            var configuredSort = GetConfiguredDefaultSort(settings);
            Sort(items, configuredSort.SortMemberPath, configuredSort.Direction);
        }

        /// <summary>
        /// Resolves a header click, or a Sort By request, into a sort action. A result that
        /// removes the sort is recorded on <paramref name="sortEvent"/>, so Sort By can show no sort.
        /// </summary>
        internal static FriendSummariesGridSortAction ResolveGridSortAction(
            string clickedSortMemberPath,
            string currentSortPath,
            ListSortDirection? currentSortDirection,
            PersistedSettings settings,
            System.Windows.Controls.DataGridSortingEventArgs sortEvent = null)
        {
            var action = ResolveGridSortActionCore(
                clickedSortMemberPath,
                currentSortPath,
                currentSortDirection,
                settings,
                GridSortRequestEventArgs.Of(sortEvent));
            if (action.Kind == FriendSummariesGridSortActionKind.ResetToDefault)
            {
                GridSortRequestEventArgs.MarkReset(sortEvent);
            }

            return action;
        }

        private static FriendSummariesGridSortAction ResolveGridSortActionCore(
            string clickedSortMemberPath,
            string currentSortPath,
            ListSortDirection? currentSortDirection,
            PersistedSettings settings,
            GridSortRequestEventArgs request)
        {
            if (request?.IsReset == true)
            {
                return FriendSummariesGridSortAction.ResetToDefault();
            }

            if (string.IsNullOrWhiteSpace(clickedSortMemberPath))
            {
                return FriendSummariesGridSortAction.None();
            }

            if (request != null)
            {
                return FriendSummariesGridSortAction.ApplySort(clickedSortMemberPath, request.Direction.Value);
            }

            var configuredSort = GetConfiguredDefaultSort(settings);
            var cycleDirections = GetCycleDirections(clickedSortMemberPath, configuredSort);
            if (cycleDirections.Count == 0)
            {
                return FriendSummariesGridSortAction.ResetToDefault();
            }

            if (!currentSortDirection.HasValue ||
                !string.Equals(currentSortPath, clickedSortMemberPath, StringComparison.Ordinal))
            {
                return FriendSummariesGridSortAction.ApplySort(clickedSortMemberPath, cycleDirections[0]);
            }

            var currentDirectionIndex = cycleDirections.IndexOf(currentSortDirection.Value);
            if (currentDirectionIndex < 0 || currentDirectionIndex == cycleDirections.Count - 1)
            {
                return FriendSummariesGridSortAction.ResetToDefault();
            }

            return FriendSummariesGridSortAction.ApplySort(
                clickedSortMemberPath,
                cycleDirections[currentDirectionIndex + 1]);
        }

        public static bool TrySortItems(
            List<FriendSummaryItem> items,
            string sortMemberPath,
            ListSortDirection direction,
            ref string currentSortPath,
            ref ListSortDirection currentSortDirection)
        {
            if (items == null || string.IsNullOrWhiteSpace(sortMemberPath))
            {
                return false;
            }

            if (TryReverse(items, sortMemberPath, direction, ref currentSortPath, ref currentSortDirection))
            {
                return true;
            }

            var comparison = CreateComparison(sortMemberPath, direction);
            if (comparison == null)
            {
                return false;
            }

            items.Sort(comparison);
            currentSortPath = sortMemberPath;
            currentSortDirection = direction;
            return true;
        }

        private static bool TryReverse(
            List<FriendSummaryItem> items,
            string sortMemberPath,
            ListSortDirection direction,
            ref string currentSortPath,
            ref ListSortDirection currentSortDirection)
        {
            if (!string.Equals(currentSortPath, sortMemberPath, StringComparison.Ordinal) ||
                currentSortDirection != ListSortDirection.Ascending ||
                direction != ListSortDirection.Descending)
            {
                return false;
            }

            // Favorites are pinned first in both directions, so descending is not a pure reversal of
            // ascending; fall back to a full re-sort when any favorite is present.
            if (items.Any(item => item?.IsFavorite == true))
            {
                return false;
            }

            items.Reverse();
            currentSortDirection = direction;
            return true;
        }

        private static List<ListSortDirection> GetCycleDirections(
            string clickedSortMemberPath,
            FriendSummariesSortSpec configuredSort)
        {
            var directions = new List<ListSortDirection>
            {
                ListSortDirection.Ascending,
                ListSortDirection.Descending
            };

            if (string.Equals(configuredSort.SortMemberPath, clickedSortMemberPath, StringComparison.Ordinal))
            {
                directions.Remove(configuredSort.Direction);
            }

            return directions;
        }

        /// <summary>
        /// Sorts a ranking: by <paramref name="sortMemberPath"/> alone, favorites not pinned first.
        /// False for a column no comparison sorts by.
        /// </summary>
        public static bool SortRanked(List<FriendSummaryItem> items, string sortMemberPath, ListSortDirection direction)
        {
            var comparison = items == null ? null : CreateComparison(sortMemberPath, direction, pinFavorites: false);
            if (comparison == null)
            {
                return false;
            }

            items.Sort(comparison);
            return true;
        }

        private static Comparison<FriendSummaryItem> CreateComparison(
            string sortMemberPath,
            ListSortDirection direction,
            bool pinFavorites = true)
        {
            return sortMemberPath switch
            {
                nameof(FriendSummaryItem.LastUnlockUtc) => (a, b) => CompareWithTieBreakers(
                    a,
                    b,
                    sortMemberPath,
                    pinFavorites,
                    CompareDateTime(a?.LastUnlockUtc, b?.LastUnlockUtc, direction)),
                nameof(FriendSummaryItem.SharedGamesCount) => (a, b) => CompareWithTieBreakers(
                    a,
                    b,
                    sortMemberPath,
                    pinFavorites,
                    CompareInt(a?.SharedGamesCount ?? 0, b?.SharedGamesCount ?? 0, direction)),
                nameof(FriendSummaryItem.UnlockedAchievementsCount) => (a, b) => CompareWithTieBreakers(
                    a,
                    b,
                    sortMemberPath,
                    pinFavorites,
                    CompareInt(a?.UnlockedAchievementsCount ?? 0, b?.UnlockedAchievementsCount ?? 0, direction)),
                nameof(FriendSummaryItem.PrestigeScore) => (a, b) => CompareWithTieBreakers(
                    a,
                    b,
                    sortMemberPath,
                    pinFavorites,
                    CompareInt(a?.PrestigeScore ?? 0, b?.PrestigeScore ?? 0, direction)),
                nameof(FriendSummaryItem.CollectionScore) => (a, b) => CompareWithTieBreakers(
                    a,
                    b,
                    sortMemberPath,
                    pinFavorites,
                    CompareInt(a?.CollectionScore ?? 0, b?.CollectionScore ?? 0, direction)),
                nameof(FriendSummaryItem.PrestigeLevel) => (a, b) => CompareWithTieBreakers(
                    a,
                    b,
                    sortMemberPath,
                    pinFavorites,
                    CompareInt(a?.PrestigeLevel ?? 0, b?.PrestigeLevel ?? 0, direction)),
                nameof(FriendSummaryItem.CollectionLevel) => (a, b) => CompareWithTieBreakers(
                    a,
                    b,
                    sortMemberPath,
                    pinFavorites,
                    CompareInt(a?.CollectionLevel ?? 0, b?.CollectionLevel ?? 0, direction)),
                nameof(FriendSummaryItem.CompletedGamesCount) => (a, b) => CompareWithTieBreakers(
                    a,
                    b,
                    sortMemberPath,
                    pinFavorites,
                    CompareInt(a?.CompletedGamesCount ?? 0, b?.CompletedGamesCount ?? 0, direction)),
                nameof(FriendSummaryItem.DisplayName) => (a, b) => CompareWithTieBreakers(
                    a,
                    b,
                    sortMemberPath,
                    pinFavorites,
                    CompareString(a?.DisplayName, b?.DisplayName, direction)),
                _ => null
            };
        }

        private static int CompareWithTieBreakers(
            FriendSummaryItem a,
            FriendSummaryItem b,
            string primarySortMemberPath,
            bool pinFavorites,
            int primaryComparison)
        {
            if (ReferenceEquals(a, b))
            {
                return 0;
            }

            if (a == null)
            {
                return 1;
            }

            if (b == null)
            {
                return -1;
            }

            // Favorites are pinned ahead of non-favorites regardless of the active sort column.
            var favoriteComparison = pinFavorites ? b.IsFavorite.CompareTo(a.IsFavorite) : 0;
            if (favoriteComparison != 0)
            {
                return favoriteComparison;
            }

            if (primaryComparison != 0)
            {
                return primaryComparison;
            }

            if (!string.Equals(primarySortMemberPath, nameof(FriendSummaryItem.LastUnlockUtc), StringComparison.Ordinal))
            {
                var comparison = CompareDateTime(a.LastUnlockUtc, b.LastUnlockUtc, ListSortDirection.Descending);
                if (comparison != 0)
                {
                    return comparison;
                }
            }

            if (!string.Equals(primarySortMemberPath, nameof(FriendSummaryItem.DisplayName), StringComparison.Ordinal))
            {
                var comparison = CompareString(a.DisplayName, b.DisplayName, ListSortDirection.Ascending);
                if (comparison != 0)
                {
                    return comparison;
                }
            }

            return CompareString(a.Key, b.Key, ListSortDirection.Ascending);
        }

        private static int CompareDateTime(DateTime? left, DateTime? right, ListSortDirection direction)
        {
            var comparison = (left ?? DateTime.MinValue).CompareTo(right ?? DateTime.MinValue);
            return direction == ListSortDirection.Ascending ? comparison : -comparison;
        }

        private static int CompareInt(int left, int right, ListSortDirection direction)
        {
            var comparison = left.CompareTo(right);
            return direction == ListSortDirection.Ascending ? comparison : -comparison;
        }

        private static int CompareString(string left, string right, ListSortDirection direction)
        {
            var comparison = StringComparer.OrdinalIgnoreCase.Compare(left ?? string.Empty, right ?? string.Empty);
            return direction == ListSortDirection.Ascending ? comparison : -comparison;
        }

        private static void Sort(List<FriendSummaryItem> items, string sortMemberPath, ListSortDirection direction)
        {
            if (items == null)
            {
                return;
            }

            var comparison = CreateComparison(sortMemberPath, direction);
            if (comparison == null)
            {
                return;
            }

            items.Sort(comparison);
        }
    }
}
