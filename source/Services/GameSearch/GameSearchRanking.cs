using PlayniteAchievements.Common;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Services.GameSearch
{
    /// <summary>Orders search results by how closely their names match the query.</summary>
    public static class GameSearchRanking
    {
        /// <summary>
        /// Best match first, by <see cref="GameNameNormalizer.ComputeMatchScore"/> over both names
        /// normalized the same way; equal scores keep the source's own order.
        /// </summary>
        public static IReadOnlyList<GameSearchResult> Rank(string query, IEnumerable<GameSearchResult> results)
        {
            var normalizedQuery = Normalize(query);
            return (results ?? Enumerable.Empty<GameSearchResult>())
                .Where(result => result != null)
                .Select((result, index) => (result, index, score: GameNameNormalizer.ComputeMatchScore(normalizedQuery, Normalize(result.Name))))
                .OrderByDescending(entry => entry.score)
                .ThenBy(entry => entry.index)
                .Select(entry => entry.result)
                .ToList();
        }

        /// <summary>The form both names are compared in: no symbols, edition suffix, accents, apostrophes or separator punctuation.</summary>
        public static string Normalize(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return string.Empty;
            }

            var text = GameNameNormalizer.StripEditionSuffix(GameNameNormalizer.StripTrademarkSymbols(name));
            text = GameNameNormalizer.StripApostrophes(GameNameNormalizer.StripDiacritics(text));
            return GameNameNormalizer.CollapseWhitespace(GameNameNormalizer.CollapseSeparators(text)) ?? string.Empty;
        }
    }
}
