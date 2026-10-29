using Playnite.SDK;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Providers.Exophase;
using PlayniteAchievements.Services.Metadata;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.Metadata.Exophase
{
    /// <summary>
    /// Exophase award pages and searches shared by the Exophase metadata branches. Public pages
    /// only, so no Exophase sign-in is involved. One browser view and one page cache span a refresh
    /// run while any branch has it open; a page two branches read for a game is loaded once. The
    /// cache keeps each page's parsed contents, not its source, and only the most recent pages.
    /// </summary>
    internal sealed class ExophasePageSource
    {
        private readonly ExophaseApiClient _client;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly ILogger _logger;
        private readonly object _sync = new object();
        // The branches share a game's pages while it is being looked up, so the cache needs to
        // span only the games in flight, not the whole run.
        private const int PageCacheCapacity = 64;
        private readonly LruCache<string, ExophaseAwardPage> _pages =
            new LruCache<string, ExophaseAwardPage>(PageCacheCapacity, StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _searches = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<ExophaseGame>> _results = new Dictionary<string, List<ExophaseGame>>(StringComparer.OrdinalIgnoreCase);
        private int _openRuns;

        public ExophasePageSource(IPlayniteAPI api, PlayniteAchievementsSettings settings, ILogger logger)
        {
            _client = api == null ? null : new ExophaseApiClient(api, logger, null);
            _settings = settings;
            _logger = logger;
        }

        public bool IsAvailable => _client != null;

        public void BeginRun()
        {
            lock (_sync)
            {
                if (_openRuns++ == 0)
                {
                    _client?.BeginCookieSession();
                }
            }
        }

        public void EndRun()
        {
            lock (_sync)
            {
                if (_openRuns == 0 || --_openRuns > 0)
                {
                    return;
                }

                _client?.EndCookieSession();
                _pages.Clear();
                _results.Clear();
                _searches.Clear();
            }
        }

        /// <summary>
        /// An award page in the plugin's language where Exophase translates it, else English; loaded
        /// once per run. <paramref name="platformSlug"/> picks the page's address (trophies for
        /// PlayStation, challenges for Ubisoft). <paramref name="english"/> asks for the English
        /// page whatever the language.
        /// </summary>
        public async Task<ExophaseAwardPage> GetPageAsync(
            string slug,
            string platformSlug,
            CancellationToken ct,
            bool english = false)
        {
            var language = english ? null : ExophaseGamePageParser.TrophyLanguageCode(_settings?.Persisted?.GlobalLanguage);
            var cacheKey = PageKey(slug, platformSlug, language);
            lock (_sync)
            {
                if (_pages.TryGetValue(cacheKey, out var cached))
                {
                    return cached;
                }
            }

            var url = ExophaseApiClient.BuildUrlFromSlug(slug, platformSlug);
            var page = await _client.FetchAwardPageAsync(
                ExophaseGamePageParser.LocalizeAwardPageUrl(url, language), ct).ConfigureAwait(false);
            var fellBack = false;
            // A localized address that is missing means the English one is too, so only a page
            // that loaded without awards, or failed to load, is asked for again in English.
            if (language != null && !(page?.Achievements?.Count > 0) && page?.IsMissing != true)
            {
                page = await _client.FetchAwardPageAsync(url, ct).ConfigureAwait(false);
                fellBack = true;
            }

            if (page != null && !page.IsMissing && !fellBack)
            {
                page.LanguageCode = language;
            }

            lock (_sync)
            {
                _pages.Set(cacheKey, page);
                if (fellBack || page?.IsMissing == true)
                {
                    // What the language fell back to is the English page, asked for again in
                    // English; a missing page is missing in English as well.
                    _pages.Set(PageKey(slug, platformSlug, null), page);
                }
            }

            return page;
        }

        /// <summary>
        /// True when <paramref name="page"/> is in the plugin's language: its localized copy, or the
        /// English page when the plugin is in English.
        /// </summary>
        public bool IsInPluginLanguage(ExophaseAwardPage page) =>
            page?.LanguageCode != null || EnglishTitlePairing.IsEnglish(_settings?.Persisted?.GlobalLanguage);

        private static string PageKey(string slug, string platformSlug, string language) =>
            slug + "|" + platformSlug + "|" + (language ?? "en");

        /// <summary>
        /// True for a page load that says nothing about whether the page exists: a failed load or
        /// a challenge page, as opposed to a page that loaded and lists no awards.
        /// </summary>
        public static bool IsUnreadable(ExophaseAwardPage page) =>
            page == null || (!(page.Achievements?.Count > 0) && page.IsChallenge);

        /// <summary>
        /// Every page Exophase search offers for <paramref name="name"/> on one of
        /// <paramref name="platformSlugs"/>, the closest title first and, among equal titles, the
        /// earlier platform's, at most <paramref name="max"/>. Titles are not filtered by score:
        /// the caller judges each page by its achievements. Answered is false when the search could
        /// not be read, which is not kept, so the next search asks again.
        /// </summary>
        public async Task<(IReadOnlyList<(string Slug, string Title)> Pages, bool Answered)> SearchCandidatesAsync(
            string name,
            IReadOnlyList<string> platformSlugs,
            int max,
            CancellationToken ct)
        {
            var normalizedName = ExophaseGameNameMatcher.NormalizeGameName(name);
            if (string.IsNullOrWhiteSpace(normalizedName))
            {
                return (Array.Empty<(string, string)>(), true);
            }

            List<ExophaseGame> games;
            lock (_sync)
            {
                _results.TryGetValue(normalizedName, out games);
            }

            if (games == null)
            {
                var (found, answered) = await _client.TrySearchGamesAsync(normalizedName, null, ct).ConfigureAwait(false);
                if (!answered)
                {
                    return (Array.Empty<(string, string)>(), false);
                }

                games = found ?? new List<ExophaseGame>();
                lock (_sync)
                {
                    _results[normalizedName] = games;
                }
            }

            var platforms = (platformSlugs ?? Array.Empty<string>()).Select(slug => slug.ToLowerInvariant()).ToList();
            var pages = games
                .Where(game => game != null && !string.IsNullOrWhiteSpace(game.EndpointAwards))
                .Select(game => (
                    Game: game,
                    Platform: (game.Platforms ?? new List<ExophasePlatform>())
                        .Select(platform => platforms.IndexOf(platform?.Slug?.Trim().ToLowerInvariant() ?? string.Empty))
                        .Where(index => index >= 0)
                        .DefaultIfEmpty(-1)
                        .Min(),
                    Score: ExophaseGameNameMatcher.ComputeMatchScore(normalizedName, ExophaseGameNameMatcher.NormalizeGameName(game.Title))))
                .Where(entry => entry.Platform >= 0)
                .OrderByDescending(entry => entry.Score)
                .ThenBy(entry => entry.Platform)
                .Select(entry => (Slug: ExophaseApiClient.ExtractSlugFromUrl(entry.Game.EndpointAwards), entry.Game.Title))
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Slug))
                .GroupBy(entry => entry.Slug, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .Take(max)
                .ToList();
            return (pages, true);
        }

        /// <summary>
        /// Exophase search's games for a user's query, on one of <paramref name="platformSlugs"/>
        /// (any platform when empty), in Exophase's order. Not cached: each search asks again.
        /// Null when Exophase did not answer.
        /// </summary>
        public async Task<IReadOnlyList<(string Slug, ExophaseGame Game)>> SearchGamesAsync(
            string query,
            IReadOnlyList<string> platformSlugs,
            CancellationToken ct)
        {
            if (_client == null)
            {
                return null;
            }

            var (found, answered) = await _client.TrySearchGamesAsync(query?.Trim(), null, ct).ConfigureAwait(false);
            if (!answered)
            {
                return null;
            }

            var platforms = new HashSet<string>(
                (platformSlugs ?? Array.Empty<string>()).Select(slug => slug.ToLowerInvariant()),
                StringComparer.OrdinalIgnoreCase);
            return (found ?? new List<ExophaseGame>())
                .Where(game => game != null && !string.IsNullOrWhiteSpace(game.EndpointAwards))
                .Where(game => platforms.Count == 0 ||
                               (game.Platforms ?? new List<ExophasePlatform>()).Any(platform => platforms.Contains(platform?.Slug?.Trim() ?? string.Empty)))
                .Select(game => (Slug: ExophaseApiClient.ExtractSlugFromUrl(game.EndpointAwards), Game: game))
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Slug))
                .GroupBy(entry => entry.Slug, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
        }

        /// <summary>
        /// The slug of Exophase's best search match for <paramref name="name"/> on one of
        /// <paramref name="platformSlugs"/>, preferring <paramref name="regionHint"/>'s release; null
        /// when none matches. Answered is false when the search could not be read, which is not
        /// kept, so the next search asks again.
        /// </summary>
        public async Task<(string Slug, bool Answered)> SearchSlugAsync(string name, IReadOnlyList<string> platformSlugs, string regionHint, CancellationToken ct)
        {
            var normalizedName = ExophaseGameNameMatcher.NormalizeGameName(name);
            if (string.IsNullOrWhiteSpace(normalizedName))
            {
                return (null, true);
            }

            var cacheKey = $"{normalizedName}|{string.Join(",", platformSlugs)}|{regionHint}";
            lock (_sync)
            {
                if (_searches.TryGetValue(cacheKey, out var cached))
                {
                    return (cached, true);
                }
            }

            // One search for the name; each platform, newest first, then picks from its results.
            string slug = null;
            var (found, answered) = await _client.TrySearchGamesAsync(normalizedName, null, ct).ConfigureAwait(false);
            if (!answered)
            {
                return (null, false);
            }

            var games = found ?? new List<ExophaseGame>();
            foreach (var platformSlug in platformSlugs)
            {
                var match = ExophaseGameNameMatcher.SelectBestSearchMatch(normalizedName, games, platformSlug, regionHint);
                slug = ExophaseApiClient.ExtractSlugFromUrl(match?.EndpointAwards);
                if (!string.IsNullOrWhiteSpace(slug))
                {
                    break;
                }
            }

            if (string.IsNullOrWhiteSpace(slug) && games.Count > 0)
            {
                _logger?.Debug(
                    $"[ExophaseMetadata] Search for '{normalizedName}' ({string.Join(", ", platformSlugs)}) matched none of {games.Count} results; platforms seen: " +
                    $"[{string.Join(", ", games.SelectMany(row => row?.Platforms ?? Enumerable.Empty<ExophasePlatform>()).Select(platform => platform?.Slug).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase))}].");
            }

            lock (_sync)
            {
                _searches[cacheKey] = slug;
            }

            return (slug, true);
        }
    }
}
