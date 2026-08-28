using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Playnite.SDK;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.GameCustomData;
using PlayniteAchievements.Services.Images;
using PlayniteAchievements.Services.Search;
using PlayniteAchievements.ViewModels.Items;
using RelayCommand = PlayniteAchievements.Common.RelayCommand;

using ObservableObject = PlayniteAchievements.Common.ObservableObject;

namespace PlayniteAchievements.ViewModels.ManageAchievements
{
    // Category and category-type assignment: bulk edits, per-row label/type writes, and the override maps they persist.
    public sealed partial class ManageAchievementsCategoryViewModel : ObservableObject
    {
        public bool ResetCategoryOverrides()
        {
            var categoryOverrides = GetCurrentCategoryOverrideMap();
            var categoryTypeOverrides = GetCurrentCategoryTypeOverrideMap();
            if (categoryOverrides.Count == 0 && categoryTypeOverrides.Count == 0)
            {
                return false;
            }

            var emptyCategories = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var emptyCategoryTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            PersistCategoryOverrideMaps(emptyCategories, emptyCategoryTypes);
            ApplyCategoryOverrideMapsToRows(emptyCategories, emptyCategoryTypes);
            return true;
        }

        public bool ApplyBulkToSelection(
            IReadOnlyList<ManageAchievementsCategoryItem> selectedRows,
            string categoryText)
        {
            if (selectedRows == null || selectedRows.Count == 0)
            {
                return false;
            }

            var normalizedCategory = AchievementCategoryTypeHelper.NormalizeCategory(categoryText);
            var normalizedCategoryType = AchievementCategoryTypeHelper.Normalize(GetSelectedCategoryTypeValue());
            var hasCategoryInput = !string.IsNullOrWhiteSpace(normalizedCategory);
            var hasCategoryTypeInput = !string.IsNullOrWhiteSpace(normalizedCategoryType);
            var selectedCategoryTypes = hasCategoryTypeInput
                ? AchievementCategoryTypeHelper.ParseValues(normalizedCategoryType)
                : new List<string>();
            if (!hasCategoryInput && !hasCategoryTypeInput)
            {
                return false;
            }

            var categoryOverrideMap = GetCurrentCategoryOverrideMap();
            var categoryTypeOverrideMap = GetCurrentCategoryTypeOverrideMap();
            var categoryChanged = false;
            var categoryTypeChanged = false;

            foreach (var item in selectedRows.Where(row => row != null))
            {
                var apiName = (item.ApiName ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(apiName))
                {
                    continue;
                }

                if (hasCategoryInput &&
                    (!categoryOverrideMap.TryGetValue(apiName, out var existingCategory) ||
                     !string.Equals(existingCategory, normalizedCategory, StringComparison.Ordinal)))
                {
                    categoryOverrideMap[apiName] = normalizedCategory;
                    categoryChanged = true;
                }

                if (hasCategoryTypeInput &&
                    selectedCategoryTypes.Count > 0)
                {
                    var currentEffectiveCategoryType = AchievementCategoryTypeHelper.NormalizeOrDefault(item.CategoryType);
                    var mergedCategoryType = AchievementCategoryTypeHelper.NormalizeOrDefault(
                        AchievementCategoryTypeHelper.Combine(
                            AchievementCategoryTypeHelper.ParseValues(currentEffectiveCategoryType)
                                .Concat(selectedCategoryTypes)));

                    if (!string.Equals(mergedCategoryType, currentEffectiveCategoryType, StringComparison.Ordinal) &&
                        (!categoryTypeOverrideMap.TryGetValue(apiName, out var existingCategoryType) ||
                         !string.Equals(existingCategoryType, mergedCategoryType, StringComparison.Ordinal)))
                    {
                        categoryTypeOverrideMap[apiName] = mergedCategoryType;
                        categoryTypeChanged = true;
                    }
                }
            }

            if (categoryChanged || categoryTypeChanged)
            {
                PersistCategoryOverrideMaps(categoryOverrideMap, categoryTypeOverrideMap);
                ApplyCategoryOverrideMapsToRows(categoryOverrideMap, categoryTypeOverrideMap);
            }

            return true;
        }

        public bool SetCategoryTypeForSelection(
            IReadOnlyList<ManageAchievementsCategoryItem> selectedRows,
            string categoryType,
            bool isSelected)
        {
            if (selectedRows == null || selectedRows.Count == 0)
            {
                return false;
            }

            var normalizedType = AchievementCategoryTypeHelper.Normalize(categoryType);
            if (string.IsNullOrWhiteSpace(normalizedType))
            {
                return false;
            }

            var categoryTypeOverrideMap = GetCurrentCategoryTypeOverrideMap();
            var categoryTypeChanged = false;

            foreach (var item in selectedRows.Where(row => row != null))
            {
                var apiName = (item.ApiName ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(apiName))
                {
                    continue;
                }

                var currentEffectiveCategoryType = AchievementCategoryTypeHelper.NormalizeOrDefault(item.CategoryType);
                var updatedCategoryType = AchievementCategoryTypeHelper.WithCategoryType(
                    currentEffectiveCategoryType, normalizedType, isSelected);

                if (string.Equals(updatedCategoryType, currentEffectiveCategoryType, StringComparison.Ordinal))
                {
                    continue;
                }

                var providerCategoryType = AchievementCategoryTypeHelper.NormalizeOrDefault(item.ProviderCategoryType);
                if (string.Equals(updatedCategoryType, providerCategoryType, StringComparison.Ordinal))
                {
                    // Result matches the provider value: drop the override so the row is no
                    // longer flagged as customized.
                    if (categoryTypeOverrideMap.Remove(apiName))
                    {
                        categoryTypeChanged = true;
                    }
                }
                else if (!categoryTypeOverrideMap.TryGetValue(apiName, out var existingCategoryType) ||
                         !string.Equals(existingCategoryType, updatedCategoryType, StringComparison.Ordinal))
                {
                    categoryTypeOverrideMap[apiName] = updatedCategoryType;
                    categoryTypeChanged = true;
                }
            }

            if (!categoryTypeChanged)
            {
                return false;
            }

            var categoryOverrideMap = GetCurrentCategoryOverrideMap();
            PersistCategoryOverrideMaps(categoryOverrideMap, categoryTypeOverrideMap);
            ApplyCategoryOverrideMapsToRows(categoryOverrideMap, categoryTypeOverrideMap);
            return true;
        }

        public bool SetCategoryLabelForSelection(
            IReadOnlyList<ManageAchievementsCategoryItem> selectedRows,
            string categoryLabel)
        {
            if (selectedRows == null || selectedRows.Count == 0)
            {
                return false;
            }

            var normalizedCategory = AchievementCategoryTypeHelper.NormalizeCategory(categoryLabel);
            if (string.IsNullOrWhiteSpace(normalizedCategory))
            {
                return false;
            }

            var categoryOverrideMap = GetCurrentCategoryOverrideMap();
            var categoryChanged = false;

            foreach (var item in selectedRows.Where(row => row != null))
            {
                var apiName = (item.ApiName ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(apiName))
                {
                    continue;
                }

                if (!categoryOverrideMap.TryGetValue(apiName, out var existingCategory) ||
                    !string.Equals(existingCategory, normalizedCategory, StringComparison.Ordinal))
                {
                    categoryOverrideMap[apiName] = normalizedCategory;
                    categoryChanged = true;
                }
            }

            if (!categoryChanged)
            {
                return false;
            }

            var categoryTypeOverrideMap = GetCurrentCategoryTypeOverrideMap();
            PersistCategoryOverrideMaps(categoryOverrideMap, categoryTypeOverrideMap);
            ApplyCategoryOverrideMapsToRows(categoryOverrideMap, categoryTypeOverrideMap);
            return true;
        }

        public bool ClearSelectionOverrides(IReadOnlyList<ManageAchievementsCategoryItem> selectedRows)
        {
            if (selectedRows == null || selectedRows.Count == 0)
            {
                return false;
            }

            var categoryOverrideMap = GetCurrentCategoryOverrideMap();
            var categoryTypeOverrideMap = GetCurrentCategoryTypeOverrideMap();
            var categoryChanged = false;
            var categoryTypeChanged = false;

            foreach (var item in selectedRows.Where(row => row != null))
            {
                var apiName = (item.ApiName ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(apiName))
                {
                    continue;
                }

                if (categoryOverrideMap.Remove(apiName))
                {
                    categoryChanged = true;
                }

                if (categoryTypeOverrideMap.Remove(apiName))
                {
                    categoryTypeChanged = true;
                }
            }

            if (!categoryChanged && !categoryTypeChanged)
            {
                return false;
            }

            PersistCategoryOverrideMaps(categoryOverrideMap, categoryTypeOverrideMap);
            ApplyCategoryOverrideMapsToRows(categoryOverrideMap, categoryTypeOverrideMap);
            return true;
        }

        /// <summary>
        /// Re-points every achievement whose effective category equals <paramref name="normalizedSource"/>
        /// to <paramref name="normalizedTarget"/> in <paramref name="categoryOverrideMap"/>. When
        /// <paramref name="categoryTypeOverrideMap"/> is non-null (merge), also replaces each moved
        /// achievement's group-based type tags with <paramref name="targetGroupTypes"/>. An override is
        /// removed rather than set when the resulting value matches the achievement's provider default.
        /// Returns true when at least one achievement was affected and something changed.
        /// </summary>
        /// <param name="rewriteDescendantPaths">
        /// True for a rename or reparent, where a descendant keeps its position beneath the moved
        /// node ("DLC::Winter" follows "DLC" to "Extras::Winter"). False for a merge, which folds
        /// the whole subtree flat into the target.
        /// </param>
        private bool ReassignEffectiveCategoryRows(
            string normalizedSource,
            string normalizedTarget,
            Dictionary<string, string> categoryOverrideMap,
            Dictionary<string, string> categoryTypeOverrideMap,
            IReadOnlyList<string> targetGroupTypes,
            bool rewriteDescendantPaths = false)
        {
            var affectedCount = 0;
            var changed = false;

            foreach (var item in _allRows.Where(row => row != null))
            {
                var apiName = (item.ApiName ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(apiName))
                {
                    continue;
                }

                var effectiveCategory = CategoryPathHelper.NormalizePath(item.Category);
                if (!CategoryPathHelper.IsSelfOrDescendantOf(effectiveCategory, normalizedSource))
                {
                    continue;
                }

                affectedCount++;

                var writtenCategory = rewriteDescendantPaths
                    ? CategoryPathHelper.RewritePrefix(effectiveCategory, normalizedSource, normalizedTarget)
                    : normalizedTarget;

                // Compare the value actually being written against the provider label, so the
                // economy of dropping a redundant override still holds per achievement.
                var providerCategory = CategoryPathHelper.NormalizePath(item.ProviderCategory);
                if (CategoryPathHelper.IsSame(providerCategory, writtenCategory))
                {
                    if (categoryOverrideMap.Remove(apiName))
                    {
                        changed = true;
                    }
                }
                else if (!categoryOverrideMap.TryGetValue(apiName, out var existingCategory) ||
                         !string.Equals(existingCategory, writtenCategory, StringComparison.Ordinal))
                {
                    categoryOverrideMap[apiName] = writtenCategory;
                    changed = true;
                }

                if (categoryTypeOverrideMap == null)
                {
                    continue;
                }

                var newType = AchievementCategoryTypeHelper.NormalizeOrDefault(
                    AchievementCategoryTypeHelper.ReplaceGroupTypes(item.CategoryType, targetGroupTypes));
                var providerType = AchievementCategoryTypeHelper.NormalizeOrDefault(item.ProviderCategoryType);
                if (string.Equals(newType, providerType, StringComparison.OrdinalIgnoreCase))
                {
                    if (categoryTypeOverrideMap.Remove(apiName))
                    {
                        changed = true;
                    }
                }
                else if (!categoryTypeOverrideMap.TryGetValue(apiName, out var existingType) ||
                         !string.Equals(existingType, newType, StringComparison.Ordinal))
                {
                    categoryTypeOverrideMap[apiName] = newType;
                    changed = true;
                }
            }

            return affectedCount > 0 && changed;
        }

        /// <summary>
        /// The group-based type signature (Base/DLC/Update/Subset) shared by the achievements currently
        /// in <paramref name="targetLabel"/>. Empty when the target category carries no group-based type.
        /// </summary>
        private IReadOnlyList<string> ResolveGroupTypesForCategory(string targetLabel)
        {
            var normalizedTarget = AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(targetLabel);

            return AchievementCategoryTypeHelper.ResolveDominantGroupType(
                _allRows
                    .Where(row => row != null)
                    .Where(row => string.Equals(
                        AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(row.Category),
                        normalizedTarget,
                        StringComparison.OrdinalIgnoreCase))
                    .Select(row => row.CategoryType));
        }

        public void ResetBulkEditorInputs()
        {
            SetCategoryTypeSelections(TypeSelectionOptions, false);
        }

        private void PersistCategoryOverrideMaps(
            IReadOnlyDictionary<string, string> categoryOverrideMap,
            IReadOnlyDictionary<string, string> categoryTypeOverrideMap)
        {
            _achievementOverridesService.SetAchievementCategoryOverrides(
                _gameId,
                categoryOverrideMap,
                categoryTypeOverrideMap);
        }

        private void ApplyCategoryOverrideMapsToRows(
            IReadOnlyDictionary<string, string> categoryOverrideMap,
            IReadOnlyDictionary<string, string> categoryTypeOverrideMap)
        {
            foreach (var item in _allRows.Where(row => row != null))
            {
                var apiName = (item.ApiName ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(apiName))
                {
                    continue;
                }

                var category = categoryOverrideMap != null &&
                               categoryOverrideMap.TryGetValue(apiName, out var categoryOverride) &&
                               !string.IsNullOrWhiteSpace(categoryOverride)
                    ? categoryOverride
                    : item.ProviderCategory;
                var categoryType = categoryTypeOverrideMap != null &&
                                   categoryTypeOverrideMap.TryGetValue(apiName, out var categoryTypeOverride) &&
                                   !string.IsNullOrWhiteSpace(categoryTypeOverride)
                    ? categoryTypeOverride
                    : item.ProviderCategoryType;

                item.Category = AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(category);
                item.CategoryType = AchievementCategoryTypeHelper.NormalizeOrDefault(categoryType);
            }

            _searchIndex.Rebuild(_allRows);
            HasCustomOverrides =
                (categoryOverrideMap?.Count ?? 0) > 0 ||
                (categoryTypeOverrideMap?.Count ?? 0) > 0;
            RefreshCategoryLabelOptions();

            // Rows update their bound Type/Category cells in place via property change, so a
            // full collection rebuild is unnecessary for an edit that does not change which
            // rows are visible. Only re-filter when a filter keyed on category, type, or search
            // text is active; otherwise skip ApplyFilter to avoid the ReplaceAll Reset that
            // regenerates every DataGrid row and causes a visible flicker.
            if (IsVisibilityFilteredByCategoryEdit())
            {
                ApplyFilter();
            }
        }

        private bool IsVisibilityFilteredByCategoryEdit()
        {
            return SearchQuery.From(SearchText).HasValue
                || GetSelectedCategoryTypeFilterValues().Count > 0
                || _selectedCategoryLabelFilters.Count > 0;
        }

        private Dictionary<string, string> GetCurrentCategoryOverrideMap()
        {
            var map = GameCustomDataLookup.GetAchievementCategoryOverrides(_gameId, _settings?.Persisted);
            var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in map)
            {
                var apiName = (pair.Key ?? string.Empty).Trim();
                var category = NormalizeCategory(pair.Value);
                if (string.IsNullOrWhiteSpace(apiName) || string.IsNullOrWhiteSpace(category))
                {
                    continue;
                }

                normalized[apiName] = category;
            }

            return normalized;
        }

        private Dictionary<string, string> GetCurrentCategoryTypeOverrideMap()
        {
            var map = GameCustomDataLookup.GetAchievementCategoryTypeOverrides(_gameId, _settings?.Persisted);
            var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in map)
            {
                var apiName = (pair.Key ?? string.Empty).Trim();
                var categoryType = AchievementCategoryTypeHelper.Normalize(pair.Value);
                if (string.IsNullOrWhiteSpace(apiName) || string.IsNullOrWhiteSpace(categoryType))
                {
                    continue;
                }

                normalized[apiName] = categoryType;
            }

            return normalized;
        }

        private static string ResolveEffectiveCategoryLabel(
            AchievementDetail achievement,
            IReadOnlyDictionary<string, string> categoryOverrides)
        {
            var apiName = (achievement?.ApiName ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(apiName) &&
                categoryOverrides != null &&
                categoryOverrides.TryGetValue(apiName, out var categoryOverride) &&
                !string.IsNullOrWhiteSpace(categoryOverride))
            {
                return categoryOverride;
            }

            return achievement?.Category;
        }

    }
}
