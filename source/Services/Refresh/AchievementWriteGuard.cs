using System.Linq;
using PlayniteAchievements.Models.Achievements;

namespace PlayniteAchievements.Services.Refresh
{
    /// <summary>
    /// Decides whether a refreshed provider payload may replace the cached payload for a game.
    /// <para>
    /// Cache writes are destructive: rows absent from the incoming set are deleted and a locked
    /// achievement overwrites an unlocked one. A provider that reports an empty or fully locked
    /// result for a game that already has unlocks therefore erases data, and the automatic
    /// game-close refresh makes that a routine occurrence rather than an edge case.
    /// </para>
    /// </summary>
    internal static class AchievementWriteGuard
    {
        /// <summary>
        /// Determines whether persisting <paramref name="incoming"/> over <paramref name="previous"/>
        /// would discard achievement data. Returns false when there is nothing to lose, so first
        /// scans and games that genuinely have no achievements are unaffected.
        /// </summary>
        /// <param name="reason">Set when the write is rejected; describes what would be lost.</param>
        public static bool ShouldRejectWrite(
            GameAchievementData previous,
            GameAchievementData incoming,
            out string reason)
        {
            reason = null;

            var previousTotal = Count(previous);
            if (previousTotal == 0)
            {
                return false;
            }

            var incomingTotal = Count(incoming);
            if (incomingTotal == 0)
            {
                reason = $"payload is empty (cached total={previousTotal})";
                return true;
            }

            var previousUnlocked = CountUnlocked(previous);
            var incomingUnlocked = CountUnlocked(incoming);
            if (previousUnlocked > 0 && incomingUnlocked == 0)
            {
                reason = $"payload reports no unlocks (cached unlocked={previousUnlocked})";
                return true;
            }

            return false;
        }

        /// <summary>
        /// True when the incoming payload keeps some unlocks but fewer than the cache holds.
        /// Allowed, because achievements can be removed from a game's schema, but worth logging.
        /// </summary>
        public static bool IsPartialUnlockRegression(
            GameAchievementData previous,
            GameAchievementData incoming,
            out int previousUnlocked,
            out int incomingUnlocked)
        {
            previousUnlocked = CountUnlocked(previous);
            incomingUnlocked = CountUnlocked(incoming);
            return incomingUnlocked > 0 && incomingUnlocked < previousUnlocked;
        }

        private static int Count(GameAchievementData data)
        {
            return data?.Achievements?.Count ?? 0;
        }

        private static int CountUnlocked(GameAchievementData data)
        {
            var achievements = data?.Achievements;
            if (achievements == null)
            {
                return 0;
            }

            return achievements.Count(a => a != null && a.Unlocked);
        }
    }
}
