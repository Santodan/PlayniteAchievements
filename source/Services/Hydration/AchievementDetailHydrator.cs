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

            ApplyOverlays(
                details,
                providerKey,
                customData ?? GameCustomDataLookup.ResolveGameCustomData(playniteGameId, Persisted));
        }

        /// <summary>
        /// Applies one game's resolved custom data to its achievements: default order index,
        /// per-achievement overrides, category and category type, filters, note, goal state and
        /// capstones. The store-free core of <see cref="HydrateAllWithCapstoneOverride"/>, for
        /// callers that already hold the resolved record, such as a package preview.
        /// </summary>
        internal static void ApplyOverlays(
            IEnumerable<AchievementDetail> details,
            string providerKey,
            ResolvedGameCustomData customData)
        {
            if (details == null || customData == null)
            {
                return;
            }

            var detailList = details as IList<AchievementDetail> ?? details.ToList();

            // The incoming list is in source order (cache reads sort by the metadata order source's
            // positions, then definition rowid), so its
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
            var overridesByApiName = customData.AchievementOverrides;
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
                AchievementOverride userOverride = null;
                if (hasOverrides && !string.IsNullOrWhiteSpace(apiName))
                {
                    overridesByApiName.TryGetValue(apiName, out userOverride);
                }

                // The platform and metadata values are what the user's customization sits on: the
                // provider category keys default category art and is what an edit compares against.
                detail.CustomLayer = null;
                var below = AchievementLayers.Resolve(detail).Values;
                detail.ProviderCategory = NormalizeCategory(below.Category);
                detail.ProviderTags = AchievementTagHelper.Normalize(below.Tags) ?? string.Empty;
                detail.ProviderCategoryKind = CategoryKindHelper.Normalize(below.CategoryKind) ?? string.Empty;

                if (userOverride != null)
                {
                    detail.CustomLayer = userOverride.ToLayer();
                    AchievementLayers.Resolve(detail);
                }

                userOverride?.ApplyUnlockTimeTo(detail, customData.HasManualLink);

                // NormalizePath applies the depth cap and empty-segment rules to every label, the
                // provider's and the user's alike.
                detail.Category = CategoryPathHelper.NormalizePath(detail.Category);
                detail.CategoryKind = CategoryKindHelper.ResolveEffective(
                    customData.GetStoredCategoryKind(detail.Category),
                    detail.CategoryKind);
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
