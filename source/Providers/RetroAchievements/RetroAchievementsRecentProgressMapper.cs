using PlayniteAchievements.Providers.RetroAchievements.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PlayniteAchievements.Providers.RetroAchievements
{
    internal static class RetroAchievementsRecentProgressMapper
    {
        public static IReadOnlyList<InGameProgressQueryResult> Map(
            IReadOnlyList<RaRecentAchievement> recent,
            IReadOnlyList<InGameTrackingContext> games,
            Func<string, DateTime, bool> tryMarkSeen)
        {
            var contexts = (games ?? Array.Empty<InGameTrackingContext>())
                .Where(context =>
                    context?.Game != null &&
                    context.CachedSchema?.Achievements != null)
                .ToList();
            if (contexts.Count == 0)
            {
                return Array.Empty<InGameProgressQueryResult>();
            }

            var observationsByGame = contexts.ToDictionary(
                context => context.Game.Id,
                _ => new List<AchievementProgressObservation>());
            var schemaKeysByGame = contexts.ToDictionary(
                context => context.Game.Id,
                context => new HashSet<string>(
                    context.CachedSchema.Achievements
                        .Where(achievement => !string.IsNullOrWhiteSpace(achievement?.ApiName))
                        .Select(achievement => achievement.ApiName.Trim()),
                    StringComparer.OrdinalIgnoreCase));

            foreach (var item in recent ?? Array.Empty<RaRecentAchievement>())
            {
                if (item == null ||
                    item.AchievementId <= 0 ||
                    !TryParseDate(item.Date, out var unlockUtc))
                {
                    continue;
                }

                var apiName = item.AchievementId.ToString(CultureInfo.InvariantCulture);
                foreach (var context in contexts)
                {
                    if (unlockUtc < context.SessionStartUtc ||
                        !schemaKeysByGame[context.Game.Id].Contains(apiName))
                    {
                        continue;
                    }

                    var seenKey =
                        apiName + "|" +
                        unlockUtc.Ticks.ToString(CultureInfo.InvariantCulture) + "|" +
                        item.HardcoreMode.ToString(CultureInfo.InvariantCulture);
                    if (tryMarkSeen != null && !tryMarkSeen(seenKey, unlockUtc))
                    {
                        break;
                    }

                    observationsByGame[context.Game.Id].Add(
                        new AchievementProgressObservation
                        {
                            ApiName = apiName,
                            Unlocked = true,
                            UnlockTimeUtc = unlockUtc,
                            UnlockMode = item.HardcoreMode != 0
                                ? "Hardcore"
                                : "Softcore"
                        });
                    break;
                }
            }

            return contexts
                .Select(context => InGameProgressQueryResult.Succeeded(
                    context.Game.Id,
                    observationsByGame[context.Game.Id],
                    isDelta: true))
                .ToList();
        }

        internal static bool TryParseDate(string value, out DateTime utc)
        {
            if (DateTime.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces |
                DateTimeStyles.AssumeUniversal |
                DateTimeStyles.AdjustToUniversal,
                out utc))
            {
                utc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
                return true;
            }

            utc = default;
            return false;
        }
    }
}
