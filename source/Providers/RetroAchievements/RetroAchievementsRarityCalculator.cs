using System;

namespace PlayniteAchievements.Providers.RetroAchievements
{
    /// <summary>
    /// Pure rarity-percent calculation for RetroAchievements achievements.
    /// Both casual and hardcore rarity use total distinct players as the denominator;
    /// RA's NumAwarded already counts every unlocker (a hardcore unlock also counts as
    /// casual), so casual% = NumAwarded / total players.
    /// </summary>
    internal static class RetroAchievementsRarityCalculator
    {
        /// <summary>
        /// Total distinct players used as the rarity denominator, falling back across the
        /// three RA player counts when the preferred one is unavailable.
        /// </summary>
        public static int ResolveTotalPlayers(int distinctPlayers, int distinctPlayersCasual, int distinctPlayersHardcore)
        {
            distinctPlayers = Math.Max(distinctPlayers, 0);
            distinctPlayersCasual = Math.Max(distinctPlayersCasual, 0);
            distinctPlayersHardcore = Math.Max(distinctPlayersHardcore, 0);

            return distinctPlayers > 0 ? distinctPlayers :
                distinctPlayersCasual > 0 ? distinctPlayersCasual :
                distinctPlayersHardcore;
        }

        /// <summary>
        /// Selects the rarity percentage that matches the achievement's unlock mode:
        /// a hardcore unlock uses hardcore rarity, a softcore-only unlock uses casual rarity,
        /// and a locked achievement falls back to the global setting (<paramref name="useHardcoreRarityForLocked"/>).
        /// Returns null when the relevant award count or player total is unavailable.
        /// </summary>
        public static double? ComputePercent(
            int numAwarded,
            int numAwardedHardcore,
            int totalPlayers,
            bool earnedInHardcore,
            bool earnedSoftcore,
            bool useHardcoreRarityForLocked)
        {
            var casualPercent = totalPlayers > 0 && numAwarded > 0
                ? (double?)(100.0 * Math.Max(numAwarded, 0) / totalPlayers)
                : null;
            var hardcorePercent = totalPlayers > 0 && numAwardedHardcore > 0
                ? (double?)(100.0 * Math.Max(numAwardedHardcore, 0) / totalPlayers)
                : null;

            if (earnedInHardcore)
            {
                return hardcorePercent;
            }

            if (earnedSoftcore)
            {
                return casualPercent;
            }

            return useHardcoreRarityForLocked ? hardcorePercent : casualPercent;
        }
    }
}
