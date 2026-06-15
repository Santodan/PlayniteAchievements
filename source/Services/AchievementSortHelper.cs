using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Models.ThemeIntegration;
using PlayniteAchievements.ViewModels;

namespace PlayniteAchievements.Services
{
    public enum AchievementSortScope
    {
        GameAchievements,
        RecentAchievements
    }

    public enum AchievementSortSurface
    {
        CompactList,
        CompactUnlockedList,
        CompactLockedList,
        OverviewSelectedGame,
        OverviewRecentAchievements,
        SingleGame,
        AchievementDataGrid
    }

    public struct AchievementSortSpec
    {
        public AchievementSortSpec(CompactListSortMode mode, ListSortDirection direction)
        {
            Mode = mode;
            Direction = direction;
        }

        public CompactListSortMode Mode { get; }

        public ListSortDirection Direction { get; }

        public string SortMemberPath => Mode switch
        {
            CompactListSortMode.UnlockTime => nameof(AchievementDisplayItem.UnlockTime),
            CompactListSortMode.Rarity => nameof(AchievementDisplayItem.RaritySortValue),
            _ => null
        };

        public bool PreservesSourceOrder => string.IsNullOrWhiteSpace(SortMemberPath);
    }

    internal enum AchievementGridSortActionKind
    {
        None,
        ApplySort,
        ResetToDefault
    }

    internal readonly struct AchievementGridSortAction
    {
        private AchievementGridSortAction(
            AchievementGridSortActionKind kind,
            string sortMemberPath,
            ListSortDirection? direction)
        {
            Kind = kind;
            SortMemberPath = sortMemberPath;
            Direction = direction;
        }

        public AchievementGridSortActionKind Kind { get; }

        public string SortMemberPath { get; }

        public ListSortDirection? Direction { get; }

        public static AchievementGridSortAction None()
        {
            return new AchievementGridSortAction(
                AchievementGridSortActionKind.None,
                null,
                null);
        }

        public static AchievementGridSortAction ApplySort(
            string sortMemberPath,
            ListSortDirection direction)
        {
            return new AchievementGridSortAction(
                AchievementGridSortActionKind.ApplySort,
                sortMemberPath,
                direction);
        }

        public static AchievementGridSortAction ResetToDefault()
        {
            return new AchievementGridSortAction(
                AchievementGridSortActionKind.ResetToDefault,
                null,
                null);
        }
    }

    /// <summary>
    /// Centralized sort logic for achievement lists and display items.
    /// </summary>
    public static class AchievementSortHelper
    {
        public static AchievementSortSpec GetConfiguredDefaultSort(
            PersistedSettings settings,
            AchievementSortSurface surface)
        {
            if (settings == null)
            {
                return surface switch
                {
                    AchievementSortSurface.OverviewSelectedGame => new AchievementSortSpec(CompactListSortMode.UnlockTime, ListSortDirection.Descending),
                    AchievementSortSurface.OverviewRecentAchievements => new AchievementSortSpec(CompactListSortMode.UnlockTime, ListSortDirection.Descending),
                    AchievementSortSurface.SingleGame => new AchievementSortSpec(CompactListSortMode.UnlockTime, ListSortDirection.Descending),
                    AchievementSortSurface.AchievementDataGrid => new AchievementSortSpec(CompactListSortMode.UnlockTime, ListSortDirection.Descending),
                    _ => new AchievementSortSpec(CompactListSortMode.None, ListSortDirection.Ascending)
                };
            }

            return surface switch
            {
                AchievementSortSurface.CompactList => new AchievementSortSpec(
                    settings.CompactListSortMode,
                    settings.CompactListSortDescending ? ListSortDirection.Descending : ListSortDirection.Ascending),
                AchievementSortSurface.CompactUnlockedList => new AchievementSortSpec(
                    settings.CompactUnlockedListSortMode,
                    settings.CompactUnlockedListSortDescending ? ListSortDirection.Descending : ListSortDirection.Ascending),
                AchievementSortSurface.CompactLockedList => new AchievementSortSpec(
                    settings.CompactLockedListSortMode,
                    settings.CompactLockedListSortDescending ? ListSortDirection.Descending : ListSortDirection.Ascending),
                AchievementSortSurface.OverviewSelectedGame => new AchievementSortSpec(
                    settings.OverviewSelectedGameGridSortMode,
                    settings.OverviewSelectedGameGridSortDescending ? ListSortDirection.Descending : ListSortDirection.Ascending),
                AchievementSortSurface.OverviewRecentAchievements => new AchievementSortSpec(
                    CompactListSortMode.UnlockTime,
                    ListSortDirection.Descending),
                AchievementSortSurface.SingleGame => new AchievementSortSpec(
                    settings.SingleGameGridSortMode,
                    settings.SingleGameGridSortDescending ? ListSortDirection.Descending : ListSortDirection.Ascending),
                AchievementSortSurface.AchievementDataGrid => new AchievementSortSpec(
                    settings.AchievementDataGridSortMode,
                    settings.AchievementDataGridSortDescending ? ListSortDirection.Descending : ListSortDirection.Ascending),
                _ => new AchievementSortSpec(CompactListSortMode.None, ListSortDirection.Ascending)
            };
        }

        public static void ApplySortIndicator(
            string currentSortPath,
            ListSortDirection? currentSortDirection,
            PersistedSettings settings,
            AchievementSortSurface surface,
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

            var configuredSort = GetConfiguredDefaultSort(settings, surface);
            applyIndicator(
                configuredSort.SortMemberPath,
                configuredSort.PreservesSourceOrder ? (ListSortDirection?)null : configuredSort.Direction);
        }

        internal static AchievementGridSortAction ResolveGridSortAction(
            string clickedSortMemberPath,
            string currentSortPath,
            ListSortDirection? currentSortDirection,
            PersistedSettings settings,
            AchievementSortSurface surface,
            ListSortDirection? visibleSortDirection = null)
        {
            if (string.IsNullOrWhiteSpace(clickedSortMemberPath))
            {
                return AchievementGridSortAction.None();
            }

            var configuredSort = GetConfiguredDefaultSort(settings, surface);
            var isConfiguredDefaultColumn =
                !configuredSort.PreservesSourceOrder &&
                string.Equals(configuredSort.SortMemberPath, clickedSortMemberPath, StringComparison.Ordinal);

            if (!currentSortDirection.HasValue ||
                !string.Equals(currentSortPath, clickedSortMemberPath, StringComparison.Ordinal))
            {
                if (isConfiguredDefaultColumn)
                {
                    var direction = visibleSortDirection == configuredSort.Direction
                        ? GetOppositeDirection(configuredSort.Direction)
                        : configuredSort.Direction;

                    return AchievementGridSortAction.ApplySort(clickedSortMemberPath, direction);
                }

                var initialCycleDirections = GetCycleDirections();
                return AchievementGridSortAction.ApplySort(clickedSortMemberPath, initialCycleDirections[0]);
            }

            if (isConfiguredDefaultColumn)
            {
                return currentSortDirection.Value == configuredSort.Direction
                    ? AchievementGridSortAction.ApplySort(
                        clickedSortMemberPath,
                        GetOppositeDirection(configuredSort.Direction))
                    : AchievementGridSortAction.ResetToDefault();
            }

            var cycleDirections = GetCycleDirections();
            var currentDirectionIndex = cycleDirections.IndexOf(currentSortDirection.Value);
            if (currentDirectionIndex < 0 || currentDirectionIndex == cycleDirections.Count - 1)
            {
                return AchievementGridSortAction.ResetToDefault();
            }

            return AchievementGridSortAction.ApplySort(
                clickedSortMemberPath,
                cycleDirections[currentDirectionIndex + 1]);
        }

        public static List<AchievementDetail> ResolveSelectedGameAchievements(
            ModernThemeBindings theme,
            PersistedSettings settings,
            AchievementSortSurface surface)
        {
            return ResolveSelectedGameAchievements(theme, GetConfiguredDefaultSort(settings, surface));
        }

        internal static List<AchievementDetail> ResolveSelectedGameAchievements(
            SelectedGameRuntimeState state,
            string sortKey,
            string sortDirectionKey)
        {
            return ResolveSelectedGameAchievements(
                state,
                CreateSelectedGameSort(sortKey, sortDirectionKey));
        }

        internal static List<AchievementDetail> ResolveLibraryAchievements(
            LibraryRuntimeState state,
            string sortKey,
            string sortDirectionKey)
        {
            if (state == null || !state.HasData)
            {
                return new List<AchievementDetail>();
            }

            var isAscending = string.Equals(sortDirectionKey, DynamicThemeViewKeys.Ascending, StringComparison.Ordinal);
            return sortKey switch
            {
                DynamicThemeViewKeys.Rarity => isAscending
                    ? state.AllAchievementsRarityAsc ?? GetDefaultLibraryAchievements(state)
                    : state.AllAchievementsRarityDesc ?? GetDefaultLibraryAchievements(state),
                _ => isAscending
                    ? state.AllAchievementsUnlockAsc ?? GetDefaultLibraryAchievements(state)
                    : state.AllAchievementsUnlockDesc ?? GetDefaultLibraryAchievements(state)
            };
        }

        public static bool IsSelectedGameAchievementsPropertyName(string propertyName)
        {
            return propertyName == nameof(ModernThemeBindings.AchievementDefaultOrder) ||
                   propertyName == nameof(ModernThemeBindings.AchievementsNewestFirst) ||
                   propertyName == nameof(ModernThemeBindings.AchievementsOldestFirst) ||
                   propertyName == nameof(ModernThemeBindings.AchievementsRarityAsc) ||
                   propertyName == nameof(ModernThemeBindings.AchievementsRarityDesc);
        }

        public static void ApplyConfiguredDefaultSort(
            List<AchievementDisplayItem> items,
            PersistedSettings settings,
            AchievementSortSurface surface,
            AchievementSortScope scope,
            IReadOnlyList<string> explicitOrder = null,
            IReadOnlyDictionary<AchievementDisplayItem, int> stableOrder = null)
        {
            if (items == null || items.Count == 0)
            {
                return;
            }

            var configuredSort = GetConfiguredDefaultSort(settings, surface);
            if (configuredSort.PreservesSourceOrder)
            {
                ApplyExplicitOrder(items, explicitOrder);
                return;
            }

            var comparison = GetComparison(configuredSort.SortMemberPath, configuredSort.Direction, scope);
            if (comparison != null)
            {
                items.Sort(WithStableOrder(comparison, stableOrder));
            }
        }

        public static Dictionary<AchievementDisplayItem, int> CreateStableOrderMap(
            IEnumerable<AchievementDisplayItem> items)
        {
            var map = new Dictionary<AchievementDisplayItem, int>();
            if (items == null)
            {
                return map;
            }

            var index = 0;
            foreach (var item in items)
            {
                if (item != null && !map.ContainsKey(item))
                {
                    map[item] = index++;
                }
            }

            return map;
        }

        public static List<string> CreateExplicitOrderKeys(IEnumerable<AchievementDetail> items)
        {
            return AchievementOrderHelper.NormalizeApiNames(
                items?.Select(item => item == null
                    ? null
                    : AchievementDisplayItem.MakeRevealKey(item.Game?.Id, item.ApiName, item.Game?.Name)));
        }

        public static void ApplyExplicitOrder(
            List<AchievementDisplayItem> items,
            IReadOnlyList<string> explicitOrder)
        {
            if (items == null || items.Count == 0 || explicitOrder == null || explicitOrder.Count == 0)
            {
                return;
            }

            var orderedItems = AchievementOrderHelper.ApplyOrder(
                items,
                item => item == null
                    ? null
                    : AchievementDisplayItem.MakeRevealKey(item.PlayniteGameId, item.ApiName, item.GameName),
                explicitOrder);

            if (!ReferenceEquals(orderedItems, items))
            {
                items.Clear();
                items.AddRange(orderedItems);
            }
        }

        public static bool IsConfiguredDefaultSortPropertyName(
            string propertyName,
            AchievementSortSurface surface)
        {
            return surface switch
            {
                AchievementSortSurface.CompactList =>
                    propertyName == nameof(PersistedSettings.CompactListSortMode) ||
                    propertyName == nameof(PersistedSettings.CompactListSortDescending),
                AchievementSortSurface.CompactUnlockedList =>
                    propertyName == nameof(PersistedSettings.CompactUnlockedListSortMode) ||
                    propertyName == nameof(PersistedSettings.CompactUnlockedListSortDescending),
                AchievementSortSurface.CompactLockedList =>
                    propertyName == nameof(PersistedSettings.CompactLockedListSortMode) ||
                    propertyName == nameof(PersistedSettings.CompactLockedListSortDescending),
                AchievementSortSurface.OverviewSelectedGame =>
                    propertyName == nameof(PersistedSettings.OverviewSelectedGameGridSortMode) ||
                    propertyName == nameof(PersistedSettings.OverviewSelectedGameGridSortDescending),
                AchievementSortSurface.OverviewRecentAchievements => false,
                AchievementSortSurface.SingleGame =>
                    propertyName == nameof(PersistedSettings.SingleGameGridSortMode) ||
                    propertyName == nameof(PersistedSettings.SingleGameGridSortDescending),
                AchievementSortSurface.AchievementDataGrid =>
                    propertyName == nameof(PersistedSettings.AchievementDataGridSortMode) ||
                    propertyName == nameof(PersistedSettings.AchievementDataGridSortDescending),
                _ => false
            };
        }

        private static AchievementSortSpec CreateSelectedGameSort(
            string sortKey,
            string sortDirectionKey)
        {
            var mode = sortKey switch
            {
                DynamicThemeViewKeys.UnlockTime => CompactListSortMode.UnlockTime,
                DynamicThemeViewKeys.Rarity => CompactListSortMode.Rarity,
                _ => CompactListSortMode.None
            };

            var direction = string.Equals(sortDirectionKey, DynamicThemeViewKeys.Ascending, StringComparison.Ordinal)
                ? ListSortDirection.Ascending
                : ListSortDirection.Descending;

            return new AchievementSortSpec(mode, direction);
        }

        private static List<AchievementDetail> ResolveSelectedGameAchievements(
            ModernThemeBindings theme,
            AchievementSortSpec configuredSort)
        {
            if (theme == null)
            {
                return new List<AchievementDetail>();
            }

            return configuredSort.Mode switch
            {
                CompactListSortMode.UnlockTime => configuredSort.Direction == ListSortDirection.Descending
                    ? GetAvailableAchievements(theme.AchievementsNewestFirst, GetDefaultSelectedGameAchievements(theme))
                    : GetAvailableAchievements(theme.AchievementsOldestFirst, GetDefaultSelectedGameAchievements(theme)),
                CompactListSortMode.Rarity => configuredSort.Direction == ListSortDirection.Descending
                    ? GetAvailableAchievements(theme.AchievementsRarityDesc, GetDefaultSelectedGameAchievements(theme))
                    : GetAvailableAchievements(theme.AchievementsRarityAsc, GetDefaultSelectedGameAchievements(theme)),
                _ => GetDefaultSelectedGameAchievements(theme)
            };
        }

        private static List<AchievementDetail> ResolveSelectedGameAchievements(
            SelectedGameRuntimeState state,
            AchievementSortSpec configuredSort)
        {
            if (state == null || !state.HasAchievements)
            {
                return new List<AchievementDetail>();
            }

            return configuredSort.Mode switch
            {
                CompactListSortMode.UnlockTime => configuredSort.Direction == ListSortDirection.Descending
                    ? GetAvailableAchievements(state.AchievementsNewestFirst, GetDefaultSelectedGameAchievements(state))
                    : GetAvailableAchievements(state.AchievementsOldestFirst, GetDefaultSelectedGameAchievements(state)),
                CompactListSortMode.Rarity => configuredSort.Direction == ListSortDirection.Descending
                    ? GetAvailableAchievements(state.AchievementsRarityDesc, GetDefaultSelectedGameAchievements(state))
                    : GetAvailableAchievements(state.AchievementsRarityAsc, GetDefaultSelectedGameAchievements(state)),
                _ => GetDefaultSelectedGameAchievements(state)
            };
        }

        private static List<AchievementDetail> GetDefaultSelectedGameAchievements(ModernThemeBindings theme)
        {
            return GetAvailableAchievements(theme?.AchievementDefaultOrder, theme?.AllAchievements);
        }

        private static List<AchievementDetail> GetDefaultSelectedGameAchievements(SelectedGameRuntimeState state)
        {
            return GetAvailableAchievements(state?.AchievementDefaultOrder, state?.AllAchievements);
        }

        private static List<AchievementDetail> GetDefaultLibraryAchievements(LibraryRuntimeState state)
        {
            return GetAvailableAchievements(state?.AllAchievementsUnlockDesc, state?.AllAchievementsUnlockAsc);
        }

        private static List<AchievementDetail> GetAvailableAchievements(
            List<AchievementDetail> primary,
            List<AchievementDetail> fallback)
        {
            if (primary != null && primary.Count > 0)
            {
                return primary;
            }

            if (fallback != null && fallback.Count > 0)
            {
                return fallback;
            }

            return primary ?? fallback ?? new List<AchievementDetail>();
        }

        public static bool TrySortItems(
            List<AchievementDisplayItem> items,
            string sortMemberPath,
            ListSortDirection direction,
            AchievementSortScope scope,
            ref string currentSortPath,
            ref ListSortDirection? currentSortDirection,
            IReadOnlyDictionary<AchievementDisplayItem, int> stableOrder = null)
        {
            if (items == null || items.Count == 0 || string.IsNullOrWhiteSpace(sortMemberPath))
            {
                return false;
            }

            if (SupportsQuickReverse(sortMemberPath) &&
                currentSortPath == sortMemberPath &&
                currentSortDirection == ListSortDirection.Ascending &&
                direction == ListSortDirection.Descending)
            {
                items.Reverse();
                currentSortDirection = direction;
                return true;
            }

            var comparison = GetComparison(sortMemberPath, direction, scope);
            if (comparison == null)
            {
                return false;
            }

            currentSortPath = sortMemberPath;
            currentSortDirection = direction;
            items.Sort(WithStableOrder(comparison, stableOrder));
            return true;
        }

        public static Comparison<AchievementDisplayItem> GetComparison(
            string sortMemberPath,
            ListSortDirection direction,
            AchievementSortScope scope)
        {
            if (string.IsNullOrWhiteSpace(sortMemberPath))
            {
                return null;
            }

            return sortMemberPath switch
            {
                "DisplayName" => ApplyDirection(CompareByDisplayName, direction),
                "SortingName" when scope == AchievementSortScope.RecentAchievements
                    => ApplyDirection(CompareBySortingName, direction),
                "CategoryType" => ApplyDirection(
                    (a, b) => CompareByCategoryTypeThenUnlock(a, b, scope),
                    direction),
                "CategoryLabel" => ApplyDirection(
                    (a, b) => CompareByCategoryLabelThenUnlock(a, b, scope),
                    direction),
                "UnlockTime" => (a, b) => CompareByUnlockTime(a, b, direction, scope),
                "GlobalPercent" => ApplyDirection(
                    (a, b) => a.RaritySortValue.CompareTo(b.RaritySortValue),
                    direction),
                "RaritySortValue" => ApplyDirection(
                    (a, b) => a.RaritySortValue.CompareTo(b.RaritySortValue),
                    direction),
                "CollectionScore" => ApplyDirection(
                    (a, b) => a.CollectionScore.CompareTo(b.CollectionScore),
                    direction),
                "PrestigeScore" => ApplyDirection(
                    (a, b) => a.PrestigeScore.CompareTo(b.PrestigeScore),
                    direction),
                "Points" => ApplyDirection(
                    (a, b) => a.Points.CompareTo(b.Points),
                    direction),
                "TrophyType" => (a, b) => CompareByTrophyType(a, b, direction),
                _ => null
            };
        }

        public static List<AchievementDetail> CreateSortedDetailList(
            IEnumerable<AchievementDetail> items,
            string sortMemberPath,
            ListSortDirection direction,
            bool includeGameNameTieBreak = false)
        {
            var list = items?.ToList() ?? new List<AchievementDetail>();
            if (list.Count == 0)
            {
                return list;
            }

            return sortMemberPath switch
            {
                "UnlockTime" => CreateDetailUnlockSortedList(list, direction, includeGameNameTieBreak),
                "GlobalPercent" => CreateDetailRaritySortedList(list, direction, includeGameNameTieBreak),
                "RaritySortValue" => CreateDetailRaritySortedList(list, direction, includeGameNameTieBreak),
                _ => list
            };
        }

        public static Comparison<AchievementDisplayItem> WithStableOrder(
            Comparison<AchievementDisplayItem> comparison,
            IReadOnlyDictionary<AchievementDisplayItem, int> stableOrder)
        {
            if (comparison == null || stableOrder == null || stableOrder.Count == 0)
            {
                return comparison;
            }

            return (a, b) =>
            {
                var result = comparison(a, b);
                if (result != 0)
                {
                    return result;
                }

                if (stableOrder.TryGetValue(a, out var aIndex) &&
                    stableOrder.TryGetValue(b, out var bIndex))
                {
                    return aIndex.CompareTo(bIndex);
                }

                return 0;
            };
        }

        public static List<AchievementDisplayItem> CreateDefaultSortedList(
            IEnumerable<AchievementDisplayItem> items,
            AchievementSortScope scope)
        {
            var list = items?.ToList() ?? new List<AchievementDisplayItem>();
            if (list.Count == 0)
            {
                return list;
            }

            list.Sort(scope == AchievementSortScope.RecentAchievements
                ? GetComparison(nameof(AchievementDisplayItem.UnlockTime), ListSortDirection.Descending, scope)
                : CompareByDefaultGameOrder);
            return list;
        }

        public static List<AchievementDetail> CreateDefaultSortedDetailList(
            IEnumerable<AchievementDetail> items)
        {
            var list = items?.ToList() ?? new List<AchievementDetail>();
            if (list.Count == 0)
            {
                return list;
            }

            var sorted = list
                .Select(detail => (Detail: detail, SortProxy: CreateDefaultSortProxy(detail)))
                .ToList();

            sorted.Sort((left, right) => CompareByDefaultGameOrder(left.SortProxy, right.SortProxy));
            return sorted.Select(entry => entry.Detail).ToList();
        }

        private static List<ListSortDirection> GetCycleDirections()
        {
            return new List<ListSortDirection>
            {
                ListSortDirection.Ascending,
                ListSortDirection.Descending
            };
        }

        private static ListSortDirection GetOppositeDirection(ListSortDirection direction)
        {
            return direction == ListSortDirection.Ascending
                ? ListSortDirection.Descending
                : ListSortDirection.Ascending;
        }

        public static int GetTrophyRank(string trophyType)
        {
            if (string.IsNullOrWhiteSpace(trophyType))
            {
                return 0;
            }

            return trophyType.ToLowerInvariant() switch
            {
                "platinum" => 4,
                "gold" => 3,
                "silver" => 2,
                "bronze" => 1,
                _ => 0
            };
        }

        private static bool SupportsQuickReverse(string sortMemberPath)
        {
            return !string.Equals(sortMemberPath, nameof(AchievementDisplayItem.UnlockTime), StringComparison.Ordinal) &&
                   !string.Equals(sortMemberPath, nameof(AchievementDisplayItem.TrophyType), StringComparison.Ordinal);
        }

        private static Comparison<AchievementDisplayItem> ApplyDirection(
            Comparison<AchievementDisplayItem> comparison,
            ListSortDirection direction)
        {
            return direction == ListSortDirection.Ascending
                ? comparison
                : (a, b) => comparison(b, a);
        }

        private static int CompareByDefaultGameOrder(AchievementDisplayItem a, AchievementDisplayItem b)
        {
            var unlockedComparison = b.Unlocked.CompareTo(a.Unlocked);
            if (unlockedComparison != 0)
            {
                return unlockedComparison;
            }

            return CompareByUnlockTime(a, b, ListSortDirection.Descending, AchievementSortScope.GameAchievements);
        }

        private static int CompareByDisplayName(AchievementDisplayItem a, AchievementDisplayItem b)
        {
            return CompareText(a?.DisplayName, b?.DisplayName);
        }

        private static int CompareBySortingName(AchievementDisplayItem a, AchievementDisplayItem b)
        {
            return CompareText(
                a?.SortingName ?? a?.GameName,
                b?.SortingName ?? b?.GameName);
        }

        private static int CompareByCategoryTypeThenUnlock(
            AchievementDisplayItem a,
            AchievementDisplayItem b,
            AchievementSortScope scope)
        {
            var typeComparison = CompareText(
                AchievementCategoryTypeHelper.ToDisplayText(a?.CategoryType),
                AchievementCategoryTypeHelper.ToDisplayText(b?.CategoryType));
            if (typeComparison != 0)
            {
                return typeComparison;
            }

            return CompareByUnlockTime(a, b, ListSortDirection.Ascending, scope);
        }

        private static int CompareByCategoryLabelThenUnlock(
            AchievementDisplayItem a,
            AchievementDisplayItem b,
            AchievementSortScope scope)
        {
            var labelComparison = CompareText(
                AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(a?.CategoryLabel),
                AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(b?.CategoryLabel));
            if (labelComparison != 0)
            {
                return labelComparison;
            }

            return CompareByUnlockTime(a, b, ListSortDirection.Ascending, scope);
        }

        private static int CompareByUnlockTime(
            AchievementDisplayItem a,
            AchievementDisplayItem b,
            ListSortDirection direction,
            AchievementSortScope scope)
        {
            var aUnlockTime = a?.UnlockTime ?? DateTime.MinValue;
            var bUnlockTime = b?.UnlockTime ?? DateTime.MinValue;
            var unlockComparison = direction == ListSortDirection.Ascending
                ? aUnlockTime.CompareTo(bUnlockTime)
                : bUnlockTime.CompareTo(aUnlockTime);
            if (unlockComparison != 0)
            {
                return unlockComparison;
            }

            var tieBreakComparison = CompareUnlockTieBreakers(a, b);
            if (tieBreakComparison != 0)
            {
                return tieBreakComparison;
            }

            if (scope == AchievementSortScope.RecentAchievements)
            {
                var gameComparison = CompareText(a?.GameName, b?.GameName);
                if (gameComparison != 0)
                {
                    return gameComparison;
                }
            }
            else
            {
                var unlockedComparison = (b?.Unlocked ?? false).CompareTo(a?.Unlocked ?? false);
                if (unlockedComparison != 0)
                {
                    return unlockedComparison;
                }
            }

            return CompareByDisplayName(a, b);
        }

        private static int CompareUnlockTieBreakers(AchievementDisplayItem a, AchievementDisplayItem b)
        {
            var progressComparison = CompareProgressFractionDescending(
                a?.ProgressNum,
                a?.ProgressDenom,
                b?.ProgressNum,
                b?.ProgressDenom);
            if (progressComparison != 0)
            {
                return progressComparison;
            }

            var rarityComparison = a.RaritySortValue.CompareTo(b.RaritySortValue);
            if (rarityComparison != 0)
            {
                return rarityComparison;
            }

            var trophyComparison = GetTrophyRank(b?.TrophyType).CompareTo(GetTrophyRank(a?.TrophyType));
            if (trophyComparison != 0)
            {
                return trophyComparison;
            }

            return (b?.Points ?? 0).CompareTo(a?.Points ?? 0);
        }

        private static int CompareByTrophyType(
            AchievementDisplayItem a,
            AchievementDisplayItem b,
            ListSortDirection direction)
        {
            var trophyComparison = direction == ListSortDirection.Ascending
                ? GetTrophyRank(a?.TrophyType).CompareTo(GetTrophyRank(b?.TrophyType))
                : GetTrophyRank(b?.TrophyType).CompareTo(GetTrophyRank(a?.TrophyType));
            if (trophyComparison != 0)
            {
                return trophyComparison;
            }

            var rarityComparison = a.RaritySortValue.CompareTo(b.RaritySortValue);
            if (rarityComparison != 0)
            {
                return rarityComparison;
            }

            var progressComparison = CompareProgressFractionDescending(
                a?.ProgressNum,
                a?.ProgressDenom,
                b?.ProgressNum,
                b?.ProgressDenom);
            if (progressComparison != 0)
            {
                return progressComparison;
            }

            var pointsComparison = (b?.Points ?? 0).CompareTo(a?.Points ?? 0);
            if (pointsComparison != 0)
            {
                return pointsComparison;
            }

            var unlockComparison = (b?.UnlockTime ?? DateTime.MinValue).CompareTo(a?.UnlockTime ?? DateTime.MinValue);
            if (unlockComparison != 0)
            {
                return unlockComparison;
            }

            return CompareByDisplayName(a, b);
        }

        private static AchievementDisplayItem CreateDefaultSortProxy(AchievementDetail detail)
        {
            return new AchievementDisplayItem
            {
                DisplayName = detail?.DisplayName,
                UnlockTimeUtc = detail?.UnlockTimeUtc,
                GlobalPercentUnlocked = detail?.GlobalPercentUnlocked,
                Rarity = detail?.Rarity ?? RarityTier.Common,
                Unlocked = detail?.Unlocked ?? false,
                TrophyType = detail?.TrophyType,
                PointsValue = detail?.Points,
                ProgressNum = detail?.ProgressNum,
                ProgressDenom = detail?.ProgressDenom
            };
        }

        private static List<AchievementDetail> CreateDetailUnlockSortedList(
            IEnumerable<AchievementDetail> items,
            ListSortDirection direction,
            bool includeGameNameTieBreak)
        {
            IOrderedEnumerable<AchievementDetail> ordered = direction == ListSortDirection.Ascending
                ? items.OrderBy(a => a?.UnlockTimeUtc ?? DateTime.MinValue)
                : items.OrderByDescending(a => a?.UnlockTimeUtc ?? DateTime.MinValue);

            ordered = ordered
                .ThenByDescending(a => HasProgress(a?.ProgressNum, a?.ProgressDenom))
                .ThenByDescending(a => GetProgressFraction(a?.ProgressNum, a?.ProgressDenom) ?? 0)
                .ThenBy(a => a?.RaritySortValue ?? double.MaxValue)
                .ThenByDescending(a => GetTrophyRank(a?.TrophyType))
                .ThenByDescending(a => a?.Points ?? 0);
            if (includeGameNameTieBreak)
            {
                ordered = ordered.ThenBy(a => a?.Game?.Name, StringComparer.OrdinalIgnoreCase);
            }

            return ordered
                .ThenByDescending(a => a?.Unlocked ?? false)
                .ThenBy(a => a?.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static List<AchievementDetail> CreateDetailRaritySortedList(
            IEnumerable<AchievementDetail> items,
            ListSortDirection direction,
            bool includeGameNameTieBreak)
        {
            IOrderedEnumerable<AchievementDetail> ordered = direction == ListSortDirection.Ascending
                ? items.OrderBy(a => a?.RaritySortValue ?? double.MaxValue)
                : items.OrderByDescending(a => a?.RaritySortValue ?? double.MaxValue);

            ordered = ordered
                .ThenByDescending(a => a?.Points ?? 0)
                .ThenByDescending(a => a?.UnlockTimeUtc ?? DateTime.MinValue);

            if (includeGameNameTieBreak)
            {
                ordered = ordered.ThenBy(a => a?.Game?.Name, StringComparer.OrdinalIgnoreCase);
            }

            return ordered
                .ThenBy(a => a?.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static int CompareProgressFractionDescending(
            int? aNum,
            int? aDenom,
            int? bNum,
            int? bDenom)
        {
            var aHasProgress = HasProgress(aNum, aDenom);
            var bHasProgress = HasProgress(bNum, bDenom);

            if (aHasProgress && bHasProgress)
            {
                var aFraction = GetProgressFraction(aNum, aDenom).Value;
                var bFraction = GetProgressFraction(bNum, bDenom).Value;
                var fractionComparison = bFraction.CompareTo(aFraction);
                if (fractionComparison != 0)
                {
                    return fractionComparison;
                }
            }

            if (aHasProgress != bHasProgress)
            {
                return aHasProgress ? -1 : 1;
            }

            return 0;
        }

        private static bool HasProgress(int? numerator, int? denominator)
        {
            return numerator.HasValue && denominator.HasValue && denominator.Value > 0;
        }

        private static double? GetProgressFraction(int? numerator, int? denominator)
        {
            return HasProgress(numerator, denominator)
                ? (double)numerator.Value / denominator.Value
                : (double?)null;
        }

        private static int CompareText(string a, string b)
        {
            return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }
}

