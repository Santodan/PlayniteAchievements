using System;
using System.Collections.Generic;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;

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

            foreach (var detail in details)
            {
                if (detail == null)
                {
                    continue;
                }

                detail.ProviderKey = providerKey;

                var providerCategory = NormalizeCategory(detail.Category);
                var providerCategoryType = AchievementCategoryTypeHelper.Normalize(detail.CategoryType);

                if (hasManualCapstone)
                {
                    detail.IsCapstone = string.Equals(
                        detail.ApiName,
                        manualCapstone,
                        StringComparison.OrdinalIgnoreCase);
                }

                if (hasCategoryOverrides)
                {
                    var apiName = (detail.ApiName ?? string.Empty).Trim();
                    if (!string.IsNullOrWhiteSpace(apiName) &&
                        categoryOverrides.TryGetValue(apiName, out var overrideCategory) &&
                        !string.IsNullOrWhiteSpace(overrideCategory))
                    {
                        providerCategory = NormalizeCategory(overrideCategory);
                    }
                }

                if (hasCategoryTypeOverrides)
                {
                    var apiName = (detail.ApiName ?? string.Empty).Trim();
                    if (!string.IsNullOrWhiteSpace(apiName) &&
                        categoryTypeOverrides.TryGetValue(apiName, out var overrideCategoryType) &&
                        !string.IsNullOrWhiteSpace(overrideCategoryType))
                    {
                        providerCategoryType = AchievementCategoryTypeHelper.Normalize(overrideCategoryType);
                    }
                }

                detail.Category = AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(providerCategory);
                detail.CategoryType = AchievementCategoryTypeHelper.NormalizeOrDefault(providerCategoryType);
            }
        }

        private static string NormalizeCategory(string value)
        {
            var normalized = (value ?? string.Empty).Trim();
            return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
        }
    }
}
