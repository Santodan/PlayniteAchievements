using System;
using System.Collections.Generic;
using PlayniteAchievements.Services.CustomProviders;

namespace PlayniteAchievements.Models.Achievements.Scoring
{
    /// <summary>
    /// Which points card a game's provider points count toward. A family is named by a provider
    /// key: Xbox covers Xbox and Xenia, PSN covers PSN, RPCS3, ShadPS4 and Vita3K, and every other
    /// provider, custom providers included, is its own family.
    /// </summary>
    public static class PointsFamilies
    {
        public const string Xbox = "Xbox";

        public const string PlayStation = "PSN";

        public const string Epic = "Epic";

        public const string RetroAchievements = "RetroAchievements";

        private static readonly Dictionary<string, string> FamilyByProviderKey =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Xbox"] = Xbox,
                ["Xenia"] = Xbox,
                ["PSN"] = PlayStation,
                ["RPCS3"] = PlayStation,
                ["ShadPS4"] = PlayStation,
                ["Vita3K"] = PlayStation
            };

        /// <summary>
        /// The family a provider key belongs to. Null for a blank key, Unknown, and the plain
        /// Custom key of a custom-only game with no custom provider.
        /// </summary>
        public static string FromProviderKey(string effectiveProviderKey)
        {
            var key = effectiveProviderKey?.Trim();
            if (string.IsNullOrEmpty(key) ||
                string.Equals(key, "Unknown", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(key, CustomProviderKeys.BaseKey, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return FamilyByProviderKey.TryGetValue(key, out var family) ? family : key;
        }

        /// <summary>A family key with provider aliases folded in, or null when blank.</summary>
        public static string Normalize(string family) => FromProviderKey(family);
    }
}
