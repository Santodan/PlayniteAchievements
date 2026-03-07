using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Playnite.SDK;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Providers.Exophase;

namespace PlayniteAchievements.Providers.Manual
{
    /// <summary>
    /// Manual source implementation for Exophase.
    /// Uses Exophase public API for search and HTML parsing for achievement pages.
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
            HttpClient httpClient,
            IPlayniteAPI playniteApi,
            ExophaseSessionManager sessionManager,
            ILogger logger,
            Func<string> getLanguage)
        {
            _apiClient = new ExophaseApiClient(
                httpClient ?? throw new ArgumentNullException(nameof(httpClient)),
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
                        continue;
                    }

                    // Get the icon URL - prefer cover, then banner
                    var iconUrl = game.Images?.Cover;
                    if (string.IsNullOrWhiteSpace(iconUrl))
                    {
                        iconUrl = game.Images?.Banner;
                    }

                    // Build platform display string
                    var platformNames = game.Platforms != null && game.Platforms.Count > 0
                        ? string.Join(", ", game.Platforms.ConvertAll(p => p?.Name).FindAll(n => !string.IsNullOrWhiteSpace(n)))
                        : "";

                    results.Add(new ManualGameSearchResult
                    {
                        SourceGameId = game.EndpointAwards,
                        Name = string.IsNullOrWhiteSpace(platformNames)
                            ? game.Title ?? "Unknown Game"
                            : $"{game.Title} ({platformNames})",
                        IconUrl = iconUrl ?? string.Empty,
                        HasAchievements = true // All Exophase games have achievements
                    });
                }

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

            // sourceGameId is the achievement page URL (endpoint_awards value)
            // It should be a URL like: https://www.exophase.com/game/.../achievements
            try
            {
                var acceptLanguage = ExophaseApiClient.MapLanguageToAcceptLanguage(language);

                var achievements = await _apiClient.FetchAchievementsAsync(
                    sourceGameId,
                    acceptLanguage,
                    ct).ConfigureAwait(false);

                if (achievements == null || achievements.Count == 0)
                {
                    _logger?.Debug($"No achievements found for Exophase URL: {sourceGameId}");
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
                _logger?.Error(ex, $"Failed to fetch Exophase achievements from {sourceGameId}");
                return null;
            }
        }
    }
}
