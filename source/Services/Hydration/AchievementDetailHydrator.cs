using System;
using System.Collections.Generic;
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
        private readonly PersistedSettings _settings;

        public AchievementDetailHydrator(PersistedSettings settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

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

            customData ??= GameCustomDataLookup.ResolveGameCustomData(playniteGameId, _settings);

            var manualCapstone = customData.ManualCapstoneApiName;
            var hasManualCapstone = !string.IsNullOrWhiteSpace(manualCapstone);

            var categoryOverrides = customData.AchievementCategoryOverrides ??
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var hasCategoryOverrides = categoryOverrides.Count > 0;

            var categoryTypeOverrides = customData.AchievementCategoryTypeOverrides ??
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var hasCategoryTypeOverrides = categoryTypeOverrides.Count > 0;

            var filteredApiNames = customData.FilteredAchievementApiNames ??
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var summaryFilteredApiNames = customData.SummaryFilteredAchievementApiNames ??
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var achievementNotes = customData.AchievementNotes ??
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var detail in details)
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

                if (hasManualCapstone)
                {
                    detail.IsCapstone = string.Equals(
                        apiName,
                        manualCapstone,
                        StringComparison.OrdinalIgnoreCase);
                }

                if (hasCategoryOverrides)
                {
                    if (!string.IsNullOrWhiteSpace(apiName) &&
                        categoryOverrides.TryGetValue(apiName, out var overrideCategory) &&
                        !string.IsNullOrWhiteSpace(overrideCategory))
                    {
                        providerCategory = NormalizeCategory(overrideCategory);
                    }
                }

                if (hasCategoryTypeOverrides)
                {
                    if (!string.IsNullOrWhiteSpace(apiName) &&
                        categoryTypeOverrides.TryGetValue(apiName, out var overrideCategoryType) &&
                        !string.IsNullOrWhiteSpace(overrideCategoryType))
                    {
                        providerCategoryType = AchievementCategoryTypeHelper.Normalize(overrideCategoryType);
                    }
                }

                detail.Category = AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(providerCategory);
                detail.CategoryType = AchievementCategoryTypeHelper.NormalizeOrDefault(providerCategoryType);
                detail.IsFiltered = !string.IsNullOrWhiteSpace(apiName) && filteredApiNames.Contains(apiName);
                detail.IsFilteredFromSummaries = !string.IsNullOrWhiteSpace(apiName) &&
                                                 summaryFilteredApiNames.Contains(apiName);
                detail.AchievementNote = !string.IsNullOrWhiteSpace(apiName) &&
                                         achievementNotes.TryGetValue(apiName, out var note)
                    ? note
                    : null;
            }
        }

        private static string NormalizeCategory(string value)
        {
            var normalized = (value ?? string.Empty).Trim();
            return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
        }
    }
}
