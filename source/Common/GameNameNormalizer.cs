using System;
using System.Text.RegularExpressions;

namespace PlayniteAchievements.Common
{
    /// <summary>
    /// Shared game-title normalization primitives and match scoring. Providers compose
    /// the primitives that fit their catalog's naming conventions so equivalent cleanup
    /// steps behave identically everywhere.
    /// </summary>
    public static class GameNameNormalizer
    {
        private static readonly Regex TrademarkSymbolRegex = new Regex(@"[®™©]", RegexOptions.Compiled);
        private static readonly Regex ParentheticalContentRegex = new Regex(@"\([^)]*\)", RegexOptions.Compiled);
        private static readonly Regex BracketedContentRegex = new Regex(@"\[[^\]]*\]", RegexOptions.Compiled);
        private static readonly Regex TildeTagRegex = new Regex(@"~[^~]+~", RegexOptions.Compiled);
        private static readonly Regex SeparatorRunRegex = new Regex(@"[\s\-_:~\.]+", RegexOptions.Compiled);
        private static readonly Regex WhitespaceRunRegex = new Regex(@"\s+", RegexOptions.Compiled);

        private static readonly string[] EditionSuffixes =
        {
            " - Definitive Edition",
            " - Game of the Year Edition",
            " - Complete Edition",
            " - Collector's Edition",
            " - Deluxe Edition",
            " - Standard Edition",
            " - Ultimate Edition",
            " - Premium Edition",
            " - Director's Cut",
            " - Director’s Cut",
            " - Directors Cut",
            " Definitive Edition",
            " Game of the Year Edition",
            " Complete Edition",
            " Collector's Edition",
            " Deluxe Edition",
            " Standard Edition",
            " Ultimate Edition",
            " Premium Edition",
            " Director's Cut",
            " Director’s Cut",
            " Directors Cut",
            " (Definitive Edition)",
            " (Game of the Year Edition)",
            " (Complete Edition)",
            " (Collector's Edition)",
            " (Deluxe Edition)",
            " (Standard Edition)",
            " (Ultimate Edition)",
            " (Premium Edition)",
            " (Director's Cut)",
            " (Director’s Cut)",
            " (Directors Cut)"
        };

        /// <summary>Removes registered/trademark/copyright symbols.</summary>
        public static string StripTrademarkSymbols(string name)
        {
            return string.IsNullOrEmpty(name) ? name : TrademarkSymbolRegex.Replace(name, "");
        }

        /// <summary>Removes parenthesized content such as region or language tags.</summary>
        public static string StripParentheticals(string name)
        {
            return string.IsNullOrEmpty(name) ? name : ParentheticalContentRegex.Replace(name, "");
        }

        /// <summary>Removes bracketed content such as ROM dump tags.</summary>
        public static string StripBrackets(string name)
        {
            return string.IsNullOrEmpty(name) ? name : BracketedContentRegex.Replace(name, "");
        }

        /// <summary>Removes RetroAchievements-style ~Hack~/~Homebrew~ tags.</summary>
        public static string StripTildeTags(string name)
        {
            return string.IsNullOrEmpty(name) ? name : TildeTagRegex.Replace(name, "");
        }

        /// <summary>
        /// Trims the name and removes one trailing edition suffix
        /// (Deluxe/Definitive/Game of the Year/... in plain, dashed, or parenthesized form).
        /// Case is preserved; downstream comparisons are case-insensitive.
        /// </summary>
        public static string StripEditionSuffix(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return name;
            }

            var normalized = name.Trim();

            foreach (var suffix in EditionSuffixes)
            {
                if (normalized.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    normalized = normalized.Substring(0, normalized.Length - suffix.Length);
                    break;
                }
            }

            return normalized.Trim();
        }

        /// <summary>Collapses runs of whitespace and separator punctuation (-_:~.) to single spaces.</summary>
        public static string CollapseSeparators(string name)
        {
            return string.IsNullOrEmpty(name) ? name : SeparatorRunRegex.Replace(name, " ").Trim();
        }

        /// <summary>Collapses runs of whitespace to single spaces.</summary>
        public static string CollapseWhitespace(string name)
        {
            return string.IsNullOrEmpty(name) ? name : WhitespaceRunRegex.Replace(name, " ").Trim();
        }

        /// <summary>
        /// Scores how well two already-normalized names match, on a 0-100 scale:
        /// exact = 100, prefix = 80, substring (either direction) = 60, otherwise a
        /// Jaro-Winkler fallback (>= 0.94 -> 70, >= 0.88 -> 40, else 0).
        /// </summary>
        public static int ComputeMatchScore(string normalizedSearch, string normalizedTitle)
        {
            if (string.IsNullOrWhiteSpace(normalizedSearch) || string.IsNullOrWhiteSpace(normalizedTitle))
            {
                return 0;
            }

            if (string.Equals(normalizedTitle, normalizedSearch, StringComparison.OrdinalIgnoreCase))
            {
                return 100;
            }

            if (normalizedTitle.StartsWith(normalizedSearch, StringComparison.OrdinalIgnoreCase))
            {
                return 80;
            }

            if (normalizedTitle.IndexOf(normalizedSearch, StringComparison.OrdinalIgnoreCase) >= 0 ||
                normalizedSearch.IndexOf(normalizedTitle, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return 60;
            }

            var similarity = StringSimilarity.JaroWinklerSimilarityIgnoreCase(normalizedSearch, normalizedTitle);
            if (similarity >= 0.94)
            {
                return 70;
            }

            if (similarity >= 0.88)
            {
                return 40;
            }

            return 0;
        }
    }
}
