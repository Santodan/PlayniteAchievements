using System;
using System.Collections.Generic;
using PlayniteAchievements.Common;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.GameCustomData;

namespace PlayniteAchievements.ViewModels.ManageAchievements
{
    public sealed partial class ManageAchievementsCategoryViewModel
    {
        /// <summary>
        /// Each category label mapped to the achievements a push from that row would reach: the
        /// ones carrying the label, plus everything under it. Built once per row rebuild and reused
        /// by the re-stamp after a push, which must not pay for a full rebuild.
        /// </summary>
        private Dictionary<string, List<string>> _subtreeApiNamesByLabel =
            new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The scopes the per-category dropdown offers, shared with the editor so both surfaces
        /// name the filter scale the same way.
        /// </summary>
        public IReadOnlyList<AchievementFilterScopeOption> FilterScopeOptions { get; } =
            AchievementFilterScopes.CreateOptions();

        /// <summary>
        /// Pushes one filter scope onto every achievement in a category and its subcategories.
        /// </summary>
        /// <remarks>
        /// One way, and one write. The category stores nothing of its own: the scope lands on the
        /// achievements, and the row then reads back what they hold. The two stored lists are
        /// replaced wholesale by the writer, so the achievements outside this subtree have to be
        /// carried across untouched rather than left to a partial write.
        /// </remarks>
        public void ApplyCategoryFilterScope(
            ManageAchievementsCategoryMetadataItem row,
            AchievementFilterScope scope)
        {
            // Mixed is what a disagreeing subtree displays, never a choice: pushing it would have
            // no defined meaning for the members.
            if (row == null || scope == AchievementFilterScope.Mixed)
            {
                return;
            }

            var apiNames = ResolveSubtreeApiNames(row.CategoryLabel);
            if (apiNames.Count == 0)
            {
                return;
            }

            using (PerfScope.Start(
                _logger,
                "ManageAchievements.Category.ApplyFilterScope",
                thresholdMs: 10,
                context: "achievements=" + apiNames.Count))
            {
                var filtered = GameCustomDataLookup.GetFilteredAchievementApiNames(_gameId, _settings?.Persisted);
                var summaryFiltered = GameCustomDataLookup.GetSummaryFilteredAchievementApiNames(_gameId, _settings?.Persisted);

                AchievementFilterScopes.Apply(filtered, summaryFiltered, apiNames, scope);

                _achievementOverridesService.SetAchievementFilters(_gameId, filtered, summaryFiltered);
            }

            // Filtering moves counts, so this write keeps the store's default library-wide pass -
            // the mirror sync and the overview delta both hang off it. Only the sibling tabs in
            // this window need telling, and that is what the metadata-persisted signal does.
            RaiseCategoryMetadataPersisted();
            RefreshCategoryFilterScopes();
        }

        /// <summary>
        /// Re-reads the stored filter lists and re-stamps the rows, without rebuilding them. A full
        /// rebuild re-probes every category's art on disk, which a filter push has no reason to pay.
        /// </summary>
        private void RefreshCategoryFilterScopes()
        {
            StampCategoryFilterScopes(CategoryRows);
        }

        private void StampCategoryFilterScopes(IReadOnlyList<ManageAchievementsCategoryMetadataItem> rows)
        {
            if (rows == null || rows.Count == 0)
            {
                return;
            }

            var filtered = GameCustomDataLookup.GetFilteredAchievementApiNames(_gameId, _settings?.Persisted);
            var summaryFiltered = GameCustomDataLookup.GetSummaryFilteredAchievementApiNames(_gameId, _settings?.Persisted);

            _isStampingFilterScopes = true;
            try
            {
                foreach (var row in rows)
                {
                    if (row == null)
                    {
                        continue;
                    }

                    var apiNames = ResolveSubtreeApiNames(row.CategoryLabel);
                    var filteredCount = 0;
                    var summaryEffectiveCount = 0;
                    foreach (var apiName in apiNames)
                    {
                        var isFiltered = filtered.Contains(apiName);
                        if (isFiltered)
                        {
                            filteredCount++;
                        }

                        // Filtering an achievement out entirely already takes it out of summaries,
                        // so it counts toward both. Reading the summary flag alone would report a
                        // fully filtered subtree as disagreeing with itself.
                        if (isFiltered || summaryFiltered.Contains(apiName))
                        {
                            summaryEffectiveCount++;
                        }
                    }

                    row.SetFilterScopeFromMembers(apiNames.Count, filteredCount, summaryEffectiveCount);
                }
            }
            finally
            {
                _isStampingFilterScopes = false;
            }
        }

        private List<string> ResolveSubtreeApiNames(string categoryLabel)
        {
            var label = AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(categoryLabel);
            return _subtreeApiNamesByLabel.TryGetValue(label, out var apiNames) && apiNames != null
                ? apiNames
                : new List<string>();
        }

        private Dictionary<string, List<string>> BuildSubtreeApiNamesByLabel()
        {
            return CategorySubtreeIndex.Build(
                _allRows,
                achievement => achievement.Category,
                achievement => achievement.ApiName);
        }
    }
}
