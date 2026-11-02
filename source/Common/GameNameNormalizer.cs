using System;
using System.Globalization;
using System.Linq;
using System.Text;
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

        /// <summary>Whether a character is an apostrophe in any of the forms titles use.</summary>
        public static bool IsApostrophe(char c)
        {
            return c == '\'' || c == '’' || c == '‘' || c == 'ʼ' || c == '`' || c == '´';
        }

        /// <summary>Removes apostrophes, so "Marvel’s" reads as "Marvels" rather than two words.</summary>
        public static string StripApostrophes(string name)
        {
            return string.IsNullOrEmpty(name) ? name : new string(name.Where(c => !IsApostrophe(c)).ToArray());
        }

        /// <summary>
        /// Removes accents from Latin, Greek and Cyrillic letters ("Ragnarök" -> "Ragnarok",
        /// "Pokémon" -> "Pokemon"). Marks on other scripts are kept, since there they change the
        /// letter (Japanese が is not か).
        /// </summary>
        public static string StripDiacritics(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return name;
            }

            var decomposed = name.Normalize(NormalizationForm.FormD);
            var builder = new StringBuilder(decomposed.Length);
            var baseIsAlphabetic = false;
            foreach (var c in decomposed)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                {
                    if (!baseIsAlphabetic)
                    {
                        builder.Append(c);
                    }

                    continue;
                }

                // Latin, IPA, Greek and Cyrillic blocks end at U+052F.
                baseIsAlphabetic = c <= 'ԯ';
                builder.Append(c);
            }

            return builder.ToString().Normalize(NormalizationForm.FormC);
        }

        private static readonly Regex LooseEditionWordRegex = new Regex(
            @"\b(definitive|complete|goty|game of the year|ultimate|gold|deluxe|standard|remastered|director s cut|edition|pack|re elected|enhanced|anniversary|\d+th)\b",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>
        /// A forgiving form for comparing names across stores: lowercase letters and digits only,
        /// without accents, parenthesized text or edition words, so "Deus Ex - Mankind Divided"
        /// equals "Deus Ex: Mankind Divided" and "Fallout: New Vegas Ultimate Edition" equals
        /// "Fallout: New Vegas". Loose enough to pair different releases, so a match on it needs
        /// confirming.
        /// </summary>
        public static string ToLooseKey(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return string.Empty;
            }

            var text = StripDiacritics(StripTrademarkSymbols(name)).ToLowerInvariant();
            text = ParentheticalContentRegex.Replace(text, " ");
            text = Regex.Replace(text, @"[^a-z0-9]+", " ");
            text = LooseEditionWordRegex.Replace(text, " ");
            return WhitespaceRunRegex.Replace(text, " ").Trim();
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
