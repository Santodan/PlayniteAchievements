using PlayniteAchievements.Providers.RetroAchievements.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PlayniteAchievements.Providers.RetroAchievements
{
    internal static class RetroAchievementsRecentProgressMapper
    {
        /// <summary>
        /// Maps the recent-achievements feed to progress observations.
        ///
        /// <paramref name="toCaptureTimeline"/> expresses the feed's server-clock unlock stamps on
        /// this machine's timeline. Everything downstream -- the session-start comparison below,
        /// the stored unlock time, and the clip anchor -- then works in one clock. Without it a
        /// machine whose clock differs from RetroAchievements' both anchors clips at the wrong
        /// moment and silently drops unlocks that appear to predate the session.
        /// </summary>
        public static IReadOnlyList<InGameProgressQueryResult> Map(
            IReadOnlyList<RaRecentAchievement> recent,
            IReadOnlyList<InGameTrackingContext> games,
            Func<string, DateTime, bool> tryMarkSeen,
            Func<DateTime, DateTime?> toCaptureTimeline = null)
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
                    !TryParseDate(item.Date, out var reportedUtc))
                {
                    continue;
                }

                // The feed reports whole seconds, so the unlock fell somewhere inside the second it
                // names; aim at the middle rather than its start, which would bias every stamp
                // half a second early. No clock sample yet leaves the stamp unconverted: it is
                // still the right value to store and display, and the anchor selector withholds it
                // from the recording in favour of the local observation.
                var unlockUtc =
                    toCaptureTimeline?.Invoke(reportedUtc.AddMilliseconds(500)) ?? reportedUtc;

                var apiName = item.AchievementId.ToString(CultureInfo.InvariantCulture);
                foreach (var context in contexts)
                {
                    if (unlockUtc < context.SessionStartUtc ||
                        !schemaKeysByGame[context.Game.Id].Contains(apiName))
                    {
                        continue;
                    }

                    // Keyed on the raw server stamp, not the converted one: the clock offset is
                    // refined as faster round trips are sampled, and a key that shifted with it
                    // would re-notify an unlock the feed's lookback keeps returning.
                    var seenKey =
                        apiName + "|" +
                        reportedUtc.Ticks.ToString(CultureInfo.InvariantCulture) + "|" +
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
