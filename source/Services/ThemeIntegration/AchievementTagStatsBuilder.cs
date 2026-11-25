using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.ThemeIntegration;
using PlayniteAchievements.Services.Achievements;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Services.ThemeIntegration
{
    internal static class AchievementTagStatsBuilder
    {
        /// <summary>
        /// One entry per shown tag that <paramref name="tallies"/> count at least one achievement
        /// for, in the order of the tag list. <paramref name="gameId"/> is the game the tallies
        /// belong to, whose own tags are then included; null for tallies of several games, which
        /// count the global tags.
        /// </summary>
        public static List<AchievementTagStats> Build(IEnumerable<AchievementTally> tallies, Guid? gameId)
        {
            var result = new List<AchievementTagStats>();
            var counted = (tallies ?? Enumerable.Empty<AchievementTally>()).ToList();
            if (counted.Count == 0)
            {
                return result;
            }

            var keys = gameId.HasValue
                ? AchievementTagRegistry.GetShownKeys(gameId, counted.SelectMany(tally => tally.Tags))
                : AchievementTagRegistry.GetShownKeys(null);
            var (perTag, _, _) = AchievementTallyBuilder.CountShown(counted, keys);
            foreach (var key in keys.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var count = perTag[key];
                if (count.Total <= 0)
                {
                    continue;
                }

                var definition = AchievementTagRegistry.Resolve(key, gameId);
                var unlocked = Math.Max(0, Math.Min(count.Unlocked, count.Total));
                result.Add(new AchievementTagStats
                {
                    Key = key,
                    Name = AchievementTagRegistry.GetName(key, gameId),
                    Color = AchievementTagHelper.NormalizeColor(definition?.Color),
                    Icon = AchievementTagHelper.IconGlyph(definition?.Icon),
                    Total = count.Total,
                    Unlocked = unlocked,
                    Locked = count.Total - unlocked
                });
            }

            return result;
        }
    }
}
