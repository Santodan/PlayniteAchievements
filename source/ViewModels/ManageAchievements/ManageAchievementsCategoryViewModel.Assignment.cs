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
            var tagOverrides = GetCurrentTagOverrideMap();
            if (categoryOverrides.Count == 0 && tagOverrides.Count == 0)
            {
                return false;
            }

            var emptyCategories = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var emptyCategoryTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            PersistCategoryOverrideMaps(emptyCategories, emptyCategoryTypes);
            ApplyCategoryOverrideMapsToRows(emptyCategories, emptyCategoryTypes);
            // Membership just moved, so the Manage sub-tab's rows are showing the old grouping.
            RefreshCategoryRows();
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

            var normalizedCategory = AchievementCategoryHelper.NormalizeCategory(categoryText);
            var normalizedTags = AchievementTagHelper.Normalize(GetSelectedTagValue());
            var hasCategoryInput = !string.IsNullOrWhiteSpace(normalizedCategory);
            var hasTagInput = !string.IsNullOrWhiteSpace(normalizedTags);
            var selectedTags = hasTagInput
                ? AchievementTagHelper.Parse(normalizedTags)
                : new List<string>();
            if (!hasCategoryInput && !hasTagInput)
            {
                return false;
            }

            var categoryOverrideMap = GetCurrentCategoryOverrideMap();
            var tagOverrideMap = GetCurrentTagOverrideMap();
            var categoryChanged = false;
            var tagsChanged = false;

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

                if (hasTagInput &&
                    selectedTags.Count > 0)
                {
                    var currentEffectiveTags = AchievementTagHelper.Normalize(item.Tags);
                    var mergedTags = AchievementTagHelper.Normalize(
                        AchievementTagHelper.Combine(
                            AchievementTagHelper.Parse(currentEffectiveTags)
                                .Concat(selectedTags)));

                    var overrideTags = AchievementTagHelper.Normalize(
                        AchievementTagHelper.StripDerived(mergedTags));
                    if (!string.Equals(mergedTags, currentEffectiveTags, StringComparison.Ordinal) &&
                        (!tagOverrideMap.TryGetValue(apiName, out var existingTags) ||
                         !string.Equals(existingTags, overrideTags, StringComparison.Ordinal)))
                    {
                        tagOverrideMap[apiName] = overrideTags;
                        tagsChanged = true;
                    }
                }
            }

            if (categoryChanged || tagsChanged)
            {
                PersistCategoryOverrideMaps(categoryOverrideMap, tagOverrideMap);
                ApplyCategoryOverrideMapsToRows(categoryOverrideMap, tagOverrideMap);
                if (categoryChanged)
                {
                    // Only a label move regroups the Manage sub-tab; a type edit renders nowhere there.
                    RefreshCategoryRows();
                }
            }

            return true;
        }

        public bool SetTagForSelection(
            IReadOnlyList<ManageAchievementsCategoryItem> selectedRows,
            string tag,
            bool isSelected)
        {
            if (selectedRows == null || selectedRows.Count == 0)
            {
                return false;
            }

            var normalizedType = AchievementTagHelper.Normalize(tag);
            if (string.IsNullOrWhiteSpace(normalizedType))
            {
                return false;
            }

            var tagOverrideMap = GetCurrentTagOverrideMap();
            var tagsChanged = false;

            foreach (var item in selectedRows.Where(row => row != null))
            {
                var apiName = (item.ApiName ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(apiName))
                {
                    continue;
                }

                var currentEffectiveTags = AchievementTagHelper.Normalize(item.Tags);
                var updatedTags = AchievementTagHelper.WithTag(
                    currentEffectiveTags, normalizedType, isSelected);

                if (string.Equals(updatedTags, currentEffectiveTags, StringComparison.Ordinal))
                {
                    continue;
                }

                var overrideTags = AchievementTagHelper.OverrideOrNull(
                    updatedTags, item.ProviderTags);
                if (overrideTags == null)
                {
                    // Result matches the provider value: drop the override so the row is no
                    // longer flagged as customized.
                    if (tagOverrideMap.Remove(apiName))
                    {
                        tagsChanged = true;
                    }
                }
                else if (!tagOverrideMap.TryGetValue(apiName, out var existingTags) ||
                         !string.Equals(existingTags, overrideTags, StringComparison.Ordinal))
                {
                    tagOverrideMap[apiName] = overrideTags;
                    tagsChanged = true;
                }
            }

            if (!tagsChanged)
            {
                return false;
            }

            var categoryOverrideMap = GetCurrentCategoryOverrideMap();
            PersistCategoryOverrideMaps(categoryOverrideMap, tagOverrideMap);
            ApplyCategoryOverrideMapsToRows(categoryOverrideMap, tagOverrideMap);
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

            var normalizedCategory = AchievementCategoryHelper.NormalizeCategory(categoryLabel);
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

            var tagOverrideMap = GetCurrentTagOverrideMap();
            PersistCategoryOverrideMaps(categoryOverrideMap, tagOverrideMap);
            ApplyCategoryOverrideMapsToRows(categoryOverrideMap, tagOverrideMap);
            RefreshCategoryRows();
            return true;
        }

        public bool ClearSelectionOverrides(IReadOnlyList<ManageAchievementsCategoryItem> selectedRows)
        {
            if (selectedRows == null || selectedRows.Count == 0)
            {
                return false;
            }

            var categoryOverrideMap = GetCurrentCategoryOverrideMap();
            var tagOverrideMap = GetCurrentTagOverrideMap();
            var categoryChanged = false;
            var tagsChanged = false;

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

                if (tagOverrideMap.Remove(apiName))
                {
                    tagsChanged = true;
                }
            }

            if (!categoryChanged && !tagsChanged)
            {
                return false;
            }

            PersistCategoryOverrideMaps(categoryOverrideMap, tagOverrideMap);
            ApplyCategoryOverrideMapsToRows(categoryOverrideMap, tagOverrideMap);
            if (categoryChanged)
            {
                RefreshCategoryRows();
            }

            return true;
        }

        /// <summary>
        /// Re-points every achievement whose effective category equals <paramref name="normalizedSource"/>
        /// to <paramref name="normalizedTarget"/> in <paramref name="categoryOverrideMap"/>. Tags and kinds
        /// are left alone: a moved achievement takes the kind of the category it lands in. An override is
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
            }

            return affectedCount > 0 && changed;
        }

        public void ResetBulkEditorInputs()
        {
            SetCategoryTypeSelections(TagSelectionOptions, false);
        }

        private void PersistCategoryOverrideMaps(
            IReadOnlyDictionary<string, string> categoryOverrideMap,
            IReadOnlyDictionary<string, string> tagOverrideMap)
        {
            // Membership is scoped out of the library-wide passes inside the service, so the
            // catch-up has to be booked here.
            MarkLibraryRefreshDeferred(false);
            var rescoped = _achievementOverridesService.SetAchievementCategoryOverrides(
                _gameId,
                categoryOverrideMap,
                tagOverrideMap);

            // A moved auto capstone stands for somewhere new, and no refresh follows a hand edit
            // to bring its unlock into step. Quiet, because the user moved it rather than the game
            // reporting an unlock.
            if (rescoped)
            {
                PlayniteAchievementsPlugin.Instance?.AutoCapstoneMaintainer?.Maintain(
                    _gameId,
                    announceUnlocks: false);
            }

            // The Editor rebuilds and writes the whole override map from its own rows, so a
            // membership change it is never told about is reverted by its next category edit.
            // This event is what marks it stale when the user leaves this tab.
            RaiseCategoryMetadataPersisted();
        }

        private void ApplyCategoryOverrideMapsToRows(
            IReadOnlyDictionary<string, string> categoryOverrideMap,
            IReadOnlyDictionary<string, string> tagOverrideMap)
        {
            var storedKinds = GameCustomDataLookup.GetAchievementCategoryKinds(_gameId, _settings?.Persisted);
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
                var tagOverride = tagOverrideMap != null &&
                                  tagOverrideMap.TryGetValue(apiName, out var storedTagOverride)
                    ? storedTagOverride
                    : null;

                item.Category = AchievementCategoryHelper.NormalizeCategoryOrDefault(category);
                item.Tags = AchievementTagHelper.ApplyOverride(item.ProviderTags, tagOverride);
                // A move changes which category's kind applies.
                item.CategoryKind = CategoryKindHelper.ResolveEffective(
                    ResolveStoredCategoryKind(storedKinds, item.Category),
                    item.LegacyCategoryKind,
                    item.ProviderCategoryKind);
            }

            _searchIndex.Rebuild(_allRows);
            HasCustomOverrides =
                (categoryOverrideMap?.Count ?? 0) > 0 ||
                (tagOverrideMap?.Count ?? 0) > 0;
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

        private Dictionary<string, string> GetCurrentTagOverrideMap()
        {
            // "" values are kept: they record that the user removed every tag.
            return GameCustomDataLookup.GetAchievementTagOverrides(_gameId, _settings?.Persisted);
        }

        private Dictionary<string, string> GetCurrentCategoryKinds()
        {
            return GameCustomDataFile.CloneCategoryKindMap(
                       GameCustomDataLookup.GetAchievementCategoryKinds(_gameId, _settings?.Persisted)) ??
                   new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Moves the kinds stored for <paramref name="source"/> and its descendants to the same place
        /// under <paramref name="target"/> ("DLC::Winter" follows "DLC" to "Extras::Winter"), or
        /// drops them when <paramref name="target"/> is null. True when the map changed.
        /// </summary>
        private static bool MoveCategoryKinds(Dictionary<string, string> kinds, string source, string target)
        {
            var normalizedSource = CategoryPathHelper.NormalizePath(source);
            if (kinds == null || kinds.Count == 0 || normalizedSource == null)
            {
                return false;
            }

            var moved = kinds
                .Where(pair => CategoryPathHelper.IsSelfOrDescendantOf(pair.Key, normalizedSource))
                .ToList();
            foreach (var pair in moved)
            {
                kinds.Remove(pair.Key);
            }

            if (target != null)
            {
                var normalizedTarget = CategoryPathHelper.NormalizePath(target);
                foreach (var pair in moved)
                {
                    kinds[CategoryPathHelper.RewritePrefix(pair.Key, normalizedSource, normalizedTarget)] = pair.Value;
                }
            }

            return moved.Count > 0;
        }

        private static string ResolveStoredCategoryKind(IReadOnlyDictionary<string, string> storedKinds, string category)
        {
            var key = CategoryPathHelper.NormalizePath(category);
            return key != null && storedKinds != null && storedKinds.TryGetValue(key, out var kind) ? kind : null;
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
