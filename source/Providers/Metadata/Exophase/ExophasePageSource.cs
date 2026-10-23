using Playnite.SDK;
using PlayniteAchievements.Models;
using PlayniteAchievements.Providers.Exophase;
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
    /// run while any branch has it open; a page two branches read is loaded once.
    /// </summary>
    internal sealed class ExophasePageSource
    {
        private readonly ExophaseApiClient _client;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly ILogger _logger;
        private readonly object _sync = new object();
        private readonly Dictionary<string, ExophaseApiClient.AchievementFetchResult> _pages =
            new Dictionary<string, ExophaseApiClient.AchievementFetchResult>(StringComparer.OrdinalIgnoreCase);
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
        public async Task<ExophaseApiClient.AchievementFetchResult> GetPageAsync(
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
            var page = await _client.FetchAchievementsWithHtmlAsync(
                ExophaseGamePageParser.LocalizeAwardPageUrl(url, language), null, ct).ConfigureAwait(false);
            var fellBack = false;
            if (language != null && !(page?.Achievements?.Count > 0))
            {
                page = await _client.FetchAchievementsWithHtmlAsync(url, null, ct).ConfigureAwait(false);
                fellBack = true;
            }

            lock (_sync)
            {
                _pages[cacheKey] = page;
                if (fellBack)
                {
                    // What the language fell back to is the English page, asked for again in English.
                    _pages[PageKey(slug, platformSlug, null)] = page;
                }
            }

            return page;
        }

        private static string PageKey(string slug, string platformSlug, string language) =>
            slug + "|" + platformSlug + "|" + (language ?? "en");

        /// <summary>
        /// True for a page load that says nothing about whether the page exists: a failed load or
        /// a challenge page, as opposed to a page that loaded and lists no awards.
        /// </summary>
        public static bool IsUnreadable(ExophaseApiClient.AchievementFetchResult page) =>
            page == null ||
            (!(page.Achievements?.Count > 0) &&
             (page.Html?.IndexOf("Just a moment", StringComparison.OrdinalIgnoreCase) >= 0 ||
              page.Html?.IndexOf("cf-chl", StringComparison.OrdinalIgnoreCase) >= 0));

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
        /// The slug of Exophase's best search match for <paramref name="name"/> on one of
        /// <paramref name="platformSlugs"/>, preferring <paramref name="regionHint"/>'s release; null
        /// when none matches.
        /// </summary>
        public async Task<string> SearchSlugAsync(string name, IReadOnlyList<string> platformSlugs, string regionHint, CancellationToken ct)
        {
            var normalizedName = ExophaseGameNameMatcher.NormalizeGameName(name);
            if (string.IsNullOrWhiteSpace(normalizedName))
            {
                return null;
            }

            var cacheKey = $"{normalizedName}|{string.Join(",", platformSlugs)}|{regionHint}";
            lock (_sync)
            {
                if (_searches.TryGetValue(cacheKey, out var cached))
                {
                    return cached;
                }
            }

            // One search for the name; each platform, newest first, then picks from its results.
            string slug = null;
            var games = await _client.SearchGamesAsync(normalizedName, null, ct).ConfigureAwait(false);
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

            return slug;
        }
    }
}
