using PlayniteAchievements.Services.Achievements;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace PlayniteAchievements.Providers.Metadata.TrueGaming
{
    /// <summary>
    /// Tag keys for TrueGaming achievement flags. A flag that means what a built-in tag means
    /// takes that tag's key; every other flag keeps its own key, built from its name, so no two
    /// flags share one. Flags that mark an achievement as no longer earnable also carry the
    /// Unobtainable system tag.
    /// </summary>
    internal static class TrueGamingFlags
    {
        private static readonly Dictionary<string, string[]> KeysByName =
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["Single Player"] = new[] { "Singleplayer" },
                ["Main Storyline"] = new[] { "Progression" },
                ["Story Completed"] = new[] { "WinCondition" },
                ["Collectable"] = new[] { "Collectable" },
                ["Stackable"] = new[] { "Stackable" },
                ["Difficulty Specific"] = new[] { "Difficulty" },
                ["Missable"] = new[] { AchievementTagHelper.Missable },
                ["Unobtainable"] = new[] { AchievementTagHelper.Unobtainable },
                ["Discontinued"] = new[] { "Discontinued", AchievementTagHelper.Unobtainable },
                ["Partly Discontinued/Unobtainable"] = new[] { "PartlyDiscontinued", AchievementTagHelper.Unobtainable },
                ["Partly Disc/Unob"] = new[] { "PartlyDiscontinued", AchievementTagHelper.Unobtainable }
            };

        // Flags about one platform's version of a game, which the sites disagree on across platforms.
        private static readonly HashSet<string> PlatformFlagNames =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Buggy +", "Buggy -", "Platinum" };

        /// <summary>Whether a flag describes one platform's version, so it is taken only from that platform's site.</summary>
        public static bool IsPlatformFlag(string flagName) => PlatformFlagNames.Contains((flagName ?? string.Empty).Trim());

        /// <summary>The tag keys of a row's flags, pipe-joined; null for none.</summary>
        public static string ToTags(IEnumerable<string> flagNames) =>
            AchievementTagHelper.Combine((flagNames ?? Enumerable.Empty<string>()).SelectMany(KeysFor));

        internal static IEnumerable<string> KeysFor(string flagName)
        {
            var name = (flagName ?? string.Empty).Trim();
            if (name.Length == 0)
            {
                return Enumerable.Empty<string>();
            }

            return KeysByName.TryGetValue(name, out var keys) ? keys : new[] { KeyFromName(name) };
        }

        /// <summary>"Buggy +" as "BuggyPlus", "Online/Offline" as "OnlineOffline", "Time/Date" as "TimeDate".</summary>
        internal static string KeyFromName(string name)
        {
            var key = new StringBuilder();
            var upperNext = true;
            foreach (var c in name)
            {
                if (char.IsLetterOrDigit(c))
                {
                    key.Append(upperNext ? char.ToUpperInvariant(c) : c);
                    upperNext = false;
                }
                else
                {
                    if (c == '+')
                    {
                        key.Append("Plus");
                    }
                    else if (c == '-' && key.Length > 0 && name.TrimEnd().EndsWith("-", StringComparison.Ordinal))
                    {
                        key.Append("Minus");
                    }

                    upperNext = true;
                }
            }

            return key.ToString();
        }
    }
}
