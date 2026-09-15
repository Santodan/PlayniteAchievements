using System;
using System.Collections.Generic;
using System.Linq;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.GameCustomData;

namespace PlayniteAchievements.Services.Hydration
{
    /// <summary>
    /// Hydrates AchievementDetail with non-persisted properties derived from plugin settings.
    /// </summary>
    public class AchievementDetailHydrator
    {
        // The settings wrapper, not its PersistedSettings: CancelEdit replaces the
        // Persisted instance, and this hydrator outlives a settings dialog.
        private readonly PlayniteAchievementsSettings _settingsHost;

        public AchievementDetailHydrator(PlayniteAchievementsSettings settings)
        {
            _settingsHost = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        private PersistedSettings Persisted => _settingsHost.Persisted;

        /// <summary>
        /// Hydrates multiple AchievementDetail instances and applies manual capstone
        /// override from settings.
        /// </summary>
        internal void HydrateAllWithCapstoneOverride(
            IEnumerable<AchievementDetail> details,
            Guid playniteGameId,
            string providerKey,
            ResolvedGameCustomData customData = null)
        {
            if (details == null)
            {
                return;
            }

            var detailList = details as IList<AchievementDetail> ?? details.ToList();

            customData ??= GameCustomDataLookup.ResolveGameCustomData(playniteGameId, Persisted);

            // The incoming list is provider-ordered (cache reads sort by definition rowid), so its
            // position under the custom-order overlay is the game's default order. Unlock-time
            // sorts and toast emission tie-break on this index.
            var orderedForIndex = AchievementOrderHelper.ApplyOrder(
                detailList,
                a => a?.ApiName,
                customData.AchievementOrder);
            for (var i = 0; i < orderedForIndex.Count; i++)
            {
                if (orderedForIndex[i] != null)
                {
                    orderedForIndex[i].DefaultOrderIndex = i;
                }
            }

            // One record per achievement carries category, category type, note, the icon paths and
            // the user-editable provider fields, so a row needs a single lookup rather than one per
            // facet.
            var overridesByApiName = customData.ResolveAchievementOverrides();
            var hasOverrides = overridesByApiName != null && overridesByApiName.Count > 0;

            var filteredApiNames = customData.FilteredAchievementApiNames ??
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var summaryFilteredApiNames = customData.SummaryFilteredAchievementApiNames ??
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Goal position is resolved once per game rather than scanning the list per row.
            var goalOrderByApiName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var goalApiNames = customData.GoalAchievementApiNames;
            if (goalApiNames != null)
            {
                for (var i = 0; i < goalApiNames.Count; i++)
                {
                    var goalApiName = (goalApiNames[i] ?? string.Empty).Trim();
                    if (!string.IsNullOrWhiteSpace(goalApiName) && !goalOrderByApiName.ContainsKey(goalApiName))
                    {
                        goalOrderByApiName[goalApiName] = i;
                    }
                }
            }

            foreach (var detail in detailList)
            {
                if (detail == null)
                {
                    continue;
                }

                detail.ProviderKey = providerKey;

                var apiName = (detail.ApiName ?? string.Empty).Trim();
                // Prefer the previously captured provider label: re-hydrating an instance whose
                // Category was already overwritten by a rename override must not lose it.
                var providerCategory = NormalizeCategory(detail.ProviderCategory ?? detail.Category);
                detail.ProviderCategory = providerCategory;
                var providerCategoryType = AchievementCategoryTypeHelper.Normalize(detail.CategoryType);

                AchievementOverride userOverride = null;
                if (hasOverrides && !string.IsNullOrWhiteSpace(apiName))
                {
                    overridesByApiName.TryGetValue(apiName, out userOverride);
                }

                if (userOverride != null)
                {
                    if (!string.IsNullOrWhiteSpace(userOverride.Category))
                    {
                        providerCategory = NormalizeCategory(userOverride.Category);
                    }

                    if (!string.IsNullOrWhiteSpace(userOverride.CategoryType))
                    {
                        providerCategoryType = AchievementCategoryTypeHelper.Normalize(userOverride.CategoryType);
                    }

                    AchievementOverrideApplier.Apply(detail, userOverride, customData.HasManualLink);
                }

                // NormalizePath, not NormalizeCategoryOrDefault: a provider may now supply a nested
                // path, and this is the one place every provider label passes through, so the depth
                // cap and empty-segment rules apply to provider input as they do to user input.
                // Blank still resolves to the Default label, so a provider that supplies nothing is
                // unaffected.
                detail.Category = CategoryPathHelper.NormalizePath(providerCategory);
                detail.CategoryType = AchievementCategoryTypeHelper.NormalizeOrDefault(providerCategoryType);
                detail.IsFiltered = !string.IsNullOrWhiteSpace(apiName) && filteredApiNames.Contains(apiName);
                detail.IsFilteredFromSummaries = !string.IsNullOrWhiteSpace(apiName) &&
                                                 summaryFilteredApiNames.Contains(apiName);
                detail.AchievementNote = userOverride?.Note;

                // An unlocked achievement is never an effective goal, so display stays correct
                // even before the stored list is pruned.
                var goalOrderIndex = int.MaxValue;
                if (!detail.Unlocked &&
                    !string.IsNullOrWhiteSpace(apiName) &&
                    goalOrderByApiName.TryGetValue(apiName, out var resolvedGoalIndex))
                {
                    goalOrderIndex = resolvedGoalIndex;
                }

                detail.IsGoal = goalOrderIndex != int.MaxValue;
                detail.GoalOrderIndex = goalOrderIndex;
            }

            StampCapstones(detailList, customData);
        }

        /// <summary>
        /// Marks the game's capstones, in a pass of its own because a category-scoped capstone is
        /// filed by its achievement's category and the loop above is where that category is
        /// finally decided. Stamping inside it would file every capstone under the provider's label
        /// and quietly lose any the user had re-filed.
        /// </summary>
        private static void StampCapstones(
            IList<AchievementDetail> detailList,
            ResolvedGameCustomData customData)
        {
            // An untouched game keeps whatever the provider flagged, so there is nothing to stamp.
            if (customData?.CapstonesMaterialized != true)
            {
                return;
            }

            var resolver = CapstoneResolver.Resolve(detailList, customData.Capstones, true);
            foreach (var detail in detailList)
            {
                if (detail != null)
                {
                    detail.IsCapstone = resolver.IsCapstone(detail.ApiName);
                }
            }
        }

        private static string NormalizeCategory(string value)
        {
            var normalized = (value ?? string.Empty).Trim();
            return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
        }
    }
}
