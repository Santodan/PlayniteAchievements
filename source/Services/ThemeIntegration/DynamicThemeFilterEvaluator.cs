using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.ThemeIntegration;
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
            foreach (var group in DynamicThemeFilterExpression.Enumerate(filterKey)
                .GroupBy(key => GetGroupKey(key, DynamicThemeOptionGroups.AchievementFilterGroupMap))
                .Where(group => !string.IsNullOrWhiteSpace(group.Key)))
            {
                var groupKeys = group.ToArray();
                items = items.Where(item => groupKeys.Any(key => MatchesAchievementFilter(item, key)));
            }

            return items;
        }

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
                    if (CategoryTypeFilterKeys.Contains(filterKey ?? string.Empty))
                    {
                        return IsCategoryType(item, filterKey);
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

        private static readonly HashSet<string> CategoryTypeFilterKeys = new HashSet<string>(
            DynamicThemeOptionGroups.AchievementCategoryTypeFilterKeys
                .Where(key => !string.Equals(key, DynamicThemeViewKeys.All, StringComparison.OrdinalIgnoreCase)),
            StringComparer.OrdinalIgnoreCase);

        private static bool IsCategoryType(AchievementDetail item, string typeKey)
        {
            if (item == null || string.IsNullOrWhiteSpace(typeKey))
            {
                return false;
            }

            // CategoryType is multi-valued ("Base|DLC"); ParseValues canonicalizes aliases.
            return Services.Achievements.AchievementCategoryTypeHelper.ParseValues(item.CategoryType)
                .Contains(typeKey, StringComparer.OrdinalIgnoreCase);
        }
    }
}
