using System;
using System.Collections.Generic;
using System.Linq;
using PlayniteAchievements.Common;

namespace PlayniteAchievements.Providers.Exophase
{
    /// <summary>
    /// Shared game-name normalization and fuzzy-match scoring used by every Exophase
    /// matching path (catalog search, metadata enrichment, and friend-library resolution).
    /// Centralizing this keeps a single normalization/scoring implementation so asymmetric
    /// edition names (e.g. "Titanfall 2 Deluxe Edition" vs "Titanfall 2") match consistently.
    /// </summary>
    internal static class ExophaseGameNameMatcher
    {
        /// <summary>Score for an exact (case-insensitive) match of two normalized names.</summary>
        public const int ExactMatchScore = 100;

        /// <summary>
        /// Normalizes a game name for matching by trimming and removing a known edition suffix.
        /// Case is preserved; downstream comparisons are case-insensitive.
        /// </summary>
        public static string NormalizeGameName(string name)
        {
            return GameNameNormalizer.StripEditionSuffix(name);
        }

        /// <summary>
        /// Normalizes a game name for use in a slug: edition-stripped, lowercased, with
        /// runs of non-alphanumeric characters collapsed to single hyphens.
        /// </summary>
        public static string NormalizeGameNameForSlug(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            var normalized = NormalizeGameName(name).ToLowerInvariant();

            var chars = new char[normalized.Length];
            var charIndex = 0;
            var lastWasHyphen = false;

            foreach (var c in normalized)
            {
                if (char.IsLetterOrDigit(c))
                {
                    chars[charIndex++] = c;
                    lastWasHyphen = false;
                }
                else if (!lastWasHyphen)
                {
                    chars[charIndex++] = '-';
                    lastWasHyphen = true;
                }
            }

            if (charIndex > 0 && chars[charIndex - 1] == '-')
            {
                charIndex--;
            }

            return new string(chars, 0, charIndex);
        }

        /// <summary>
        /// Scores how well two already-normalized names match, on a 0-100 scale:
        /// exact = 100, prefix = 80, substring (either direction) = 60, otherwise a
        /// Jaro-Winkler fallback (>= 0.94 -> 70, >= 0.88 -> 40, else 0).
        /// </summary>
        public static int ComputeMatchScore(string normalizedSearch, string normalizedTitle)
        {
            return GameNameNormalizer.ComputeMatchScore(normalizedSearch, normalizedTitle);
        }

        /// <summary>Minimum name-match score an archive-search row must reach to be eligible.</summary>
        public const int MinimumSearchMatchScore = 60;

        /// <summary>
        /// Picks the best Exophase archive-search row for a game name and target platform.
        /// Rows are scored by normalized-name match. When rows carry platform data, only
        /// rows listing the target platform stay eligible, which separates same-title
        /// releases on other platforms (e.g. a PS5 remake of a PS3 game). Exophase lists
        /// regional variants of one game as separate same-title entries whose base region
        /// has the shortest slug (name, name-2, name-3, ...), so equal scores resolve to
        /// the shortest slug instead of being discarded; regional trophy lists are the
        /// same set, and achievement-level matching downstream guards the application.
        /// Returns null when nothing reaches the score threshold, or when rows carry
        /// platform data but none lists the target platform. Rows without platform data
        /// keep the legacy URL-suffix platform heuristic and the conservative rule that
        /// an exact score tie yields no match.
        /// </summary>
        public static ExophaseGame SelectBestSearchMatch(string gameName, IList<ExophaseGame> games, string platformSlug)
        {
            if (games == null || games.Count == 0 || string.IsNullOrWhiteSpace(gameName))
            {
                return null;
            }

            var normalizedSearch = NormalizeGameName(gameName);
            var targetPlatform = string.IsNullOrWhiteSpace(platformSlug)
                ? null
                : platformSlug.Trim().ToLowerInvariant();

            var candidates = new List<(ExophaseGame Game, int Score, string Slug, bool PlatformKnown, bool PlatformMatch)>();
            foreach (var game in games)
            {
                if (game == null || string.IsNullOrWhiteSpace(game.EndpointAwards))
                {
                    continue;
                }

                var title = NormalizeGameName(game.Title);
                if (string.IsNullOrWhiteSpace(title))
                {
                    continue;
                }

                var score = ComputeMatchScore(normalizedSearch, title);
                if (score < MinimumSearchMatchScore)
                {
                    continue;
                }

                var platformSlugs = game.Platforms?
                    .Select(platform => platform?.Slug?.Trim().ToLowerInvariant())
                    .Where(slug => !string.IsNullOrWhiteSpace(slug))
                    .ToList();
                var platformKnown = platformSlugs != null && platformSlugs.Count > 0;
                var platformMatch = targetPlatform != null &&
                    (platformKnown
                        ? platformSlugs.Contains(targetPlatform)
                        : game.EndpointAwards.IndexOf($"-{targetPlatform}", StringComparison.OrdinalIgnoreCase) >= 0);

                var resolvedSlug = ExophaseApiClient.ExtractSlugFromUrl(game.EndpointAwards) ?? game.EndpointAwards;
                candidates.Add((game, score, resolvedSlug, platformKnown, platformMatch));
            }

            if (candidates.Count == 0)
            {
                return null;
            }

            if (targetPlatform != null)
            {
                var platformMatched = candidates.Where(candidate => candidate.PlatformMatch).ToList();
                if (platformMatched.Count > 0)
                {
                    return platformMatched
                        .OrderByDescending(candidate => candidate.Score)
                        .ThenBy(candidate => candidate.Slug.Length)
                        .ThenBy(candidate => candidate.Slug, StringComparer.OrdinalIgnoreCase)
                        .First().Game;
                }

                if (candidates.Any(candidate => candidate.PlatformKnown))
                {
                    // Every row declares its platforms and none is the target:
                    // same-title releases on other platforms only. Enriching from
                    // one of those would apply another platform's rarity data.
                    return null;
                }
            }

            var ordered = candidates
                .OrderByDescending(candidate => candidate.Score)
                .ThenBy(candidate => candidate.Slug.Length)
                .ThenBy(candidate => candidate.Slug, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (ordered.Count > 1 && ordered[0].Score == ordered[1].Score)
            {
                return null;
            }

            return ordered[0].Game;
        }
    }
}
