using System;
using System.Collections.Generic;
using System.Linq;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.ViewModels;

namespace PlayniteAchievements.Services.Sidebar
{
    internal sealed class SelectedGameFilterOptions
    {
        public List<string> TypeOptions { get; set; }

        public List<string> CategoryOptions { get; set; }

        public bool TypeSelectionPruned { get; set; }

        public bool CategorySelectionPruned { get; set; }
    }

    internal static class SidebarAchievementFilters
    {
        public static SelectedGameFilterOptions BuildSelectedGameFilterOptions(
            IEnumerable<AchievementDisplayItem> source,
            HashSet<string> selectedTypeFilters,
            HashSet<string> selectedCategoryFilters)
        {
            var typeValues = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var categoryValues = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (source != null)
            {
                foreach (var item in source)
                {
                    if (item == null)
                    {
                        continue;
                    }

                    foreach (var parsedType in AchievementCategoryTypeHelper.ParseValues(
                                 AchievementCategoryTypeHelper.NormalizeOrDefault(item.CategoryType)))
                    {
                        if (!string.IsNullOrWhiteSpace(parsedType))
                        {
                            typeValues.Add(parsedType);
                        }
                    }

                    var normalizedCategory = AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(item.CategoryLabel);
                    if (!string.IsNullOrWhiteSpace(normalizedCategory))
                    {
                        categoryValues.Add(normalizedCategory);
                    }
                }
            }

            var typeOptions = AchievementCategoryTypeHelper.AllowedCategoryTypes
                .Where(typeValues.Contains)
                .ToList();

            var categoryOptions = categoryValues
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new SelectedGameFilterOptions
            {
                TypeOptions = typeOptions,
                CategoryOptions = categoryOptions,
                TypeSelectionPruned = PruneSelections(selectedTypeFilters, typeOptions),
                CategorySelectionPruned = PruneSelections(selectedCategoryFilters, categoryOptions)
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
                var selectedTypeSet = new HashSet<string>(
                    selectedGameTypeFilters
                        .Select(AchievementCategoryTypeHelper.NormalizeOrDefault)
                        .Where(value => !string.IsNullOrWhiteSpace(value)),
                    StringComparer.OrdinalIgnoreCase);

                filtered = filtered.Where(a =>
                    AchievementCategoryTypeHelper.ParseValues(
                            AchievementCategoryTypeHelper.NormalizeOrDefault(a.CategoryType))
                        .Any(selectedTypeSet.Contains));
            }

            if (selectedGameCategoryFilters != null && selectedGameCategoryFilters.Count > 0)
            {
                var selectedCategorySet = new HashSet<string>(
                    selectedGameCategoryFilters
                        .Select(AchievementCategoryTypeHelper.NormalizeCategoryOrDefault)
                        .Where(value => !string.IsNullOrWhiteSpace(value)),
                    StringComparer.OrdinalIgnoreCase);

                filtered = filtered.Where(a =>
                    selectedCategorySet.Contains(
                        AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(a.CategoryLabel)));
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

        private static bool PruneSelections(HashSet<string> selectedValues, IEnumerable<string> options)
        {
            if (selectedValues == null)
            {
                return false;
            }

            var optionSet = new HashSet<string>(
                (options ?? Enumerable.Empty<string>()).Where(value => !string.IsNullOrWhiteSpace(value)),
                StringComparer.OrdinalIgnoreCase);
            return selectedValues.RemoveWhere(value => !optionSet.Contains(value)) > 0;
        }
    }
}
