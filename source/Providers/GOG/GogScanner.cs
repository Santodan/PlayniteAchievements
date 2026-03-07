using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Services;
using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.GOG
{
    /// <summary>
    /// Scanner for GOG achievements.
    /// Follows SteamScanner pattern with rate limiting and progress reporting.
    /// </summary>
    internal sealed class GogScanner
    {
        private readonly PlayniteAchievementsSettings _settings;
        private readonly GogApiClient _apiClient;
        private readonly GogSessionManager _sessionManager;
        private readonly ILogger _logger;

        public GogScanner(
            PlayniteAchievementsSettings settings,
            GogApiClient apiClient,
            GogSessionManager sessionManager,
            ILogger logger)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
            _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
            _logger = logger;
        }

        public async Task<RebuildPayload> RefreshAsync(
            IReadOnlyList<Game> gamesToRefresh,
            Action<Game> onGameStarting,
            Func<Game, GameAchievementData, Task> onGameCompleted,
            CancellationToken cancel)
        {
            // Ensure auth state is loaded from current GOG web session.
            var probeResult = await _sessionManager.ProbeAuthenticationAsync(cancel).ConfigureAwait(false);
            if (!probeResult.IsSuccess)
            {
                if (probeResult.Outcome == GogAuthOutcome.NotAuthenticated ||
                    probeResult.Outcome == GogAuthOutcome.Cancelled ||
                    probeResult.Outcome == GogAuthOutcome.TimedOut)
                {
                    _logger?.Warn("[GogAch] GOG not authenticated - cannot scan achievements.");
                    return new RebuildPayload
                    {
                        Summary = new RebuildSummary(),
                        AuthRequired = true
                    };
                }
                else
                {
                    _logger?.Warn($"[GogAch] GOG auth probe failed with outcome={probeResult.Outcome}. Scan aborted without auth-required state.");
                }

                return new RebuildPayload { Summary = new RebuildSummary() };
            }

            _logger?.Info("[GogAch] GOG authentication verified.");

            if (gamesToRefresh is null || gamesToRefresh.Count == 0)
            {
                _logger?.Info("[GogAch] No games found to scan.");
                return new RebuildPayload { Summary = new RebuildSummary() };
            }

            // Create rate limiter with exponential backoff
            var rateLimiter = new RateLimiter(
                _settings.Persisted.ScanDelayMs,
                _settings.Persisted.MaxRetryAttempts);

            return await RefreshPipeline.RunProviderGamesAsync(
                gamesToRefresh,
                onGameStarting,
                async (game, token) =>
                {
                    if (!TryGetProductId(game, out var productId))
                    {
                        _logger?.Warn($"[GogAch] Skipping game without valid product ID: {game?.Name}");
                        return RefreshPipeline.ProviderGameResult.Skipped();
                    }

                    var data = await rateLimiter.ExecuteWithRetryAsync(
                        () => FetchGameDataAsync(game, productId, token),
                        GogApiClient.IsTransientError,
                        token).ConfigureAwait(false);

                    return new RefreshPipeline.ProviderGameResult
                    {
                        Data = data
                    };
                },
                onGameCompleted,
                isAuthRequiredException: ex => ex is AuthRequiredException,
                onGameError: (game, ex, consecutiveErrors) =>
                {
                    var productId = TryGetProductId(game, out var pid) ? pid : "?";
                    _logger?.Debug(ex, $"[GogAch] Failed to scan achievements for {game?.Name} (productId={productId}) after {consecutiveErrors} consecutive errors");
                },
                delayBetweenGamesAsync: (index, token) => rateLimiter.DelayBeforeNextAsync(token),
                delayAfterErrorAsync: (consecutiveErrors, token) => rateLimiter.DelayAfterErrorAsync(consecutiveErrors, token),
                cancel).ConfigureAwait(false);
        }

        /// <summary>
        /// Extracts the product ID from a GOG game.
        /// </summary>
        private static bool TryGetProductId(Game game, out string productId)
        {
            productId = null;
            if (game == null || string.IsNullOrWhiteSpace(game.GameId))
                return false;

            // GOG games use product ID as GameId
            productId = game.GameId.Trim();
            return true;
        }

        /// <summary>
        /// Fetches game achievement data from GOG API.
        /// </summary>
        private async Task<GameAchievementData> FetchGameDataAsync(
            Game game,
            string productId,
            CancellationToken cancel)
        {
            cancel.ThrowIfCancellationRequested();

            // First get the client_id from GOGDB
            var clientId = await _apiClient.GetClientIdAsync(productId, cancel).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(clientId))
            {
                _logger?.Warn($"[GogAch] client_id not found for productId={productId}, game={game?.Name}. " +
                              "Skipping refresh to preserve existing cached data.");
                return null;
            }

            // Get user ID from auth client
            var userId = _sessionManager.GetUserId();
            if (string.IsNullOrWhiteSpace(userId))
            {
                _logger?.Warn("[GogAch] No user ID available from auth client.");
                throw new AuthRequiredException("GOG user ID not available. Please re-authenticate.");
            }

            // Fetch achievements
            var achievements = await _apiClient.GetAchievementsAsync(
                clientId,
                userId,
                _settings?.Persisted?.GlobalLanguage,
                cancel).ConfigureAwait(false);

            var gameData = new GameAchievementData
            {
                AppId = int.TryParse(productId, out var pid) ? pid : 0,
                GameName = game.Name,
                ProviderName = "GOG",
                LibrarySourceName = game?.Source?.Name,
                LastUpdatedUtc = DateTime.UtcNow,
                HasAchievements = achievements != null && achievements.Count > 0,
                PlayniteGameId = game.Id,
                Achievements = new List<AchievementDetail>()
            };

            if (!gameData.HasAchievements && achievements != null)
            {
                _logger?.Debug($"[GogAch] No achievements found for productId={productId}, game={game?.Name}");
            }

            if (gameData.HasAchievements)
            {
                foreach (var ach in achievements)
                {
                    var achievementId = ach.ResolvedAchievementId;
                    if (string.IsNullOrWhiteSpace(achievementId))
                        continue;

                    var detail = new AchievementDetail
                    {
                        ApiName = achievementId,
                        DisplayName = !string.IsNullOrWhiteSpace(ach.ResolvedTitle)
                            ? ach.ResolvedTitle
                            : achievementId,
                        Description = ach.ResolvedDescription ?? string.Empty,
                        UnlockedIconPath = ach.ResolvedImageUrlUnlocked ?? string.Empty,
                        LockedIconPath = ach.ResolvedImageUrlLocked,
                        Points = null,
                        Category = null,
                        Hidden = !ach.ResolvedVisible,
                        GlobalPercentUnlocked = ach.ResolvedRarityPercent,
                        UnlockTimeUtc = ach.UnlockTimeUtc
                    };

                    gameData.Achievements.Add(detail);
                }
            }

            return gameData;
        }
    }
}
