using System;
using System.Collections.Generic;
using System.ComponentModel;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.Services
{
    public enum GameSummariesSortSurface
    {
        Overview,
        FriendsOverview
    }

    public readonly struct GameSummariesSortSpec
    {
        public GameSummariesSortSpec(GameSummariesSortMode mode, ListSortDirection direction)
        {
            Mode = mode;
            Direction = direction;
        }

        public GameSummariesSortMode Mode { get; }

        public ListSortDirection Direction { get; }

        public string SortMemberPath => Mode switch
        {
            GameSummariesSortMode.RecentUnlock => nameof(GameSummaryItem.LastUnlockUtc),
            GameSummariesSortMode.LastPlayed => nameof(GameSummaryItem.LastPlayed),
            GameSummariesSortMode.TotalAchievements => nameof(GameSummaryItem.TotalAchievements),
            GameSummariesSortMode.Progress => nameof(GameSummaryItem.Progression),
            GameSummariesSortMode.Alphabetical => "SortingName",
            _ => nameof(GameSummaryItem.LastUnlockUtc)
        };

        public string IndicatorSortMemberPath => Mode switch
        {
            GameSummariesSortMode.RecentUnlock => null,
            GameSummariesSortMode.LastPlayed => nameof(GameSummaryItem.LastPlayed),
            GameSummariesSortMode.TotalAchievements => nameof(GameSummaryItem.TotalAchievements),
            GameSummariesSortMode.Progress => nameof(GameSummaryItem.Progression),
            GameSummariesSortMode.Alphabetical => "SortingName",
            _ => null
        };
    }

    internal enum GameSummariesGridSortActionKind
    {
        None,
        ApplySort,
        ResetToDefault
    }

    internal readonly struct GameSummariesGridSortAction
    {
        private GameSummariesGridSortAction(
            GameSummariesGridSortActionKind kind,
            string sortMemberPath,
            ListSortDirection? direction)
        {
            Kind = kind;
            SortMemberPath = sortMemberPath;
            Direction = direction;
        }

        public GameSummariesGridSortActionKind Kind { get; }

        public string SortMemberPath { get; }

        public ListSortDirection? Direction { get; }

        public static GameSummariesGridSortAction None()
        {
            return new GameSummariesGridSortAction(
                GameSummariesGridSortActionKind.None,
                null,
                null);
        }

        public static GameSummariesGridSortAction ApplySort(
            string sortMemberPath,
            ListSortDirection direction)
        {
            return new GameSummariesGridSortAction(
                GameSummariesGridSortActionKind.ApplySort,
                sortMemberPath,
                direction);
        }

        public static GameSummariesGridSortAction ResetToDefault()
        {
            return new GameSummariesGridSortAction(
                GameSummariesGridSortActionKind.ResetToDefault,
                null,
                null);
        }
    }

    public static class GameSummariesSortHelper
    {
        public static GameSummariesSortSpec GetConfiguredDefaultSort(
            PersistedSettings settings,
            GameSummariesSortSurface surface = GameSummariesSortSurface.Overview)
        {
            if (settings == null)
            {
                return new GameSummariesSortSpec(
                    GameSummariesSortMode.RecentUnlock,
                    ListSortDirection.Descending);
            }

            return surface switch
            {
                GameSummariesSortSurface.FriendsOverview => new GameSummariesSortSpec(
                    settings.FriendsOverviewGameSummariesGridSortMode,
                    settings.FriendsOverviewGameSummariesGridSortDescending
                        ? ListSortDirection.Descending
                        : ListSortDirection.Ascending),
                _ => new GameSummariesSortSpec(
                    settings.OverviewGameSummariesGridSortMode,
                    settings.OverviewGameSummariesGridSortDescending
                        ? ListSortDirection.Descending
                        : ListSortDirection.Ascending)
            };
        }

        public static bool IsConfiguredDefaultSortPropertyName(
            string propertyName,
            GameSummariesSortSurface surface = GameSummariesSortSurface.Overview)
        {
            return surface switch
            {
                GameSummariesSortSurface.FriendsOverview =>
                    propertyName == nameof(PersistedSettings.FriendsOverviewGameSummariesGridSortMode) ||
                    propertyName == nameof(PersistedSettings.FriendsOverviewGameSummariesGridSortDescending),
                _ =>
                    propertyName == nameof(PersistedSettings.OverviewGameSummariesGridSortMode) ||
                    propertyName == nameof(PersistedSettings.OverviewGameSummariesGridSortDescending)
            };
        }

        public static void ApplySortIndicator(
            string currentSortPath,
            ListSortDirection? currentSortDirection,
            PersistedSettings settings,
            Action<string, ListSortDirection?> applyIndicator,
            GameSummariesSortSurface surface = GameSummariesSortSurface.Overview)
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

            var configuredSort = GetConfiguredDefaultSort(settings, surface);
            applyIndicator(
                configuredSort.IndicatorSortMemberPath,
                string.IsNullOrWhiteSpace(configuredSort.IndicatorSortMemberPath)
                    ? (ListSortDirection?)null
                    : configuredSort.Direction);
        }

        public static void SortByConfiguredDefault<TItem>(
            List<TItem> items,
            PersistedSettings settings,
            GameSummariesSortSurface surface = GameSummariesSortSurface.Overview)
            where TItem : GameSummaryItem
        {
            var configuredSort = GetConfiguredDefaultSort(settings, surface);
            Sort(items, configuredSort.SortMemberPath, configuredSort.Direction);
        }

        public static void Sort(List<GameSummaryItem> items, GameSummariesSortMode mode, ListSortDirection direction)
        {
            var sort = new GameSummariesSortSpec(mode, direction);
            Sort(items, sort.SortMemberPath, sort.Direction);
        }

        internal static GameSummariesGridSortAction ResolveGridSortAction(
            string clickedSortMemberPath,
            string currentSortPath,
            ListSortDirection? currentSortDirection,
            PersistedSettings settings,
            GameSummariesSortSurface surface = GameSummariesSortSurface.Overview)
        {
            if (string.IsNullOrWhiteSpace(clickedSortMemberPath))
            {
                return GameSummariesGridSortAction.None();
            }

            var configuredSort = GetConfiguredDefaultSort(settings, surface);
            var cycleDirections = GetCycleDirections(clickedSortMemberPath, configuredSort);
            if (cycleDirections.Count == 0)
            {
                return GameSummariesGridSortAction.ResetToDefault();
            }

            if (!currentSortDirection.HasValue ||
                !string.Equals(currentSortPath, clickedSortMemberPath, StringComparison.Ordinal))
            {
                return GameSummariesGridSortAction.ApplySort(clickedSortMemberPath, cycleDirections[0]);
            }

            var currentDirectionIndex = cycleDirections.IndexOf(currentSortDirection.Value);
            if (currentDirectionIndex < 0 || currentDirectionIndex == cycleDirections.Count - 1)
            {
                return GameSummariesGridSortAction.ResetToDefault();
            }

            return GameSummariesGridSortAction.ApplySort(
                clickedSortMemberPath,
                cycleDirections[currentDirectionIndex + 1]);
        }

        // Sort-action resolution for grids whose default is the source collection's own order
        // (e.g. category summaries in provider/custom category order) rather than a configured
        // sort mode: every column cycles ascending -> descending -> reset to source order.
        internal static GameSummariesGridSortAction ResolveSourceOrderedGridSortAction(
            string clickedSortMemberPath,
            string currentSortPath,
            ListSortDirection? currentSortDirection)
        {
            if (string.IsNullOrWhiteSpace(clickedSortMemberPath))
            {
                return GameSummariesGridSortAction.None();
            }

            if (!currentSortDirection.HasValue ||
                !string.Equals(currentSortPath, clickedSortMemberPath, StringComparison.Ordinal))
            {
                return GameSummariesGridSortAction.ApplySort(
                    clickedSortMemberPath,
                    ListSortDirection.Ascending);
            }

            return currentSortDirection.Value == ListSortDirection.Ascending
                ? GameSummariesGridSortAction.ApplySort(clickedSortMemberPath, ListSortDirection.Descending)
                : GameSummariesGridSortAction.ResetToDefault();
        }

        public static bool TrySortItems<TItem>(
            List<TItem> items,
            string sortMemberPath,
            ListSortDirection direction,
            ref string currentSortPath,
            ref ListSortDirection currentSortDirection)
            where TItem : GameSummaryItem
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

            items.Sort((a, b) => comparison(a, b));
            currentSortPath = sortMemberPath;
            currentSortDirection = direction;
            return true;
        }

        private static bool TryReverse<TItem>(
            List<TItem> items,
            string sortMemberPath,
            ListSortDirection direction,
            ref string currentSortPath,
            ref ListSortDirection currentSortDirection)
            where TItem : GameSummaryItem
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
            GameSummariesSortSpec configuredSort)
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

        private static Comparison<GameSummaryItem> CreateComparison(string sortMemberPath, ListSortDirection direction)
        {
            return sortMemberPath switch
            {
                nameof(GameSummaryItem.LastUnlockUtc) => (a, b) => CompareWithTieBreakers(
                    a,
                    b,
                    sortMemberPath,
                    CompareDateTime(a?.LastUnlockUtc, b?.LastUnlockUtc, direction)),
                nameof(GameSummaryItem.LastPlayed) => (a, b) => CompareWithTieBreakers(
                    a,
                    b,
                    sortMemberPath,
                    CompareDateTime(a?.LastPlayed, b?.LastPlayed, direction)),
                nameof(GameSummaryItem.TotalAchievements) => (a, b) => CompareWithTieBreakers(
                    a,
                    b,
                    sortMemberPath,
                    CompareInt(a?.TotalAchievements ?? 0, b?.TotalAchievements ?? 0, direction)),
                nameof(GameSummaryItem.Progression) => (a, b) => CompareWithTieBreakers(
                    a,
                    b,
                    sortMemberPath,
                    CompareInt(a?.Progression ?? 0, b?.Progression ?? 0, direction)),
                nameof(GameSummaryItem.PlaytimeSeconds) => (a, b) => CompareWithTieBreakers(
                    a,
                    b,
                    sortMemberPath,
                    CompareULong(a?.PlaytimeSeconds ?? 0, b?.PlaytimeSeconds ?? 0, direction)),
                nameof(GameSummaryItem.UnlockedAchievements) => (a, b) => CompareWithTieBreakers(
                    a,
                    b,
                    sortMemberPath,
                    CompareInt(a?.UnlockedAchievements ?? 0, b?.UnlockedAchievements ?? 0, direction)),
                nameof(GameSummaryItem.CollectionScore) => (a, b) => CompareWithTieBreakers(
                    a,
                    b,
                    sortMemberPath,
                    CompareInt(a?.CollectionScore ?? 0, b?.CollectionScore ?? 0, direction)),
                nameof(GameSummaryItem.PrestigeScore) => (a, b) => CompareWithTieBreakers(
                    a,
                    b,
                    sortMemberPath,
                    CompareInt(a?.PrestigeScore ?? 0, b?.PrestigeScore ?? 0, direction)),
                nameof(GameSummaryItem.Points) => (a, b) => CompareWithTieBreakers(
                    a,
                    b,
                    sortMemberPath,
                    CompareInt(a?.Points ?? 0, b?.Points ?? 0, direction)),
                nameof(GameSummaryItem.Owned) => (a, b) => CompareWithTieBreakers(
                    a,
                    b,
                    sortMemberPath,
                    CompareBool(a?.Owned ?? false, b?.Owned ?? false, direction)),
                "SortingName" => (a, b) => CompareWithTieBreakers(
                    a,
                    b,
                    sortMemberPath,
                    CompareString(GetSortingName(a), GetSortingName(b), direction)),
                nameof(GameSummaryItem.GameName) => (a, b) => CompareWithTieBreakers(
                    a,
                    b,
                    sortMemberPath,
                    CompareString(a?.GameName, b?.GameName, direction)),
                "TrophyType" => (a, b) => CompareWithTieBreakers(
                    a,
                    b,
                    sortMemberPath,
                    CompareInt(
                        AchievementSortHelper.GetTrophyRank(GetPrimaryTrophyType(a)),
                        AchievementSortHelper.GetTrophyRank(GetPrimaryTrophyType(b)),
                        direction)),
                _ => null
            };
        }

        private static int CompareWithTieBreakers(
            GameSummaryItem a,
            GameSummaryItem b,
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

            if (!string.Equals(primarySortMemberPath, nameof(GameSummaryItem.LastUnlockUtc), StringComparison.Ordinal))
            {
                var comparison = CompareDateTime(a.LastUnlockUtc, b.LastUnlockUtc, ListSortDirection.Descending);
                if (comparison != 0)
                {
                    return comparison;
                }
            }

            if (!string.Equals(primarySortMemberPath, nameof(GameSummaryItem.LastPlayed), StringComparison.Ordinal))
            {
                var comparison = CompareDateTime(a.LastPlayed, b.LastPlayed, ListSortDirection.Descending);
                if (comparison != 0)
                {
                    return comparison;
                }
            }

            if (!string.Equals(primarySortMemberPath, nameof(GameSummaryItem.Progression), StringComparison.Ordinal))
            {
                var comparison = CompareInt(a.Progression, b.Progression, ListSortDirection.Descending);
                if (comparison != 0)
                {
                    return comparison;
                }
            }

            if (!string.Equals(primarySortMemberPath, nameof(GameSummaryItem.TotalAchievements), StringComparison.Ordinal))
            {
                var comparison = CompareInt(a.TotalAchievements, b.TotalAchievements, ListSortDirection.Descending);
                if (comparison != 0)
                {
                    return comparison;
                }
            }

            if (!string.Equals(primarySortMemberPath, nameof(GameSummaryItem.PlaytimeSeconds), StringComparison.Ordinal))
            {
                var comparison = CompareULong(a.PlaytimeSeconds, b.PlaytimeSeconds, ListSortDirection.Descending);
                if (comparison != 0)
                {
                    return comparison;
                }
            }

            if (!string.Equals(primarySortMemberPath, "SortingName", StringComparison.Ordinal) &&
                !string.Equals(primarySortMemberPath, nameof(GameSummaryItem.GameName), StringComparison.Ordinal))
            {
                var comparison = CompareString(GetSortingName(a), GetSortingName(b), ListSortDirection.Ascending);
                if (comparison != 0)
                {
                    return comparison;
                }
            }

            var gameNameComparison = CompareString(a.GameName, b.GameName, ListSortDirection.Ascending);
            if (gameNameComparison != 0)
            {
                return gameNameComparison;
            }

            var appIdComparison = CompareInt(a.AppId, b.AppId, ListSortDirection.Ascending);
            if (appIdComparison != 0)
            {
                return appIdComparison;
            }

            return CompareString(
                a.PlayniteGameId?.ToString("D"),
                b.PlayniteGameId?.ToString("D"),
                ListSortDirection.Ascending);
        }

        private static string GetSortingName(GameSummaryItem item)
        {
            return string.IsNullOrWhiteSpace(item?.SortingName)
                ? item?.GameName ?? string.Empty
                : item.SortingName;
        }

        private static string GetPrimaryTrophyType(GameSummaryItem item)
        {
            if (item == null)
            {
                return null;
            }

            if (item.TrophyPlatinumCount > 0)
            {
                return "platinum";
            }

            if (item.TrophyGoldCount > 0)
            {
                return "gold";
            }

            if (item.TrophySilverCount > 0)
            {
                return "silver";
            }

            if (item.TrophyBronzeCount > 0)
            {
                return "bronze";
            }

            return null;
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

        private static int CompareBool(bool left, bool right, ListSortDirection direction)
        {
            var comparison = left.CompareTo(right);
            return direction == ListSortDirection.Ascending ? comparison : -comparison;
        }

        private static int CompareULong(ulong left, ulong right, ListSortDirection direction)
        {
            var comparison = left.CompareTo(right);
            return direction == ListSortDirection.Ascending ? comparison : -comparison;
        }

        private static int CompareString(string left, string right, ListSortDirection direction)
        {
            var comparison = StringComparer.OrdinalIgnoreCase.Compare(left ?? string.Empty, right ?? string.Empty);
            return direction == ListSortDirection.Ascending ? comparison : -comparison;
        }

        private static void Sort<TItem>(List<TItem> items, string sortMemberPath, ListSortDirection direction)
            where TItem : GameSummaryItem
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

            items.Sort((a, b) => comparison(a, b));
        }
    }
}
