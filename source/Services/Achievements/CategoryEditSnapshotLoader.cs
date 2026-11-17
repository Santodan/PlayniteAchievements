using System;
using System.Collections.Generic;
using System.Linq;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.GameCustomData;

namespace PlayniteAchievements.Services.Achievements
{
    /// <summary>
    /// Builds a <see cref="CategoryEditSnapshot"/> straight from the store for a caller with no
    /// rows of its own, such as a grid's context menu. Reads the same data the Manage tab loads
    /// its rows from, so both see one category tree.
    /// </summary>
    internal static class CategoryEditSnapshotLoader
    {
        public static CategoryEditSnapshot Load(
            Guid gameId,
            AchievementDataService dataService,
            Func<PersistedSettings> settings,
            GameCustomDataStore store = null)
        {
            if (gameId == Guid.Empty || dataService == null)
            {
                return null;
            }

            var rawGameData = dataService.GetRawGameAchievementDataWithCustomAchievements(gameId);
            var achievements = rawGameData?.Achievements?
                .Where(a => a != null && !string.IsNullOrWhiteSpace(a.ApiName))
                .ToList() ?? new List<AchievementDetail>();
            var resolved = GameCustomDataLookup.ResolveGameCustomData(gameId, settings?.Invoke(), store);
            return Build(gameId, achievements, resolved);
        }

        /// <summary>
        /// Members in definition order - the stored achievement order applied over the raw list,
        /// which is also what the Manage tab seeds its first-seen category order from - and the
        /// rendered labels resolved from them plus the label-keyed metadata.
        /// </summary>
        public static CategoryEditSnapshot Build(
            Guid gameId,
            IReadOnlyList<AchievementDetail> achievements,
            ResolvedGameCustomData resolved)
        {
            var overrides = resolved?.Project(o => o.Category) ?? new Dictionary<string, string>();
            var ordered = AchievementOrderHelper.ApplyOrder(
                achievements ?? Array.Empty<AchievementDetail>(),
                a => a.ApiName,
                resolved?.AchievementOrder);

            var members = new List<CategoryEditMember>(ordered.Count);
            foreach (var achievement in ordered)
            {
                var apiName = (achievement.ApiName ?? string.Empty).Trim();
                var providerCategory = (achievement.Category ?? string.Empty).Trim();
                string overrideCategory = null;
                var hasOverride = apiName.Length > 0 &&
                                  overrides.TryGetValue(apiName, out overrideCategory) &&
                                  !string.IsNullOrWhiteSpace(overrideCategory);
                members.Add(new CategoryEditMember(
                    apiName,
                    hasOverride ? overrideCategory : providerCategory,
                    providerCategory));
            }

            var renderedLabels = CategoryEditor.ResolveRenderedLabels(
                members.Select(member => member.Category),
                resolved?.AchievementCategoryOrder,
                resolved?.AchievementCategoryImageOverrides?.Keys,
                resolved?.GameSummaryCategory?.Label);
            return new CategoryEditSnapshot(gameId, members, renderedLabels);
        }
    }
}
