using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Playnite.SDK;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Providers.Exophase;
using PlayniteAchievements.Providers;

namespace PlayniteAchievements.Providers.Manual
{
    /// <summary>
    /// Manual source implementation for Exophase.
    /// Uses WebView for all requests to bypass Cloudflare protection.
    /// </summary>
    internal sealed class ExophaseManualSource : IManualSource
    {
        private readonly ExophaseApiClient _apiClient;
        private readonly ExophaseSessionManager _sessionManager;
        private readonly ILogger _logger;

        public string SourceKey => "Exophase";
        public string SourceName => ResourceProvider.GetString("LOCPlayAch_Provider_Exophase");
        public bool IsAuthenticated => _sessionManager?.IsAuthenticated ?? false;
        public ISessionManager AuthSession => _sessionManager;

        public ExophaseManualSource(
            IPlayniteAPI playniteApi,
            ExophaseSessionManager sessionManager,
            ILogger logger,
            Func<string> getLanguage)
        {
            _apiClient = new ExophaseApiClient(
                playniteApi ?? throw new ArgumentNullException(nameof(playniteApi)),
                logger);
            _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
            _logger = logger;
            _ = getLanguage ?? throw new ArgumentNullException(nameof(getLanguage));
        }

        public async Task<List<ManualGameSearchResult>> SearchGamesAsync(string query, string language, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return new List<ManualGameSearchResult>();
            }

            try
            {
                await ManualSourceAuthentication.EnsureAuthenticatedAsync(this, ct).ConfigureAwait(false);

                var games = await _apiClient.SearchGamesAsync(query, ct).ConfigureAwait(false);
                if (games == null || games.Count == 0)
                {
                    return new List<ManualGameSearchResult>();
                }

                var results = new List<ManualGameSearchResult>(games.Count);
                foreach (var game in games)
                {
                    if (game == null || string.IsNullOrWhiteSpace(game.EndpointAwards))
                    {
                        _logger?.Debug("[ExophaseManualSource] Skipping game: null or no EndpointAwards");
                        continue;
                    }

                    // Extract the slug from the achievement URL for a stable identifier
                    var slug = ExophaseApiClient.ExtractSlugFromUrl(game.EndpointAwards);
                    if (string.IsNullOrWhiteSpace(slug))
                    {
                        _logger?.Debug($"[ExophaseManualSource] Skipping game '{game.Title}': could not extract slug from {game.EndpointAwards}");
                        continue;
                    }

                    // Get the icon URL - prefer O (original), then L (large), then M (medium)
                    var iconUrl = game.Images?.O ?? game.Images?.L ?? game.Images?.M;

                    // Build platform display string
                    var platformNames = game.Platforms != null && game.Platforms.Count > 0
                        ? string.Join(", ", game.Platforms.ConvertAll(p => p?.Name).FindAll(n => !string.IsNullOrWhiteSpace(n)))
                        : "";

                    _logger?.Debug($"[ExophaseManualSource] Adding result: {game.Title} | Platforms: {platformNames} | Slug: {slug}");

                    results.Add(new ManualGameSearchResult
                    {
                        SourceGameId = slug,
                        Name = game.Title ?? "Unknown Game",
                        IconUrl = iconUrl ?? string.Empty,
                        HasAchievements = true, // All Exophase games have achievements
                        Platforms = platformNames
                    });
                }

                _logger?.Debug($"[ExophaseManualSource] Returning {results.Count} search results");
                return results;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Exophase search failed");
                return new List<ManualGameSearchResult>();
            }
        }

        public async Task<List<AchievementDetail>> GetAchievementsAsync(string sourceGameId, string language, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(sourceGameId))
            {
                return null;
            }

            // sourceGameId is the game slug (e.g., "shogun-showdown-steam")
            // Build the full URL from the slug
            try
            {
                await ManualSourceAuthentication.EnsureAuthenticatedAsync(this, ct).ConfigureAwait(false);

                var achievementUrl = ExophaseApiClient.BuildUrlFromSlug(sourceGameId);
                if (string.IsNullOrWhiteSpace(achievementUrl))
                {
                    _logger?.Debug($"Failed to build URL from slug: {sourceGameId}");
                    return null;
                }

                var acceptLanguage = ExophaseApiClient.MapLanguageToAcceptLanguage(language);

                List<AchievementDetail> achievements;
                using (var httpClient = CreateAuthenticatedHttpClient())
                {
                    achievements = await _apiClient.FetchAchievementsViaHttpAsync(
                        achievementUrl,
                        acceptLanguage,
                        httpClient,
                        ct).ConfigureAwait(false);
                }

                if (achievements == null || achievements.Count == 0)
                {
                    _logger?.Debug($"No achievements found for Exophase slug: {sourceGameId}");
                    return null;
                }

                // Manual linking uses Exophase only as a schema/text source.
                // ManualAchievementsProvider applies stored unlock state and manual-provider rarity rules later.
                foreach (var achievement in achievements)
                {
                    if (achievement == null)
                    {
                        continue;
                    }

                    achievement.Unlocked = false;
                    achievement.UnlockTimeUtc = null;
                }

                return achievements;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed to fetch Exophase achievements from slug: {sourceGameId}");
                return null;
            }
        }

        private HttpClient CreateAuthenticatedHttpClient()
        {
            var cookieJar = new CookieContainer();
            _sessionManager.LoadCefCookiesIntoJar(cookieJar);

            var handler = new HttpClientHandler
            {
                CookieContainer = cookieJar,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                AllowAutoRedirect = true,
                UseCookies = true
            };

            var httpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(30)
            };
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            return httpClient;
        }

    }
}
