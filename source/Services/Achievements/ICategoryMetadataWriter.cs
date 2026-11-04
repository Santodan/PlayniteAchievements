using System;
using System.Collections.Generic;
using PlayniteAchievements.Models.Settings;

namespace PlayniteAchievements.Services.Achievements
{
    /// <summary>
    /// The two store writes a category edit lands: membership plus the label-keyed metadata in one
    /// update, or the metadata alone. <see cref="AchievementOverridesService"/> implements it; the
    /// editor depends on this seam so a test can record writes against a temporary store.
    /// </summary>
    public interface ICategoryMetadataWriter
    {
        /// <returns>True when auto capstones were rescoped by the write.</returns>
        bool SetAchievementCategoryAssignmentAndMetadata(
            Guid gameId,
            IReadOnlyDictionary<string, string> categoryOverrides,
            IReadOnlyDictionary<string, string> categoryKinds,
            IReadOnlyList<string> categoryOrder,
            IReadOnlyDictionary<string, CategoryImageOverrideData> categoryImageOverrides,
            GameSummaryCategoryData gameSummaryCategory,
            bool affectsSummaryData = true,
            bool rescopeAutoCapstones = false);

        void SetAchievementCategoryMetadata(
            Guid gameId,
            IReadOnlyList<string> categoryOrder,
            IReadOnlyDictionary<string, CategoryImageOverrideData> categoryImageOverrides,
            GameSummaryCategoryData gameSummaryCategory,
            bool affectsSummaryData = true);
    }
}
