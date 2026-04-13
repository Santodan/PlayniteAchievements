using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.ViewModels;

namespace PlayniteAchievements.Services
{
    public enum AchievementGridSortScope
    {
        GameAchievements,
        RecentAchievements
    }

    /// <summary>
    /// Centralized sort logic for achievement display grids.
    /// </summary>
    public static class AchievementGridSortHelper
    {
        public static bool TrySortItems(
            List<AchievementDisplayItem> items,
            string sortMemberPath,
            ListSortDirection direction,
            AchievementGridSortScope scope,
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
            AchievementGridSortScope scope)
        {
            if (string.IsNullOrWhiteSpace(sortMemberPath))
            {
                return null;
            }

            return sortMemberPath switch
            {
                "DisplayName" => ApplyDirection(CompareByDisplayName, direction),
                "SortingName" when scope == AchievementGridSortScope.RecentAchievements
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
            AchievementGridSortScope scope)
        {
            var list = items?.ToList() ?? new List<AchievementDisplayItem>();
            if (list.Count == 0)
            {
                return list;
            }

            list.Sort(scope == AchievementGridSortScope.RecentAchievements
                ? GetComparison(nameof(AchievementDisplayItem.UnlockTime), ListSortDirection.Descending, scope)
                : CompareByDefaultGameOrder);
            return list;
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

            return CompareByUnlockTime(a, b, ListSortDirection.Descending, AchievementGridSortScope.GameAchievements);
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
            AchievementGridSortScope scope)
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
            AchievementGridSortScope scope)
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
            AchievementGridSortScope scope)
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

            if (scope == AchievementGridSortScope.RecentAchievements)
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

            var progressComparison = CompareProgressFractionDescending(
                a?.ProgressNum,
                a?.ProgressDenom,
                b?.ProgressNum,
                b?.ProgressDenom);
            if (progressComparison != 0)
            {
                return progressComparison;
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

        private static List<AchievementDetail> CreateDetailUnlockSortedList(
            IEnumerable<AchievementDetail> items,
            ListSortDirection direction,
            bool includeGameNameTieBreak)
        {
            IOrderedEnumerable<AchievementDetail> ordered = direction == ListSortDirection.Ascending
                ? items.OrderBy(a => a?.UnlockTimeUtc ?? DateTime.MinValue)
                : items.OrderByDescending(a => a?.UnlockTimeUtc ?? DateTime.MinValue);

            ordered = ordered
                .ThenBy(a => a?.RaritySortValue ?? double.MaxValue)
                .ThenByDescending(a => GetTrophyRank(a?.TrophyType))
                .ThenByDescending(a => HasProgress(a?.ProgressNum, a?.ProgressDenom))
                .ThenByDescending(a => GetProgressFraction(a?.ProgressNum, a?.ProgressDenom) ?? 0)
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
