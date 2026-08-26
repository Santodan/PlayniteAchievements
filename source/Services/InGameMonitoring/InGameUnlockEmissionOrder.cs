using System;
using System.Collections.Generic;
using System.Linq;
using PlayniteAchievements.Models.Achievements;

namespace PlayniteAchievements.Services.InGameMonitoring
{
    /// <summary>
    /// The order a batch of newly observed user unlocks is emitted to the notification queue:
    /// unlock time ascending (chronological toasts; timestampless unlocks last), then the game's
    /// default order (custom order when one exists, provider order otherwise) ascending, so
    /// same-moment unlocks notify in the order the game lists them. The sort is stable, so a batch
    /// whose details were never stamped with a default-order index keeps its incoming provider
    /// enumeration order.
    /// </summary>
    internal static class InGameUnlockEmissionOrder
    {
        public static List<AchievementDetail> Sort(IEnumerable<AchievementDetail> unlocks)
        {
            return (unlocks ?? Enumerable.Empty<AchievementDetail>())
                .OrderBy(a => NormalizeUnlockTime(a?.UnlockTimeUtc) ?? DateTime.MaxValue)
                .ThenBy(a => a?.DefaultOrderIndex ?? int.MaxValue)
                .ToList();
        }

        private static DateTime? NormalizeUnlockTime(DateTime? value)
        {
            if (!value.HasValue || value.Value == DateTime.MinValue)
            {
                return null;
            }

            var utc = value.Value;
            if (utc.Kind == DateTimeKind.Local)
            {
                return utc.ToUniversalTime();
            }

            return utc.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(utc, DateTimeKind.Utc)
                : utc;
        }
    }
}
