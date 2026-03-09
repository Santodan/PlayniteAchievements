using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Playnite.SDK;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Providers.Exophase;

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
        private readonly Func<string> _getLanguage;

        public string SourceKey => "Exophase";
        public string SourceName => ResourceProvider.GetString("LOCPlayAch_Provider_Exophase");

        public ExophaseManualSource(
            IPlayniteAPI playniteApi,
            ExophaseSessionManager sessionManager,
            ILogger logger,
            Func<string> getLanguage)
        {
            _apiClient = new ExophaseApiClient(
                playniteApi ?? throw new ArgumentNullException(nameof(playniteApi)),
                logger);
            _sessionManager = sessionManager;
            _logger = logger;
            _getLanguage = getLanguage ?? throw new ArgumentNullException(nameof(getLanguage));
        }

        public async Task<List<ManualGameSearchResult>> SearchGamesAsync(string query, string language, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return new List<ManualGameSearchResult>();
            }

            try
            {
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
                var achievementUrl = ExophaseApiClient.BuildUrlFromSlug(sourceGameId);
                if (string.IsNullOrWhiteSpace(achievementUrl))
                {
                    _logger?.Debug($"Failed to build URL from slug: {sourceGameId}");
                    return null;
                }

                var acceptLanguage = ExophaseApiClient.MapLanguageToAcceptLanguage(language);

                var achievements = await _apiClient.FetchAchievementsAsync(
                    achievementUrl,
                    acceptLanguage,
                    ct).ConfigureAwait(false);

                if (achievements == null || achievements.Count == 0)
                {
                    _logger?.Debug($"No achievements found for Exophase slug: {sourceGameId}");
                    return null;
                }

                // All achievements should have Unlocked=false and UnlockTimeUtc=null
                // The ManualAchievementsProvider will apply stored unlock states
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
    }
}
