using System;
using System.Collections.Generic;
using System.Linq;
using PlayniteAchievements.Common;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.GameCustomData;

namespace PlayniteAchievements.ViewModels.ManageAchievements
{
    public sealed partial class ManageAchievementsCategoryViewModel
    {
        /// <summary>
        /// Each category label mapped to the achievements a push from that row would reach: the
        /// ones carrying exactly that label. Subcategories keep their own scope. Built once per row
        /// rebuild and reused by the re-stamp after a push, which must not pay for a full rebuild.
        /// </summary>
        private Dictionary<string, List<string>> _memberApiNamesByLabel =
            new Dictionary<string, List<string>>(CategoryPathHelper.LabelComparer);

        /// <summary>
        /// The scopes the per-category menu offers, shared with the editor so both surfaces
        /// name the filter scale the same way.
        /// </summary>
        public IReadOnlyList<AchievementFilterScopeOption> FilterScopeOptions { get; } =
            AchievementFilterScopes.CreateOptions();

        /// <summary>
        /// Pushes one filter scope onto every achievement carrying a category's label. Its
        /// subcategories are left as they are.
        /// </summary>
        /// <remarks>
        /// One way, and one write. The category stores nothing of its own: the scope lands on the
        /// achievements, and the row then reads back what they hold. The two stored lists are
        /// replaced wholesale by the writer, so the achievements outside this category have to be
        /// carried across untouched rather than left to a partial write.
        /// </remarks>
        public void ApplyCategoryFilterScope(
            ManageAchievementsCategoryMetadataItem row,
            AchievementFilterScope scope)
        {
            // Mixed is what a disagreeing category displays, never a choice: pushing it would have
            // no defined meaning for the members.
            if (row == null || scope == AchievementFilterScope.Mixed)
            {
                return;
            }

            var apiNames = ResolveMemberApiNames(row.CategoryLabel);
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
            var storedKinds = GameCustomDataLookup.GetAchievementCategoryKinds(_gameId, _settings?.Persisted);
            var rowsByApiName = _allRows
                .Where(item => item != null && !string.IsNullOrWhiteSpace(item.ApiName))
                .GroupBy(item => item.ApiName.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

            _isStampingFilterScopes = true;
            try
            {
                foreach (var row in rows)
                {
                    if (row == null)
                    {
                        continue;
                    }

                    var apiNames = ResolveMemberApiNames(row.CategoryLabel);
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
                        // fully filtered category as disagreeing with itself.
                        if (isFiltered || summaryFiltered.Contains(apiName))
                        {
                            summaryEffectiveCount++;
                        }
                    }

                    row.SetFilterScopeFromMembers(apiNames.Count, filteredCount, summaryEffectiveCount);

                    // A stored kind is the category's; otherwise it has one only when every member agrees.
                    var storedKind = ResolveStoredCategoryKind(storedKinds, row.CategoryLabel);
                    var memberKind = CategoryKindHelper.ResolveUnanimous(apiNames
                        .Select(apiName => rowsByApiName.TryGetValue(apiName, out var member) ? member.CategoryKind : null));
                    row.SetKind(storedKind, storedKind != null ? storedKind : memberKind);
                }
            }
            finally
            {
                _isStampingFilterScopes = false;
            }
        }

        /// <summary>
        /// Sets the kind stored for <paramref name="row"/>'s category: a kind, None, or null to let the
        /// providers decide. Its achievements take it at once, here and in the rest of the plugin.
        /// </summary>
        public void ApplyCategoryKind(ManageAchievementsCategoryMetadataItem row, string kind)
        {
            if (row == null)
            {
                return;
            }

            var apiNames = ResolveMemberApiNames(row.CategoryLabel);
            _achievementOverridesService.SetCategoryKind(_gameId, row.CategoryLabel, kind);
            // The write skips the summary fan-out, so the overview's recent unlocks keep the old
            // kind until the teardown flush refreshes this game's library-scope rows.
            MarkLibraryRefreshDeferred(false);

            var stored = CategoryKindHelper.NormalizeStored(kind);
            var members = new HashSet<string>(apiNames, StringComparer.OrdinalIgnoreCase);
            foreach (var item in _allRows.Where(item => item != null && members.Contains(item.ApiName ?? string.Empty)))
            {
                item.CategoryKind = CategoryKindHelper.ResolveEffective(stored, item.ProviderCategoryKind);
            }

            RaiseCategoryMetadataPersisted();
            RefreshCategoryFilterScopes();
        }

        private List<string> ResolveMemberApiNames(string categoryLabel)
        {
            var label = AchievementCategoryHelper.NormalizeCategoryOrDefault(categoryLabel);
            return _memberApiNamesByLabel.TryGetValue(label, out var apiNames) && apiNames != null
                ? apiNames
                : new List<string>();
        }

        private Dictionary<string, List<string>> BuildMemberApiNamesByLabel()
        {
            return CategoryMemberIndex.Build(
                _allRows,
                achievement => achievement.Category,
                achievement => achievement.ApiName);
        }
    }
}
