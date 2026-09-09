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
        /// The label-keyed metadata a rename produces, computed rather than written so the caller
        /// can land it together with the ApiName-keyed membership rewrite in one store update.
        /// Chaining plans is what lets a multi-row indent collapse into a single write: every write
        /// fans out a synchronous whole-library recompute, so one per moved row made it crawl.
        ///
        /// Every entry moves from <paramref name="sourceCategory"/> to
        /// <paramref name="targetCategory"/>. Art already stored against the target wins, so a
        /// rename that collapses two labels keeps the target's own art and inherits the source's
        /// only where the target has none.
        /// </summary>
        /// <returns>Null for a no-op: a blank label on either side, or a rename onto the same label.</returns>
        public static CategoryMetadataPlan Plan(
            string sourceCategory,
            string targetCategory,
            IReadOnlyList<string> currentOrder,
            IReadOnlyDictionary<string, CategoryImageOverrideData> currentImages,
            GameSummaryCategoryData currentSummaryCategory)
        {
            var normalizedSource = AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(sourceCategory);
            var normalizedTarget = AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(targetCategory);
            if (string.IsNullOrWhiteSpace(normalizedSource) ||
                string.IsNullOrWhiteSpace(normalizedTarget) ||
                string.Equals(normalizedSource, normalizedTarget, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            // Rewriting by prefix rather than exact match is what makes a rename cascade: moving
            // "DLC" to "Extras" has to carry "DLC::Winter" to "Extras::Winter" with it, or the
            // subtree's art and ordering are stranded under a label nothing points at any more.
            var nextOrder = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var label in currentOrder ?? Enumerable.Empty<string>())
            {
                var normalized = CategoryPathHelper.NormalizePath(label);
                if (string.IsNullOrWhiteSpace(normalized))
                {
                    continue;
                }

                normalized = CategoryPathHelper.RewritePrefix(normalized, normalizedSource, normalizedTarget);
                if (seen.Add(normalized))
                {
                    nextOrder.Add(normalized);
                }
            }

            var nextImages = new Dictionary<string, CategoryImageOverrideData>(StringComparer.OrdinalIgnoreCase);
            CategoryImageOverrideData sourceImages = null;
            foreach (var pair in currentImages ?? new Dictionary<string, CategoryImageOverrideData>(StringComparer.OrdinalIgnoreCase))
            {
                var key = CategoryPathHelper.NormalizePath(pair.Key);
                if (string.IsNullOrWhiteSpace(key) || pair.Value == null)
                {
                    continue;
                }

                // The source's own art is folded into the target below, where the target's
                // existing art wins; descendants just have their keys rewritten.
                if (CategoryPathHelper.IsSame(key, normalizedSource))
                {
                    sourceImages = pair.Value;
                    continue;
                }

                nextImages[CategoryPathHelper.RewritePrefix(key, normalizedSource, normalizedTarget)] = pair.Value.Clone();
            }

            if (sourceImages != null)
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

            var summaryCategory = currentSummaryCategory;
            if (summaryCategory != null &&
                CategoryPathHelper.IsSelfOrDescendantOf(summaryCategory.Label, normalizedSource))
            {
                summaryCategory = new GameSummaryCategoryData
                {
                    Label = CategoryPathHelper.RewritePrefix(summaryCategory.Label, normalizedSource, normalizedTarget),
                    ProviderLabel = summaryCategory.ProviderLabel
                };
            }

            return new CategoryMetadataPlan
            {
                Order = nextOrder,
                Images = nextImages,
                SummaryCategory = summaryCategory
            };
        }
    }

    /// <summary>The label-keyed per-game metadata a rename produces, before it is written.</summary>
    internal sealed class CategoryMetadataPlan
    {
        public List<string> Order { get; set; }

        public Dictionary<string, CategoryImageOverrideData> Images { get; set; }

        public GameSummaryCategoryData SummaryCategory { get; set; }
    }
}
