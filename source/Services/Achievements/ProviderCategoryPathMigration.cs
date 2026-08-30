using System;
using System.Collections.Generic;
using System.Linq;
using PlayniteAchievements.Models.Settings;

namespace PlayniteAchievements.Services.Achievements
{
    /// <summary>
    /// Repoints a game's label-keyed category metadata when a provider starts emitting a nested
    /// path where it previously emitted one flat "Parent - Child" label.
    ///
    /// PSN, RPCS3 and StarCraft II each used to compose two segments into a single label joined by
    /// " - ". Their achievements move to the new label on their own because membership is keyed by
    /// ApiName, but the custom order, the category art and the game-summary selection are keyed by
    /// label and would be stranded under a label nothing points at any more.
    ///
    /// The old label is rebuilt from the new path's own segments rather than by rewriting the
    /// separator in the stored string: a segment can itself contain " - " - a PSN set titled
    /// "Ratchet &amp; Clank - Size Matters" - and a textual reversal would split it in the wrong
    /// place.
    /// </summary>
    internal static class ProviderCategoryPathMigration
    {
        /// <summary>The join the providers used before they emitted real paths.</summary>
        private const string LegacySeparator = " - ";

        /// <summary>
        /// Plans the repoint for one game, or returns null when nothing moved.
        ///
        /// Self-limiting: a move is only planned where the game holds metadata under the old flat
        /// label and holds none under the new path. Once migrated the old label is gone, so every
        /// later refresh plans nothing and no one-time flag is needed.
        /// </summary>
        public static CategoryMetadataPlan Plan(
            IEnumerable<string> providerCategories,
            IReadOnlyList<string> currentOrder,
            IReadOnlyDictionary<string, CategoryImageOverrideData> currentImages,
            GameSummaryCategoryData currentSummaryCategory)
        {
            var known = BuildKnownLabels(currentOrder, currentImages, currentSummaryCategory);
            if (known.Count == 0)
            {
                return null;
            }

            var order = currentOrder;
            var images = currentImages;
            var summaryCategory = currentSummaryCategory;
            var considered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            CategoryMetadataPlan result = null;

            foreach (var category in providerCategories ?? Enumerable.Empty<string>())
            {
                var newPath = CategoryPathHelper.NormalizePath(category);

                // Only a nested label can have been composed; a flat one never changed.
                if (CategoryPathHelper.GetDepth(newPath) < 2 || !considered.Add(newPath))
                {
                    continue;
                }

                var oldLabel = CategoryPathHelper.NormalizePath(
                    string.Join(LegacySeparator, CategoryPathHelper.Split(newPath)));

                // Nothing stored under the old label means this game was never customized there;
                // something already stored under the new path means the move has happened.
                if (!known.Contains(oldLabel) || known.Contains(newPath))
                {
                    continue;
                }

                // Chained so a game with several moved labels lands as one write, the way a
                // multi-row indent does.
                var plan = CategoryMetadataRenamer.Plan(oldLabel, newPath, order, images, summaryCategory);
                if (plan == null)
                {
                    continue;
                }

                order = plan.Order;
                images = plan.Images;
                summaryCategory = plan.SummaryCategory;
                result = plan;
            }

            return result;
        }

        /// <summary>
        /// Every label this game currently has metadata under, canonicalized so a lookup matches
        /// the form <see cref="CategoryMetadataRenamer"/> writes.
        /// </summary>
        private static HashSet<string> BuildKnownLabels(
            IReadOnlyList<string> currentOrder,
            IReadOnlyDictionary<string, CategoryImageOverrideData> currentImages,
            GameSummaryCategoryData currentSummaryCategory)
        {
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var label in currentOrder ?? Enumerable.Empty<string>())
            {
                AddLabel(known, label);
            }

            foreach (var pair in currentImages ?? new Dictionary<string, CategoryImageOverrideData>())
            {
                if (pair.Value != null)
                {
                    AddLabel(known, pair.Key);
                }
            }

            AddLabel(known, currentSummaryCategory?.Label);
            return known;
        }

        private static void AddLabel(HashSet<string> known, string label)
        {
            if (string.IsNullOrWhiteSpace(label))
            {
                return;
            }

            known.Add(CategoryPathHelper.NormalizePath(label));
        }
    }
}
