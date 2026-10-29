using System;
using System.Collections.Generic;
using System.Linq;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.Services.Overview
{
    internal sealed class SelectedGameFilterOptions
    {
        public List<string> TypeOptions { get; set; }

        public List<string> CategoryOptions { get; set; }

        public bool TypeSelectionPruned { get; set; }

        public bool CategorySelectionPruned { get; set; }
    }

    internal static class OverviewAchievementFilters
    {
        public static SelectedGameFilterOptions BuildSelectedGameFilterOptions(
            IEnumerable<AchievementDisplayItem> source,
            HashSet<string> selectedTypeFilters,
            HashSet<string> selectedCategoryFilters)
        {
            var typeOptions = AchievementClassificationFilter.GetPresentKeys(
                source,
                item => item.Tags,
                source?.FirstOrDefault(item => item != null)?.PlayniteGameId);

            var categoryOptions = AchievementCategoryFilterOrderHelper.BuildOrderedCategoryLabels(
                source,
                item => item?.CategoryLabel,
                (source ?? Enumerable.Empty<AchievementDisplayItem>())
                    .Where(item => item != null && item.CategoryOrderIndex < int.MaxValue)
                    .OrderBy(item => item.CategoryOrderIndex)
                    .Select(item => item.CategoryLabel));

            return new SelectedGameFilterOptions
            {
                TypeOptions = typeOptions,
                CategoryOptions = categoryOptions,
                TypeSelectionPruned = PruneSelections(selectedTypeFilters, typeOptions, StringComparer.OrdinalIgnoreCase),
                CategorySelectionPruned = PruneSelections(
                    selectedCategoryFilters,
                    categoryOptions,
                    CategoryPathHelper.LabelComparer)
            };
        }

        public static List<AchievementDisplayItem> FilterSelectedGameAchievements(
            IEnumerable<AchievementDisplayItem> source,
            bool showSelectedGameHidden,
            bool showSelectedGameUnlocked,
            bool showSelectedGameLocked,
            HashSet<string> selectedGameTypeFilters,
            HashSet<string> selectedGameCategoryFilters,
            string rightSearchText)
        {
            var filtered = (source ?? Enumerable.Empty<AchievementDisplayItem>())
                .Where(item => item != null)
                .AsEnumerable();

            if (!showSelectedGameHidden)
            {
                filtered = filtered.Where(a => !(a.Hidden && !a.Unlocked));
            }

            filtered = filtered.Where(a => a.Unlocked ? showSelectedGameUnlocked : showSelectedGameLocked);

            if (selectedGameTypeFilters != null && selectedGameTypeFilters.Count > 0)
            {
                var selectedTypes = selectedGameTypeFilters
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .ToList();

                filtered = filtered.Where(a => selectedTypes.Any(key =>
                    AchievementClassificationFilter.Matches(a.CategoryKind, a.Tags, key)));
            }

            if (selectedGameCategoryFilters != null && selectedGameCategoryFilters.Count > 0)
            {
                var selectedCategorySet = new HashSet<string>(
                    selectedGameCategoryFilters
                        .Select(AchievementCategoryHelper.NormalizeCategoryOrDefault)
                        .Where(value => !string.IsNullOrWhiteSpace(value)),
                    CategoryPathHelper.LabelComparer);

                filtered = filtered.Where(a =>
                    selectedCategorySet.Contains(
                        AchievementCategoryHelper.NormalizeCategoryOrDefault(a.CategoryLabel)));
            }

            if (!string.IsNullOrEmpty(rightSearchText))
            {
                filtered = filtered.Where(a =>
                    (a.DisplayName?.IndexOf(rightSearchText, StringComparison.OrdinalIgnoreCase) >= 0) ||
                    (a.Description?.IndexOf(rightSearchText, StringComparison.OrdinalIgnoreCase) >= 0));
            }

            return filtered.ToList();
        }

        public static List<AchievementDisplayItem> FilterRecentAchievements(
            IEnumerable<AchievementDisplayItem> source,
            string rightSearchText)
        {
            var filtered = (source ?? Enumerable.Empty<AchievementDisplayItem>())
                .Where(item => item != null)
                .AsEnumerable();

            if (!string.IsNullOrEmpty(rightSearchText))
            {
                filtered = filtered.Where(r =>
                    (r.GameName?.IndexOf(rightSearchText, StringComparison.OrdinalIgnoreCase) >= 0) ||
                    (r.Name?.IndexOf(rightSearchText, StringComparison.OrdinalIgnoreCase) >= 0));
            }

            return filtered.ToList();
        }

        private static bool PruneSelections(
            HashSet<string> selectedValues,
            IEnumerable<string> options,
            StringComparer comparer)
        {
            if (selectedValues == null)
            {
                return false;
            }

            var optionSet = new HashSet<string>(
                (options ?? Enumerable.Empty<string>()).Where(value => !string.IsNullOrWhiteSpace(value)),
                comparer);
            return selectedValues.RemoveWhere(value => !optionSet.Contains(value)) > 0;
        }
    }
}
