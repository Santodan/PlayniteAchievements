using System;
using System.Collections.Generic;

namespace PlayniteAchievements.Providers.GuildWars2
{
    /// <summary>
    /// Pure helpers for the Guild Wars 2 provider, isolated from HTTP and Playnite dependencies so
    /// they can be unit tested directly.
    /// </summary>
    internal static class Gw2Parsing
    {
        /// <summary>
        /// The languages the Guild Wars 2 API serves. Anything else falls back to English rather
        /// than returning untranslated keys.
        /// </summary>
        private const string DefaultLanguage = "en";

        private static readonly HashSet<string> SupportedLanguages =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "en", "es", "de", "fr", "zh" };

        private static readonly Dictionary<string, string> LanguageByGlobalLanguage =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["english"] = "en",
                ["german"] = "de",
                ["french"] = "fr",
                ["spanish"] = "es",
                ["latam"] = "es",
                ["schinese"] = "zh",
                ["tchinese"] = "zh",
                ["chinese"] = "zh"
            };

        /// <summary>
        /// Checks common Playnite/store title forms for Guild Wars 2. Store metadata inserts symbols
        /// into the title - the Steam entry is literally "Guild Wars 2(R)" with a registered-trademark
        /// sign - so matching uses a compact letters-and-digits identity instead of an equality test
        /// against one spelling.
        /// </summary>
        public static bool IsGuildWars2Title(string title)
        {
            var normalized = NormalizeTitleIdentity(title);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return false;
            }

            // "guildwars2" prefixed rather than equality, so expansion-suffixed entries such as
            // "Guild Wars 2: Secrets of the Obscure" still match. "gw2" covers shorthand entries.
            // The original Guild Wars normalizes to "guildwars" and is deliberately not matched.
            return normalized.StartsWith("guildwars2", StringComparison.Ordinal) ||
                   normalized.StartsWith("gw2", StringComparison.Ordinal);
        }

        /// <summary>
        /// Maps the plugin's global language to a Guild Wars 2 API lang code. The API serves five
        /// languages; everything else falls back to English.
        /// </summary>
        public static string MapGlobalLanguage(string globalLanguage)
        {
            var trimmed = (globalLanguage ?? string.Empty).Trim();
            if (trimmed.Length == 0)
            {
                return DefaultLanguage;
            }

            if (LanguageByGlobalLanguage.TryGetValue(trimmed, out var mapped))
            {
                return mapped;
            }

            // Already a code such as "de", "de-DE" or "de_DE".
            var prefix = trimmed.Replace('_', '-');
            var separator = prefix.IndexOf('-');
            if (separator > 0)
            {
                prefix = prefix.Substring(0, separator);
            }

            return SupportedLanguages.Contains(prefix) ? prefix.ToLowerInvariant() : DefaultLanguage;
        }

        /// <summary>
        /// Strips a title down to its lowercase letters and digits, so punctuation and symbols the
        /// stores add cannot defeat a match.
        /// </summary>
        internal static string NormalizeTitleIdentity(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return null;
            }

            var chars = new char[title.Length];
            var index = 0;
            foreach (var c in title)
            {
                if (char.IsLetterOrDigit(c))
                {
                    chars[index++] = char.ToLowerInvariant(c);
                }
            }

            return index == 0 ? null : new string(chars, 0, index);
        }
    }
}
