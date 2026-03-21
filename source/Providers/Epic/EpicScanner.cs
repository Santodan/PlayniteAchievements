using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Services;
using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.Epic
{
    internal sealed class EpicScanner
    {
        private readonly PlayniteAchievementsSettings _settings;
        private readonly EpicApiClient _apiClient;
        private readonly EpicSessionManager _sessionManager;
        private readonly ILogger _logger;

        public EpicScanner(
            PlayniteAchievementsSettings settings,
            EpicApiClient apiClient,
            EpicSessionManager sessionManager,
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
            var probeResult = await _sessionManager.ProbeAuthenticationAsync(cancel).ConfigureAwait(false);
            if (!probeResult.IsSuccess)
            {
                _logger?.Warn("[EpicAch] Epic not authenticated - cannot scan achievements.");
                return new RebuildPayload
                {
                    Summary = new RebuildSummary(),
                    AuthRequired = true
                };
            }

            if (gamesToRefresh == null || gamesToRefresh.Count == 0)
            {
                return new RebuildPayload { Summary = new RebuildSummary() };
            }

            var rateLimiter = new RateLimiter(
                _settings.Persisted.ScanDelayMs,
                _settings.Persisted.MaxRetryAttempts);

            return await ProviderRefreshExecutor.RunProviderGamesAsync(
                gamesToRefresh,
                onGameStarting,
                async (game, token) =>
                {
                    var gameId = game?.GameId?.Trim();
                    if (string.IsNullOrWhiteSpace(gameId))
                    {
                        return ProviderRefreshExecutor.ProviderGameResult.Skipped();
                    }

                    var data = await rateLimiter.ExecuteWithRetryAsync(
                        () => FetchGameDataAsync(game, gameId, token),
                        EpicApiClient.IsTransientError,
                        token).ConfigureAwait(false);

                    return new ProviderRefreshExecutor.ProviderGameResult
                    {
                        Data = data
                    };
                },
                onGameCompleted,
                isAuthRequiredException: ex => ex is EpicAuthRequiredException,
                onGameError: (game, ex, consecutiveErrors) =>
                {
                    _logger?.Debug(ex, $"[EpicAch] Failed to scan {game?.Name} after {consecutiveErrors} consecutive errors.");
                },
                delayBetweenGamesAsync: (index, token) => rateLimiter.DelayBeforeNextAsync(token),
                delayAfterErrorAsync: (consecutiveErrors, token) => rateLimiter.DelayAfterErrorAsync(consecutiveErrors, token),
                cancel).ConfigureAwait(false);
        }

        private async Task<GameAchievementData> FetchGameDataAsync(Game game, string gameId, CancellationToken cancel)
        {
            cancel.ThrowIfCancellationRequested();

            var accountId = _sessionManager.GetAccountId();
            if (string.IsNullOrWhiteSpace(accountId))
            {
                throw new EpicAuthRequiredException("Epic account ID not available. Please authenticate.");
            }

            try
            {
                var items = await _apiClient.GetAchievementsAsync(gameId, accountId, cancel).ConfigureAwait(false);
                return MapToGameData(game, gameId, items);
            }
            catch (EpicApiNotAvailableException)
            {
                _logger?.Debug($"[EpicAch] API unavailable for {game?.Name}; using web fallback.");
                return await FetchViaWebFallbackAsync(game, gameId, cancel).ConfigureAwait(false);
            }
        }

        private Task<GameAchievementData> FetchViaWebFallbackAsync(Game game, string gameId, CancellationToken cancel)
        {
            cancel.ThrowIfCancellationRequested();

            _logger?.Warn($"[EpicAch] API unavailable for gameId={gameId}, game={game?.Name}. " +
                          "Skipping refresh to preserve existing cached data.");
            return Task.FromResult<GameAchievementData>(null);
        }

        private static GameAchievementData MapToGameData(Game game, string gameId, List<EpicAchievementItem> items)
        {
            var data = new GameAchievementData
            {
                AppId = 0,
                GameName = game?.Name,
                ProviderKey = "Epic",
                LibrarySourceName = game?.Source?.Name,
                LastUpdatedUtc = DateTime.UtcNow,
                HasAchievements = items != null && items.Count > 0,
                PlayniteGameId = game != null ? game.Id : Guid.Empty,
                Achievements = new List<AchievementDetail>()
            };

            if (items == null)
            {
                return data;
            }

            foreach (var item in items)
            {
                if (item == null || string.IsNullOrWhiteSpace(item.AchievementId))
                {
                    continue;
                }

                data.Achievements.Add(new AchievementDetail
                {
                    ApiName = item.AchievementId,
                    DisplayName = string.IsNullOrWhiteSpace(item.Title) ? item.AchievementId : item.Title,
                    Description = item.Description ?? string.Empty,
                    UnlockedIconPath = item.UnlockedIconUrl ?? string.Empty,
                    LockedIconPath = item.LockedIconUrl,
                    Points = item.XP,
                    Category = null,
                    Hidden = item.Hidden,
                    UnlockTimeUtc = item.UnlockTimeUtc,
                    GlobalPercentUnlocked = item.RarityPercent
                });
            }

            data.HasAchievements = data.Achievements.Count > 0;
            return data;
        }
    }
}
