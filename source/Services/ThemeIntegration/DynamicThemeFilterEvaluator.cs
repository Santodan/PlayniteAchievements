using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.ThemeIntegration;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.Database;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Services.ThemeIntegration
{
    internal static class DynamicThemeFilterEvaluator
    {
        public static IEnumerable<AchievementDetail> ApplyAchievementFilters(
            IEnumerable<AchievementDetail> source,
            string filterKey)
        {
            var items = source ?? Enumerable.Empty<AchievementDetail>();
            foreach (var groupKeys in AchievementFilterGroups(filterKey))
            {
                items = items.Where(item => groupKeys.Any(key => MatchesAchievementFilter(item, key)));
            }

            return items;
        }

        /// <summary><paramref name="query"/> narrowed by a filter expression, as <see cref="ApplyAchievementFilters(IEnumerable{AchievementDetail}, string)"/> narrows rows.</summary>
        public static AchievementQuery ApplyAchievementFilters(AchievementQuery query, string filterKey) =>
            AchievementFilterGroups(filterKey).Aggregate(query, (narrowed, group) => narrowed.WhereAnyKey(group));

        /// <summary>
        /// <paramref name="query"/> ordered by a dynamic list's sort key and direction, newest
        /// unlocks first by default.
        /// </summary>
        public static AchievementQuery ApplyAchievementSort(AchievementQuery query, string sortKey, string directionKey) =>
            query.OrderBy(
                AchievementQuery.SortOfColumn(AchievementSortHelper.GetDynamicAchievementSortMemberPath(sortKey)) ?? AchievementSort.UnlockTime,
                !string.Equals(directionKey, DynamicThemeViewKeys.Ascending, StringComparison.Ordinal));

        // Keys of one group match any of them; groups all match.
        private static IEnumerable<string[]> AchievementFilterGroups(string filterKey) =>
            DynamicThemeFilterExpression.Enumerate(filterKey)
                .Select(CanonicalizeCategoryTypeKey)
                .GroupBy(key => GetGroupKey(key, DynamicThemeOptionGroups.AchievementFilterGroupMap))
                .Where(group => !string.IsNullOrWhiteSpace(group.Key))
                .Select(group => group.ToArray());

        public static IEnumerable<GameAchievementSummary> ApplyGameSummaryFilters(
            IEnumerable<GameAchievementSummary> source,
            string filterKey)
        {
            var items = source ?? Enumerable.Empty<GameAchievementSummary>();
            foreach (var group in DynamicThemeFilterExpression.Enumerate(filterKey)
                .GroupBy(key => GetGroupKey(key, DynamicThemeOptionGroups.GameSummaryFilterGroupMap))
                .Where(group => !string.IsNullOrWhiteSpace(group.Key)))
            {
                var groupKeys = group.ToArray();
                items = items.Where(item => groupKeys.Any(key => MatchesGameSummaryFilter(item, key)));
            }

            return items;
        }

        private static string GetGroupKey(
            string filterKey,
            IReadOnlyDictionary<string, string> groupMap)
        {
            return !string.IsNullOrWhiteSpace(filterKey) &&
                   groupMap != null &&
                   groupMap.TryGetValue(filterKey, out var groupKey)
                ? groupKey
                : null;
        }

        private static bool MatchesAchievementFilter(AchievementDetail item, string filterKey)
        {
            switch (filterKey)
            {
                case DynamicThemeViewKeys.Unlocked:
                    return item?.Unlocked == true;
                case DynamicThemeViewKeys.Locked:
                    return item != null && !item.Unlocked;
                case DynamicThemeViewKeys.Visible:
                    return item != null && !item.Hidden;
                case DynamicThemeViewKeys.Hidden:
                    return item?.Hidden == true;
                case DynamicThemeViewKeys.InProgress:
                    return item != null && !item.Unlocked && HasProgress(item);
                case DynamicThemeViewKeys.NoProgress:
                    return item != null && !HasProgress(item);
                case DynamicThemeViewKeys.HasNotes:
                    return !string.IsNullOrWhiteSpace(item?.AchievementNote);
                case DynamicThemeViewKeys.NoNotes:
                    return string.IsNullOrWhiteSpace(item?.AchievementNote);
                case DynamicThemeViewKeys.Capstone:
                    return item?.IsCapstone == true;
                case DynamicThemeViewKeys.Common:
                    return item?.Rarity == RarityTier.Common;
                case DynamicThemeViewKeys.Uncommon:
                    return item?.Rarity == RarityTier.Uncommon;
                case DynamicThemeViewKeys.Rare:
                    return item?.Rarity == RarityTier.Rare;
                case DynamicThemeViewKeys.UltraRare:
                    return item?.Rarity == RarityTier.UltraRare;
                case DynamicThemeViewKeys.Platinum:
                case DynamicThemeViewKeys.Gold:
                case DynamicThemeViewKeys.Silver:
                case DynamicThemeViewKeys.Bronze:
                    return IsTrophyType(item, filterKey);
                default:
                    if (Services.Achievements.AchievementClassificationFilter.IsFilterKey(filterKey))
                    {
                        return item != null && Services.Achievements.AchievementClassificationFilter.Matches(
                            item.CategoryKind,
                            item.Tags,
                            filterKey);
                    }

                    return true;
            }
        }

        private static bool MatchesGameSummaryFilter(GameAchievementSummary item, string filterKey)
        {
            switch (filterKey)
            {
                case DynamicThemeViewKeys.Completed:
                    return item?.IsCompleted == true;
                case DynamicThemeViewKeys.Incomplete:
                    return item != null && !item.IsCompleted;
                case DynamicThemeViewKeys.Started:
                    return item != null && item.UnlockedCount > 0;
                case DynamicThemeViewKeys.NotStarted:
                    return item != null && item.UnlockedCount <= 0;
                case DynamicThemeViewKeys.Played:
                    return item?.LastPlayed.HasValue == true;
                case DynamicThemeViewKeys.Unplayed:
                    return item != null && !item.LastPlayed.HasValue;
                case DynamicThemeViewKeys.HasLastUnlock:
                    return item != null && item.LastUnlockDate != DateTime.MinValue;
                case DynamicThemeViewKeys.NoLastUnlock:
                    return item != null && item.LastUnlockDate == DateTime.MinValue;
                default:
                    return true;
            }
        }

        private static bool HasProgress(AchievementDetail item)
        {
            return item?.ProgressNum.HasValue == true &&
                   item.ProgressDenom.HasValue &&
                   item.ProgressDenom.Value > 0;
        }

        private static bool IsTrophyType(AchievementDetail item, string trophyKey)
        {
            if (item == null || string.IsNullOrWhiteSpace(trophyKey))
            {
                return false;
            }

            return string.Equals(item.TrophyType, trophyKey, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// A kind or tag key in its current spelling, so a theme still naming a renamed key
        /// (SideProgression) filters by the one it became. Any other key is returned unchanged.
        /// </summary>
        private static string CanonicalizeCategoryTypeKey(string filterKey)
        {
            return Services.Achievements.AchievementClassificationFilter.IsFilterKey(filterKey)
                ? Services.Achievements.AchievementClassificationFilter.Canonicalize(filterKey)
                : filterKey;
        }
    }
}
