using HtmlAgilityPack;
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
        private static readonly Regex LodestoneIdRegex =
            new Regex("/lodestone/character/(\\d+)/", RegexOptions.Compiled);

        /// <summary>
        /// FFXIV Collect categories whose achievements are only obtainable during a
        /// limited-time window, mapped to the canonical "Missable" category type.
        /// FFXIV Collect exposes no obtainable flag, so the category is the signal.
        /// </summary>
        private static readonly HashSet<string> MissableCategories =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Seasonal Events"
            };

        /// <summary>
        /// Extracts the Lodestone character id for an exact name + world match from a
        /// Lodestone character search results page. Returns null when there is no
        /// exact match. The Lodestone search is a partial match (e.g. "Mal Reynolds"
        /// also returns "Malynor Reynolds"), so the first result link cannot be used.
        /// </summary>
        public static long? ParseLodestoneCharacterId(string html, string name, string world)
        {
            if (string.IsNullOrWhiteSpace(html) ||
                string.IsNullOrWhiteSpace(name) ||
                string.IsNullOrWhiteSpace(world))
            {
                return null;
            }

            var wantName = HtmlEntity.DeEntitize(name).Trim();
            var wantWorld = world.Trim();

            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            var entries = doc.DocumentNode.SelectNodes("//a[contains(@class, 'entry__link')]");
            if (entries == null)
            {
                return null;
            }

            foreach (var entry in entries)
            {
                var href = entry.GetAttributeValue("href", string.Empty);
                var idMatch = LodestoneIdRegex.Match(href);
                if (!idMatch.Success)
                {
                    continue;
                }

                var nameNode = entry.SelectSingleNode(".//p[contains(@class, 'entry__name')]");
                var worldNode = entry.SelectSingleNode(".//p[contains(@class, 'entry__world')]");

                var entryName = HtmlEntity.DeEntitize(nameNode?.InnerText ?? string.Empty).Trim();
                var entryWorldText = HtmlEntity.DeEntitize(worldNode?.InnerText ?? string.Empty);
                // entry__world reads like "Gilgamesh [Aether]"; keep the world.
                var entryWorld = entryWorldText.Split('[')[0].Replace(' ', ' ').Trim();

                if (string.Equals(entryName, wantName, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(entryWorld, wantWorld, StringComparison.OrdinalIgnoreCase) &&
                    long.TryParse(idMatch.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                {
                    return id;
                }
            }

            return null;
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
        /// Rewrites the FFXIV Collect icon URL from webp to png. WPF on .NET
        /// Framework 4.6.2 cannot decode webp.
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
        public static string ResolveCategoryType(string categoryName)
        {
            if (!string.IsNullOrWhiteSpace(categoryName) && MissableCategories.Contains(categoryName.Trim()))
            {
                return "Missable";
            }

            return null;
        }
    }
}
