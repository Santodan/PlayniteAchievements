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
        public void HydrateAllWithCapstoneOverride(
            IEnumerable<AchievementDetail> details,
            Guid playniteGameId)
        {
            if (details == null)
            {
                return;
            }

            // Resolve manual capstone override from settings.
            var hasManualCapstone = _settings.ManualCapstones
                .TryGetValue(playniteGameId, out var manualCapstone);

            // Resolve manual category overrides from settings.
            Dictionary<string, string> categoryOverrides = null;
            var hasCategoryOverrides = _settings.AchievementCategoryOverrides != null &&
                                       _settings.AchievementCategoryOverrides.TryGetValue(playniteGameId, out categoryOverrides) &&
                                       categoryOverrides != null &&
                                       categoryOverrides.Count > 0;

            Dictionary<string, string> categoryTypeOverrides = null;
            var hasCategoryTypeOverrides = _settings.AchievementCategoryTypeOverrides != null &&
                                           _settings.AchievementCategoryTypeOverrides.TryGetValue(playniteGameId, out categoryTypeOverrides) &&
                                           categoryTypeOverrides != null &&
                                           categoryTypeOverrides.Count > 0;

            foreach (var detail in details)
            {
                if (detail == null)
                {
                    continue;
                }

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
