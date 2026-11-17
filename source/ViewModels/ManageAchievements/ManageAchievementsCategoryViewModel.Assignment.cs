using System;
using System.Collections.Generic;
using System.Linq;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.GameCustomData;

namespace PlayniteAchievements.ViewModels.ManageAchievements
{
    // The override maps the rows are stamped from after a category edit lands.
    public sealed partial class ManageAchievementsCategoryViewModel
    {
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
                    CategoryEditor.ResolveStoredCategoryKind(storedKinds, item.Category),
                    item.ProviderCategoryKind);
            }
        }

        private Dictionary<string, string> GetCurrentCategoryOverrideMap()
        {
            return _editor.GetCategoryOverrideMap(_gameId);
        }

        private Dictionary<string, string> GetCurrentTagOverrideMap()
        {
            // "" values are kept: they record that the user removed every tag.
            return _editor.GetTagOverrideMap(_gameId);
        }

        /// <summary>
        /// The rows as the editor plans against them: every achievement with where it is filed, and
        /// the rendered labels in render order. Taken once per gesture.
        /// </summary>
        private CategoryEditSnapshot BuildSnapshot()
        {
            var members = _allRows
                .Where(row => row != null)
                .Select(row => new CategoryEditMember(row.ApiName, row.Category, row.ProviderCategory))
                .ToList();
            var renderedLabels = CategoryRows
                .Where(row => row != null)
                .Select(row => row.CategoryLabel)
                .ToList();
            return new CategoryEditSnapshot(_gameId, members, renderedLabels);
        }

        /// <summary>
        /// Folds an edit the editor landed into this view model: the deferred library catch-up,
        /// the rows' membership, the sibling-tab signal and the row rebuild, then the selection
        /// restore for a move. False when the editor wrote nothing.
        /// </summary>
        private bool Finish(CategoryEditOutcome outcome)
        {
            if (outcome == null || !outcome.Applied)
            {
                return false;
            }

            MarkLibraryRefreshDeferred(outcome.AffectsSummaryData);
            if (outcome.CategoryOverrideMap != null)
            {
                ApplyCategoryOverrideMapsToRows(outcome.CategoryOverrideMap, GetCurrentTagOverrideMap());
            }

            RaiseCategoryMetadataPersisted();
            RefreshCategoryRows();
            if (outcome.MovedSelectionLabels != null)
            {
                CategoryRowsMoved?.Invoke(this, outcome.MovedSelectionLabels.ToList());
            }

            return true;
        }
    }
}
