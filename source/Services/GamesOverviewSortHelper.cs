using System;
using System.Collections.Generic;
using System.ComponentModel;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.ViewModels;

namespace PlayniteAchievements.Services
{
    public readonly struct GamesOverviewSortSpec
    {
        public GamesOverviewSortSpec(GamesOverviewSortMode mode, ListSortDirection direction)
        {
            Mode = mode;
            Direction = direction;
        }

        public GamesOverviewSortMode Mode { get; }

        public ListSortDirection Direction { get; }

        public string SortMemberPath => Mode switch
        {
            GamesOverviewSortMode.RecentUnlock => nameof(GameOverviewItem.LastUnlockUtc),
            GamesOverviewSortMode.LastPlayed => nameof(GameOverviewItem.LastPlayed),
            GamesOverviewSortMode.TotalAchievements => nameof(GameOverviewItem.TotalAchievements),
            GamesOverviewSortMode.Progress => nameof(GameOverviewItem.Progression),
            GamesOverviewSortMode.Alphabetical => "SortingName",
            _ => nameof(GameOverviewItem.LastUnlockUtc)
        };

        public string IndicatorSortMemberPath => Mode switch
        {
            GamesOverviewSortMode.RecentUnlock => null,
            GamesOverviewSortMode.LastPlayed => nameof(GameOverviewItem.LastPlayed),
            GamesOverviewSortMode.TotalAchievements => nameof(GameOverviewItem.TotalAchievements),
            GamesOverviewSortMode.Progress => nameof(GameOverviewItem.Progression),
            GamesOverviewSortMode.Alphabetical => "SortingName",
            _ => null
        };
    }

    internal enum GamesOverviewGridSortActionKind
    {
        None,
        ApplySort,
        ResetToDefault
    }

    internal readonly struct GamesOverviewGridSortAction
    {
        private GamesOverviewGridSortAction(
            GamesOverviewGridSortActionKind kind,
            string sortMemberPath,
            ListSortDirection? direction)
        {
            Kind = kind;
            SortMemberPath = sortMemberPath;
            Direction = direction;
        }

        public GamesOverviewGridSortActionKind Kind { get; }

        public string SortMemberPath { get; }

        public ListSortDirection? Direction { get; }

        public static GamesOverviewGridSortAction None()
        {
            return new GamesOverviewGridSortAction(
                GamesOverviewGridSortActionKind.None,
                null,
                null);
        }

        public static GamesOverviewGridSortAction ApplySort(
            string sortMemberPath,
            ListSortDirection direction)
        {
            return new GamesOverviewGridSortAction(
                GamesOverviewGridSortActionKind.ApplySort,
                sortMemberPath,
                direction);
        }

        public static GamesOverviewGridSortAction ResetToDefault()
        {
            return new GamesOverviewGridSortAction(
                GamesOverviewGridSortActionKind.ResetToDefault,
                null,
                null);
        }
    }

    public static class GamesOverviewSortHelper
    {
        public static GamesOverviewSortSpec GetConfiguredDefaultSort(PersistedSettings settings)
        {
            if (settings == null)
            {
                return new GamesOverviewSortSpec(
                    GamesOverviewSortMode.RecentUnlock,
                    ListSortDirection.Descending);
            }

            return new GamesOverviewSortSpec(
                settings.GamesOverviewGridSortMode,
                settings.GamesOverviewGridSortDescending
                    ? ListSortDirection.Descending
                    : ListSortDirection.Ascending);
        }

        public static bool IsConfiguredDefaultSortPropertyName(string propertyName)
        {
            return propertyName == nameof(PersistedSettings.GamesOverviewGridSortMode) ||
                   propertyName == nameof(PersistedSettings.GamesOverviewGridSortDescending);
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

        public static void SortByConfiguredDefault(List<GameOverviewItem> items, PersistedSettings settings)
        {
            var configuredSort = GetConfiguredDefaultSort(settings);
            Sort(items, configuredSort.SortMemberPath, configuredSort.Direction);
        }

        internal static GamesOverviewGridSortAction ResolveGridSortAction(
            string clickedSortMemberPath,
            string currentSortPath,
            ListSortDirection? currentSortDirection,
            PersistedSettings settings)
        {
            if (string.IsNullOrWhiteSpace(clickedSortMemberPath))
            {
                return GamesOverviewGridSortAction.None();
            }

            var configuredSort = GetConfiguredDefaultSort(settings);
            var cycleDirections = GetCycleDirections(clickedSortMemberPath, configuredSort);
            if (cycleDirections.Count == 0)
            {
                return GamesOverviewGridSortAction.ResetToDefault();
            }

            if (!currentSortDirection.HasValue ||
                !string.Equals(currentSortPath, clickedSortMemberPath, StringComparison.Ordinal))
            {
                return GamesOverviewGridSortAction.ApplySort(clickedSortMemberPath, cycleDirections[0]);
            }

            var currentDirectionIndex = cycleDirections.IndexOf(currentSortDirection.Value);
            if (currentDirectionIndex < 0 || currentDirectionIndex == cycleDirections.Count - 1)
            {
                return GamesOverviewGridSortAction.ResetToDefault();
            }

            return GamesOverviewGridSortAction.ApplySort(
                clickedSortMemberPath,
                cycleDirections[currentDirectionIndex + 1]);
        }

        public static bool TrySortItems(
            List<GameOverviewItem> items,
            string sortMemberPath,
            ListSortDirection direction,
            ref string currentSortPath,
            ref ListSortDirection currentSortDirection)
        {
            return TrySortItems(items, sortMemberPath, direction, null, ref currentSortPath, ref currentSortDirection);
        }

        /// <summary>
        /// Sorts items by a primary column plus optional secondary columns (Ctrl+Click multi-sort).
        /// When secondarySorts is provided the primary comparison no longer bakes in automatic
        /// tie-breakers; instead the caller-supplied secondary columns are tried in order, and
        /// automatic tie-breakers are applied only after all explicit columns tie.
        /// </summary>
        public static bool TrySortItems(
            List<GameOverviewItem> items,
            string sortMemberPath,
            ListSortDirection direction,
            IReadOnlyList<(string Path, ListSortDirection Direction)> secondarySorts,
            ref string currentSortPath,
            ref ListSortDirection currentSortDirection)
        {
            if (items == null || string.IsNullOrWhiteSpace(sortMemberPath))
            {
                return false;
            }

            var hasSecondarySorts = secondarySorts != null && secondarySorts.Count > 0;

            // Optimise: if no secondary sorts and the direction is just a flip of the same column, reverse in-place
            if (!hasSecondarySorts && TryReverse(items, sortMemberPath, direction, ref currentSortPath, ref currentSortDirection))
            {
                return true;
            }

            Comparison<GameOverviewItem> comparison;
            if (hasSecondarySorts)
            {
                // Build raw comparers for primary + secondaries, then apply automatic tie-breakers last
                comparison = CreateMultiColumnComparison(sortMemberPath, direction, secondarySorts);
            }
            else
            {
                comparison = CreateComparison(sortMemberPath, direction);
            }

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
            List<GameOverviewItem> items,
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
            GamesOverviewSortSpec configuredSort)
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

        private static Comparison<GameOverviewItem> CreateComparison(string sortMemberPath, ListSortDirection direction)
        {
            return sortMemberPath switch
            {
                nameof(GameOverviewItem.LastUnlockUtc) => (a, b) => CompareWithTieBreakers(
                    a,
                    b,
                    sortMemberPath,
                    CompareDateTime(a?.LastUnlockUtc, b?.LastUnlockUtc, direction)),
                nameof(GameOverviewItem.LastPlayed) => (a, b) => CompareWithTieBreakers(
                    a,
                    b,
                    sortMemberPath,
                    CompareDateTime(a?.LastPlayed, b?.LastPlayed, direction)),
                nameof(GameOverviewItem.TotalAchievements) => (a, b) => CompareWithTieBreakers(
                    a,
                    b,
                    sortMemberPath,
                    CompareInt(a?.TotalAchievements ?? 0, b?.TotalAchievements ?? 0, direction)),
                nameof(GameOverviewItem.Progression) => (a, b) => CompareWithTieBreakers(
                    a,
                    b,
                    sortMemberPath,
                    CompareInt(a?.Progression ?? 0, b?.Progression ?? 0, direction)),
                nameof(GameOverviewItem.PlaytimeSeconds) => (a, b) => CompareWithTieBreakers(
                    a,
                    b,
                    sortMemberPath,
                    CompareULong(a?.PlaytimeSeconds ?? 0, b?.PlaytimeSeconds ?? 0, direction)),
                nameof(GameOverviewItem.UnlockedAchievements) => (a, b) => CompareWithTieBreakers(
                    a,
                    b,
                    sortMemberPath,
                    CompareInt(a?.UnlockedAchievements ?? 0, b?.UnlockedAchievements ?? 0, direction)),
                "SortingName" => (a, b) => CompareWithTieBreakers(
                    a,
                    b,
                    sortMemberPath,
                    CompareString(GetSortingName(a), GetSortingName(b), direction)),
                nameof(GameOverviewItem.GameName) => (a, b) => CompareWithTieBreakers(
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
            GameOverviewItem a,
            GameOverviewItem b,
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

            if (!string.Equals(primarySortMemberPath, nameof(GameOverviewItem.LastUnlockUtc), StringComparison.Ordinal))
            {
                var comparison = CompareDateTime(a.LastUnlockUtc, b.LastUnlockUtc, ListSortDirection.Descending);
                if (comparison != 0)
                {
                    return comparison;
                }
            }

            if (!string.Equals(primarySortMemberPath, nameof(GameOverviewItem.LastPlayed), StringComparison.Ordinal))
            {
                var comparison = CompareDateTime(a.LastPlayed, b.LastPlayed, ListSortDirection.Descending);
                if (comparison != 0)
                {
                    return comparison;
                }
            }

            if (!string.Equals(primarySortMemberPath, nameof(GameOverviewItem.Progression), StringComparison.Ordinal))
            {
                var comparison = CompareInt(a.Progression, b.Progression, ListSortDirection.Descending);
                if (comparison != 0)
                {
                    return comparison;
                }
            }

            if (!string.Equals(primarySortMemberPath, nameof(GameOverviewItem.TotalAchievements), StringComparison.Ordinal))
            {
                var comparison = CompareInt(a.TotalAchievements, b.TotalAchievements, ListSortDirection.Descending);
                if (comparison != 0)
                {
                    return comparison;
                }
            }

            if (!string.Equals(primarySortMemberPath, nameof(GameOverviewItem.PlaytimeSeconds), StringComparison.Ordinal))
            {
                var comparison = CompareULong(a.PlaytimeSeconds, b.PlaytimeSeconds, ListSortDirection.Descending);
                if (comparison != 0)
                {
                    return comparison;
                }
            }

            if (!string.Equals(primarySortMemberPath, "SortingName", StringComparison.Ordinal) &&
                !string.Equals(primarySortMemberPath, nameof(GameOverviewItem.GameName), StringComparison.Ordinal))
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

        private static string GetSortingName(GameOverviewItem item)
        {
            return string.IsNullOrWhiteSpace(item?.SortingName)
                ? item?.GameName ?? string.Empty
                : item.SortingName;
        }

        private static string GetPrimaryTrophyType(GameOverviewItem item)
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

        private static void Sort(List<GameOverviewItem> items, string sortMemberPath, ListSortDirection direction)
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

        /// <summary>
        /// Builds a chained comparison that evaluates primary then secondary columns, finally
        /// falling back to the automatic tie-breakers after all explicit columns tie.
        /// </summary>
        private static Comparison<GameOverviewItem> CreateMultiColumnComparison(
            string primaryPath,
            ListSortDirection primaryDirection,
            IReadOnlyList<(string Path, ListSortDirection Direction)> secondarySorts)
        {
            return (a, b) =>
            {
                if (ReferenceEquals(a, b)) return 0;
                if (a == null) return 1;
                if (b == null) return -1;

                // Primary column (raw, no built-in tie-breakers)
                var result = CompareRaw(a, b, primaryPath, primaryDirection);
                if (result != 0) return result;

                // Explicit secondary columns
                foreach (var (path, dir) in secondarySorts)
                {
                    result = CompareRaw(a, b, path, dir);
                    if (result != 0) return result;
                }

                // Automatic tie-breakers after all explicit columns tie
                return ApplyAutomaticTieBreakers(a, b, primaryPath);
            };
        }

        /// <summary>
        /// Returns the raw sort value for a single column without any tie-breaker chaining.
        /// </summary>
        private static int CompareRaw(GameOverviewItem a, GameOverviewItem b, string sortMemberPath, ListSortDirection direction)
        {
            return sortMemberPath switch
            {
                nameof(GameOverviewItem.LastUnlockUtc)    => CompareDateTime(a?.LastUnlockUtc, b?.LastUnlockUtc, direction),
                nameof(GameOverviewItem.LastPlayed)       => CompareDateTime(a?.LastPlayed, b?.LastPlayed, direction),
                nameof(GameOverviewItem.TotalAchievements)=> CompareInt(a?.TotalAchievements ?? 0, b?.TotalAchievements ?? 0, direction),
                nameof(GameOverviewItem.Progression)      => CompareInt(a?.Progression ?? 0, b?.Progression ?? 0, direction),
                nameof(GameOverviewItem.PlaytimeSeconds)  => CompareULong(a?.PlaytimeSeconds ?? 0, b?.PlaytimeSeconds ?? 0, direction),
                nameof(GameOverviewItem.UnlockedAchievements) => CompareInt(a?.UnlockedAchievements ?? 0, b?.UnlockedAchievements ?? 0, direction),
                "SortingName"                             => CompareString(GetSortingName(a), GetSortingName(b), direction),
                nameof(GameOverviewItem.GameName)         => CompareString(a?.GameName, b?.GameName, direction),
                "TrophyType"                              => CompareInt(
                    AchievementSortHelper.GetTrophyRank(GetPrimaryTrophyType(a)),
                    AchievementSortHelper.GetTrophyRank(GetPrimaryTrophyType(b)),
                    direction),
                _ => 0
            };
        }

        /// <summary>
        /// Applies the fixed automatic tie-breakers that are independent of user column choices.
        /// Skips any tie-breaker that duplicates the column the user explicitly sorted by.
        /// </summary>
        private static int ApplyAutomaticTieBreakers(GameOverviewItem a, GameOverviewItem b, string primarySortMemberPath)
        {
            if (!string.Equals(primarySortMemberPath, nameof(GameOverviewItem.LastUnlockUtc), StringComparison.Ordinal))
            {
                var c = CompareDateTime(a.LastUnlockUtc, b.LastUnlockUtc, ListSortDirection.Descending);
                if (c != 0) return c;
            }

            if (!string.Equals(primarySortMemberPath, nameof(GameOverviewItem.LastPlayed), StringComparison.Ordinal))
            {
                var c = CompareDateTime(a.LastPlayed, b.LastPlayed, ListSortDirection.Descending);
                if (c != 0) return c;
            }

            if (!string.Equals(primarySortMemberPath, nameof(GameOverviewItem.Progression), StringComparison.Ordinal))
            {
                var c = CompareInt(a.Progression, b.Progression, ListSortDirection.Descending);
                if (c != 0) return c;
            }

            if (!string.Equals(primarySortMemberPath, nameof(GameOverviewItem.TotalAchievements), StringComparison.Ordinal))
            {
                var c = CompareInt(a.TotalAchievements, b.TotalAchievements, ListSortDirection.Descending);
                if (c != 0) return c;
            }

            if (!string.Equals(primarySortMemberPath, nameof(GameOverviewItem.PlaytimeSeconds), StringComparison.Ordinal))
            {
                var c = CompareULong(a.PlaytimeSeconds, b.PlaytimeSeconds, ListSortDirection.Descending);
                if (c != 0) return c;
            }

            if (!string.Equals(primarySortMemberPath, "SortingName", StringComparison.Ordinal) &&
                !string.Equals(primarySortMemberPath, nameof(GameOverviewItem.GameName), StringComparison.Ordinal))
            {
                var c = CompareString(GetSortingName(a), GetSortingName(b), ListSortDirection.Ascending);
                if (c != 0) return c;
            }

            var gameNameC = CompareString(a.GameName, b.GameName, ListSortDirection.Ascending);
            if (gameNameC != 0) return gameNameC;

            var appIdC = CompareInt(a.AppId, b.AppId, ListSortDirection.Ascending);
            if (appIdC != 0) return appIdC;

            return CompareString(
                a.PlayniteGameId?.ToString("D"),
                b.PlayniteGameId?.ToString("D"),
                ListSortDirection.Ascending);
        }
    }
}
