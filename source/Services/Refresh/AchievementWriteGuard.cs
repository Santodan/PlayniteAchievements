using System;
using System.Collections.Generic;
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
    /// <para>
    /// The stored unlock bit is therefore monotonic for provider payloads: <see cref="PreserveCachedUnlocks"/>
    /// carries a cached unlock forward over a payload that reports it locked. The bit doubles as the
    /// in-game monitor's record of what it has already announced, so a provider snapshot that has not
    /// yet caught up with a locally detected unlock would otherwise make that unlock look new again on
    /// the next session. Clearing an unlock is a user action (clear a game's data, or edit manual
    /// tracking), not something a refresh does.
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

        /// <summary>
        /// Restores the cached unlock state onto <paramref name="incoming"/> for every achievement the
        /// cache holds unlocked and the payload reports locked, so persisting the payload cannot erase
        /// an unlock the plugin already recorded. The cached unlock time comes along, because the
        /// payload carries none for an achievement it believes is locked.
        /// <para>
        /// A recorded unlock time is also never traded for a null on an achievement the payload
        /// agrees is unlocked: some sources date only their most recent unlock, so the moment the
        /// plugin already captured is the better answer.
        /// </para>
        /// </summary>
        /// <returns>The number of unlocks carried forward.</returns>
        public static int PreserveCachedUnlocks(
            GameAchievementData previous,
            GameAchievementData incoming)
        {
            var incomingAchievements = incoming?.Achievements;
            if (incomingAchievements == null || incomingAchievements.Count == 0)
            {
                return 0;
            }

            var cachedUnlocks = BuildUnlockedLookup(previous);
            if (cachedUnlocks.Count == 0)
            {
                return 0;
            }

            var preserved = 0;
            foreach (var achievement in incomingAchievements)
            {
                if (achievement == null ||
                    string.IsNullOrWhiteSpace(achievement.ApiName) ||
                    !cachedUnlocks.TryGetValue(achievement.ApiName.Trim(), out var cached))
                {
                    continue;
                }

                if (!achievement.Unlocked)
                {
                    achievement.Unlocked = true;
                    achievement.UnlockTimeUtc = cached.UnlockTimeUtc;
                    preserved++;
                    continue;
                }

                // A payload can report an achievement unlocked while carrying no moment for it -
                // a source that only dates its most recent unlock, or a scrape that renders a date
                // for some rows and not others. The cached time is the better answer, so an unlock
                // time already recorded is never traded for a null.
                if (!achievement.UnlockTimeUtc.HasValue && cached.UnlockTimeUtc.HasValue)
                {
                    achievement.UnlockTimeUtc = cached.UnlockTimeUtc;
                }
            }

            return preserved;
        }

        private static Dictionary<string, AchievementDetail> BuildUnlockedLookup(GameAchievementData data)
        {
            var result = new Dictionary<string, AchievementDetail>(StringComparer.OrdinalIgnoreCase);
            var achievements = data?.Achievements;
            if (achievements == null)
            {
                return result;
            }

            foreach (var achievement in achievements)
            {
                if (achievement == null ||
                    !achievement.Unlocked ||
                    string.IsNullOrWhiteSpace(achievement.ApiName))
                {
                    continue;
                }

                result[achievement.ApiName.Trim()] = achievement;
            }

            return result;
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
