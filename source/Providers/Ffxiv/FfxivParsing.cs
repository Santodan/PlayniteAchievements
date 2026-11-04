using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace PlayniteAchievements.Providers.Ffxiv
{
    /// <summary>
    /// Pure parsing helpers for the FFXIV provider, isolated from HTTP and Playnite
    /// dependencies so they can be unit tested directly.
    /// </summary>
    internal static class FfxivParsing
    {
        private static readonly Regex CharacterIdRegex =
            new Regex("/(?:lodestone/character|characters)/(\\d+)(?:[/?#]|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// FFXIV Collect categories whose achievements are only obtainable during a
        /// limited-time window, mapped to the canonical "Missable" tag.
        /// FFXIV Collect exposes no obtainable flag, so the category is the signal.
        /// </summary>
        private static readonly HashSet<string> MissableCategories =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Seasonal Events"
            };

        /// <summary>
        /// Extracts a Lodestone character id from a Lodestone profile URL
        /// (".../lodestone/character/123/"), an FFXIV Collect profile URL
        /// (".../characters/123") or a bare id. Returns null for anything else.
        /// </summary>
        public static long? ParseCharacterId(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return null;
            }

            var trimmed = input.Trim();
            var match = CharacterIdRegex.Match(trimmed);
            var digits = match.Success ? match.Groups[1].Value : trimmed;

            return long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0
                ? id
                : (long?)null;
        }

        /// <summary>
        /// Checks common Playnite/store title forms for Final Fantasy XIV.
        /// Store metadata sometimes inserts punctuation or symbols into the title
        /// (for example "FINAL FANTASY® XIV Online"), so matching uses a compact
        /// letters-and-digits identity instead of raw substring checks.
        /// </summary>
        public static bool IsFinalFantasyXivTitle(string title)
        {
            var normalized = NormalizeTitleIdentity(title);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return false;
            }

            return normalized.Contains("ffxiv") ||
                   normalized.Contains("ff14") ||
                   normalized.Contains("finalfantasyxiv") ||
                   normalized.Contains("finalfantasy14");
        }

        /// <summary>
        /// Parses an FFXIV Collect ownership string such as "98%" into a 0-100 value.
        /// </summary>
        public static double? ParseOwnedPercent(string owned)
        {
            if (string.IsNullOrWhiteSpace(owned))
            {
                return null;
            }

            var trimmed = owned.Trim().TrimEnd('%').Trim();
            if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                return Math.Max(0, Math.Min(100, value));
            }

            return null;
        }

        private static string NormalizeTitleIdentity(string title)
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

        /// <summary>
        /// Rewrites the FFXIV Collect icon URL from webp to png. WebP decoding depends on an
        /// optional OS component, so requesting png keeps these icons readable on every machine.
        /// </summary>
        public static string NormalizeIconUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return url;
            }

            return url.Replace("format=webp", "format=png");
        }

        /// <summary>
        /// Maps an FFXIV Collect category name to a canonical achievement category
        /// type. Limited-time categories become "Missable"; everything else returns
        /// null so the category falls back to the default classification.
        /// </summary>
        public static string ResolveTag(string categoryName)
        {
            if (!string.IsNullOrWhiteSpace(categoryName) && MissableCategories.Contains(categoryName.Trim()))
            {
                return "Missable";
            }

            return null;
        }
    }
}
