using System;
using System.Collections.Generic;
using System.Linq;
using PlayniteAchievements.Models.Achievements;

namespace PlayniteAchievements.Providers.Metadata
{
    internal sealed partial class SteamHuntersCategoryEnricher
    {
        // Reuse only metadata: the target's unlock state and progress came from the
        // current save file and must never be replaced by the cached achievement.
        internal static void ApplyCachedCategories(
            IList<AchievementDetail> achievements,
            IEnumerable<AchievementDetail> cachedAchievements)
        {
            var cachedByName = (cachedAchievements ?? Enumerable.Empty<AchievementDetail>())
                .Where(item => !string.IsNullOrWhiteSpace(item?.ApiName))
                .GroupBy(item => item.ApiName.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            foreach (var item in achievements ?? Array.Empty<AchievementDetail>())
            {
                var apiName = NormalizeApiName(item?.ApiName);
                if (apiName != null && cachedByName.TryGetValue(apiName, out var cached))
                {
                    item.Category = cached.Category;
                    item.CategoryKind = cached.CategoryKind;
                    item.Tags = cached.Tags;
                }
            }
        }

    }
}
