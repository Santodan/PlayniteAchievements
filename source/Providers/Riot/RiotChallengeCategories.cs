using Playnite.SDK;
using System;
using System.Collections.Generic;

namespace PlayniteAchievements.Providers.Riot
{
    /// <summary>
    /// Display names for the five top-level challenge categories. CommunityDragon exposes these
    /// nodes only as raw codes (IMAGINATION, EXPERTISE, ...) because the League client localizes
    /// them from a separate bundle, so the plugin supplies its own localized labels.
    /// </summary>
    internal static class RiotChallengeCategories
    {
        private static readonly Dictionary<string, string> ResourceKeyById =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["1"] = "LOCPlayAch_Riot_ChallengeCategory_Imagination",
                ["2"] = "LOCPlayAch_Riot_ChallengeCategory_Expertise",
                ["3"] = "LOCPlayAch_Riot_ChallengeCategory_Veterancy",
                ["4"] = "LOCPlayAch_Riot_ChallengeCategory_Teamwork",
                ["5"] = "LOCPlayAch_Riot_ChallengeCategory_Collection"
            };

        /// <summary>
        /// Builds the category id to display-name map the mapper consumes. Resolved per call so a
        /// language change takes effect on the next refresh.
        /// </summary>
        public static IReadOnlyDictionary<string, string> BuildDisplayNames()
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in ResourceKeyById)
            {
                var label = ResourceProvider.GetString(pair.Value);
                if (!string.IsNullOrWhiteSpace(label))
                {
                    result[pair.Key] = label;
                }
            }

            return result;
        }
    }
}
