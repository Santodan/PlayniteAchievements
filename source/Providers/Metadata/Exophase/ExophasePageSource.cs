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
                _searches.Clear();
            }
        }

        /// <summary>
        /// An award page in the plugin's language where Exophase translates it, else English; loaded
        /// once per run. <paramref name="platformSlug"/> picks the page's address (trophies for
        /// PlayStation, challenges for Ubisoft).
        /// </summary>
        public async Task<ExophaseApiClient.AchievementFetchResult> GetPageAsync(string slug, string platformSlug, CancellationToken ct)
        {
            var cacheKey = slug + "|" + platformSlug;
            lock (_sync)
            {
                if (_pages.TryGetValue(cacheKey, out var cached))
                {
                    return cached;
                }
            }

            var language = ExophaseGamePageParser.TrophyLanguageCode(_settings?.Persisted?.GlobalLanguage);
            var url = ExophaseApiClient.BuildUrlFromSlug(slug, platformSlug);
            var page = await _client.FetchAchievementsWithHtmlAsync(
                ExophaseGamePageParser.LocalizeAwardPageUrl(url, language), null, ct).ConfigureAwait(false);
            if (language != null && !(page?.Achievements?.Count > 0))
            {
                page = await _client.FetchAchievementsWithHtmlAsync(url, null, ct).ConfigureAwait(false);
            }

            lock (_sync)
            {
                _pages[cacheKey] = page;
            }

            return page;
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

            string slug = null;
            foreach (var platformSlug in platformSlugs)
            {
                var games = await _client.SearchGamesAsync(normalizedName, platformSlug, ct).ConfigureAwait(false);
                if (games.Count == 0 && !string.IsNullOrWhiteSpace(platformSlug))
                {
                    // The search does not validate its platform filter, so an unknown value finds
                    // nothing; the match below checks each row's platforms itself.
                    games = await _client.SearchGamesAsync(normalizedName, null, ct).ConfigureAwait(false);
                }

                var match = ExophaseGameNameMatcher.SelectBestSearchMatch(normalizedName, games, platformSlug, regionHint);
                slug = ExophaseApiClient.ExtractSlugFromUrl(match?.EndpointAwards);
                if (!string.IsNullOrWhiteSpace(slug))
                {
                    break;
                }

                if (games.Count > 0)
                {
                    _logger?.Debug(
                        $"[ExophaseMetadata] Search for '{normalizedName}' ({platformSlug}) matched none of {games.Count} results; platforms seen: " +
                        $"[{string.Join(", ", games.SelectMany(row => row?.Platforms ?? Enumerable.Empty<ExophasePlatform>()).Select(platform => platform?.Slug).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase))}].");
                }
            }

            lock (_sync)
            {
                _searches[cacheKey] = slug;
            }

            return slug;
        }
    }
}
