using System;
using System.Collections.Generic;
using System.ComponentModel;
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
            _ => nameof(FriendSummaryItem.LastUnlockUtc)
        };

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

        internal static FriendSummariesGridSortAction ResolveGridSortAction(
            string clickedSortMemberPath,
            string currentSortPath,
            ListSortDirection? currentSortDirection,
            PersistedSettings settings)
        {
            if (string.IsNullOrWhiteSpace(clickedSortMemberPath))
            {
                return FriendSummariesGridSortAction.None();
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

        private static Comparison<FriendSummaryItem> CreateComparison(string sortMemberPath, ListSortDirection direction)
        {
            return sortMemberPath switch
            {
                nameof(FriendSummaryItem.LastUnlockUtc) => (a, b) => CompareWithTieBreakers(
                    a,
                    b,
                    sortMemberPath,
                    CompareDateTime(a?.LastUnlockUtc, b?.LastUnlockUtc, direction)),
                nameof(FriendSummaryItem.SharedGamesCount) => (a, b) => CompareWithTieBreakers(
                    a,
                    b,
                    sortMemberPath,
                    CompareInt(a?.SharedGamesCount ?? 0, b?.SharedGamesCount ?? 0, direction)),
                nameof(FriendSummaryItem.UnlockedAchievementsCount) => (a, b) => CompareWithTieBreakers(
                    a,
                    b,
                    sortMemberPath,
                    CompareInt(a?.UnlockedAchievementsCount ?? 0, b?.UnlockedAchievementsCount ?? 0, direction)),
                nameof(FriendSummaryItem.PrestigeScore) => (a, b) => CompareWithTieBreakers(
                    a,
                    b,
                    sortMemberPath,
                    CompareInt(a?.PrestigeScore ?? 0, b?.PrestigeScore ?? 0, direction)),
                nameof(FriendSummaryItem.CollectionScore) => (a, b) => CompareWithTieBreakers(
                    a,
                    b,
                    sortMemberPath,
                    CompareInt(a?.CollectionScore ?? 0, b?.CollectionScore ?? 0, direction)),
                nameof(FriendSummaryItem.PrestigeLevel) => (a, b) => CompareWithTieBreakers(
                    a,
                    b,
                    sortMemberPath,
                    CompareInt(a?.PrestigeLevel ?? 0, b?.PrestigeLevel ?? 0, direction)),
                nameof(FriendSummaryItem.CollectionLevel) => (a, b) => CompareWithTieBreakers(
                    a,
                    b,
                    sortMemberPath,
                    CompareInt(a?.CollectionLevel ?? 0, b?.CollectionLevel ?? 0, direction)),
                nameof(FriendSummaryItem.DisplayName) => (a, b) => CompareWithTieBreakers(
                    a,
                    b,
                    sortMemberPath,
                    CompareString(a?.DisplayName, b?.DisplayName, direction)),
                _ => null
            };
        }

        private static int CompareWithTieBreakers(
            FriendSummaryItem a,
            FriendSummaryItem b,
            string primarySortMemberPath,
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
