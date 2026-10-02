using System;
using System.Collections.Generic;

namespace PlayniteAchievements.Providers.Riot
{
    /// <summary>
    /// The challenge tier ladder. Riot reports a player's tier as one of these names, and
    /// CommunityDragon keys both thresholds and token art by the same names.
    /// </summary>
    internal static class RiotChallengeLevels
    {
        public const string None = "NONE";
        public const string Iron = "IRON";

        /// <summary>Ascending tier order. Index 0 is the "not yet started" state.</summary>
        public static readonly string[] Ascending =
        {
            None,
            Iron,
            "BRONZE",
            "SILVER",
            "GOLD",
            "PLATINUM",
            "DIAMOND",
            "MASTER",
            "GRANDMASTER",
            "CHALLENGER"
        };

        private static readonly Dictionary<string, int> RankByLevel = BuildRankIndex();

        private static Dictionary<string, int> BuildRankIndex()
        {
            var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < Ascending.Length; i++)
            {
                index[Ascending[i]] = i;
            }

            return index;
        }

        /// <summary>
        /// Position on the ladder, 0 for NONE. Unrecognized names rank 0 so an unfamiliar tier
        /// degrades to "locked" rather than throwing.
        /// </summary>
        public static int GetRank(string level)
        {
            if (string.IsNullOrWhiteSpace(level))
            {
                return 0;
            }

            return RankByLevel.TryGetValue(level.Trim(), out var rank) ? rank : 0;
        }

        public static bool IsUnlocked(string level) => GetRank(level) > 0;

        /// <summary>Canonical upper-case name, or <see cref="None"/> when unrecognized.</summary>
        public static string Normalize(string level) => Ascending[GetRank(level)];

        /// <summary>
        /// Title-cased tier label for display, e.g. IRON to "Iron". Empty for <see cref="None"/> and
        /// for unrecognized names, so a caller composing a label never renders an empty pair of
        /// parentheses. Derived from <see cref="Ascending"/> so the ladder stays the only source of
        /// tier names.
        /// </summary>
        public static string GetDisplayName(string level)
        {
            var rank = GetRank(level);
            if (rank == 0)
            {
                return string.Empty;
            }

            var canonical = Ascending[rank];
            return char.ToUpperInvariant(canonical[0]) + canonical.Substring(1).ToLowerInvariant();
        }
    }
}
