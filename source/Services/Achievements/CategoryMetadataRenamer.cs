using System;
using System.Collections.Generic;
using System.Linq;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.GameCustomData;

namespace PlayniteAchievements.Services.Achievements
{
    /// <summary>
    /// Repoints the per-game category metadata that is keyed by category label - the custom order,
    /// the art overrides, and the game-summary art selection - when a label changes.
    ///
    /// Membership (which achievement sits in which category) is keyed by ApiName rather than by
    /// label and is not touched here; callers rewrite that separately.
    /// </summary>
    internal static class CategoryMetadataRenamer
    {
        /// <summary>
        /// Moves every label-keyed metadata entry from <paramref name="sourceCategory"/> to
        /// <paramref name="targetCategory"/>. Art already stored against the target wins, so a
        /// rename that collapses two labels keeps the target's own art and inherits the source's
        /// only where the target has none.
        /// </summary>
        /// <returns>
        /// True when the metadata was written. False for a no-op: a blank label on either side, or
        /// a rename onto the same label.
        /// </returns>
        public static bool Rename(
            Guid gameId,
            string sourceCategory,
            string targetCategory,
            AchievementOverridesService overridesService,
            PersistedSettings fallbackSettings = null,
            GameCustomDataStore store = null)
        {
            if (overridesService == null)
            {
                return false;
            }

            var normalizedSource = AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(sourceCategory);
            var normalizedTarget = AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(targetCategory);
            if (string.IsNullOrWhiteSpace(normalizedSource) ||
                string.IsNullOrWhiteSpace(normalizedTarget) ||
                string.Equals(normalizedSource, normalizedTarget, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var currentOrder = GameCustomDataLookup.GetAchievementCategoryOrder(gameId, fallbackSettings, store);
            var currentImages = GameCustomDataLookup.GetAchievementCategoryImageOverrides(gameId, fallbackSettings, store);
            var nextOrder = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var label in currentOrder ?? Enumerable.Empty<string>())
            {
                var normalized = AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(label);
                if (string.IsNullOrWhiteSpace(normalized))
                {
                    continue;
                }

                if (string.Equals(normalized, normalizedSource, StringComparison.OrdinalIgnoreCase))
                {
                    normalized = normalizedTarget;
                }

                if (seen.Add(normalized))
                {
                    nextOrder.Add(normalized);
                }
            }

            var nextImages = new Dictionary<string, CategoryImageOverrideData>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in currentImages ?? new Dictionary<string, CategoryImageOverrideData>(StringComparer.OrdinalIgnoreCase))
            {
                var key = AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(pair.Key);
                if (string.IsNullOrWhiteSpace(key) || pair.Value == null)
                {
                    continue;
                }

                if (string.Equals(key, normalizedSource, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                nextImages[key] = pair.Value.Clone();
            }

            if (currentImages != null &&
                currentImages.TryGetValue(normalizedSource, out var sourceImages) &&
                sourceImages != null)
            {
                if (!nextImages.TryGetValue(normalizedTarget, out var targetImages) || targetImages == null)
                {
                    nextImages[normalizedTarget] = sourceImages.Clone();
                }
                else if (string.IsNullOrWhiteSpace(targetImages.Art))
                {
                    targetImages.Art = sourceImages.Art;
                }
            }

            var summaryCategory = GameCustomDataLookup.GetGameSummaryCategory(gameId, fallbackSettings, store);
            if (summaryCategory != null &&
                string.Equals(summaryCategory.Label, normalizedSource, StringComparison.OrdinalIgnoreCase))
            {
                summaryCategory = new GameSummaryCategoryData
                {
                    Label = normalizedTarget,
                    ProviderLabel = summaryCategory.ProviderLabel
                };
            }

            overridesService.SetAchievementCategoryMetadata(gameId, nextOrder, nextImages, summaryCategory);
            return true;
        }
    }
}
