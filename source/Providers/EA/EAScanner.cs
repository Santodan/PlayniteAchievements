using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Providers.Exophase;
using PlayniteAchievements.Services;
using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.EA
{
    internal sealed class EAScanner
    {
        private readonly PlayniteAchievementsSettings _settings;
        private readonly EASettings _providerSettings;
        private readonly EAApiClient _apiClient;
        private readonly EASessionManager _sessionManager;
        private readonly ILogger _logger;
        private readonly IPlayniteAPI _playniteApi;
        private readonly string _pluginUserDataPath;

        public EAScanner(
            PlayniteAchievementsSettings settings,
            EASettings providerSettings,
            EAApiClient apiClient,
            EASessionManager sessionManager,
            ILogger logger,
            IPlayniteAPI playniteApi,
            string pluginUserDataPath)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _providerSettings = providerSettings ?? throw new ArgumentNullException(nameof(providerSettings));
            _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
            _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
            _logger = logger;
            _playniteApi = playniteApi;
            _pluginUserDataPath = pluginUserDataPath;
        }

        public async Task<RebuildPayload> RefreshAsync(
            IReadOnlyList<Game> gamesToRefresh,
            Action<Game> onGameStarting,
            Func<Game, GameAchievementData, Task> onGameCompleted,
            CancellationToken cancel)
        {
            var probeResult = await _sessionManager.ProbeAuthStateAsync(cancel).ConfigureAwait(false);
            if (!probeResult.IsSuccess)
            {
                _logger?.Warn("[EAAch] EA not authenticated - cannot scan achievements.");
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
            var metadataEnricher = await CreateMetadataEnricherAsync(cancel).ConfigureAwait(false);

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
                        EAApiClient.IsTransientError,
                        token).ConfigureAwait(false);

                    await EnrichMetadataAsync(game, data, metadataEnricher, token).ConfigureAwait(false);

                    return new ProviderRefreshExecutor.ProviderGameResult
                    {
                        Data = data
                    };
                },
                onGameCompleted,
                isAuthRequiredException: ex => ex is EaAuthRequiredException,
                onGameError: (game, ex, consecutiveErrors) =>
                {
                    _logger?.Debug(ex, $"[EAAch] Failed to scan {game?.Name} after {consecutiveErrors} consecutive errors.");
                },
                delayBetweenGamesAsync: (index, token) => rateLimiter.DelayBeforeNextAsync(token),
                delayAfterErrorAsync: (consecutiveErrors, token) => rateLimiter.DelayAfterErrorAsync(consecutiveErrors, token),
                cancel).ConfigureAwait(false);
        }

        private async Task<GameAchievementData> FetchGameDataAsync(Game game, string gameId, CancellationToken cancel)
        {
            cancel.ThrowIfCancellationRequested();

            var playerPsd = _sessionManager.GetPlayerSubId();
            if (string.IsNullOrWhiteSpace(playerPsd))
            {
                throw new EaAuthRequiredException("EA player sub ID not available. Please authenticate.");
            }

            var ownedGames = await _apiClient.GetOwnedGamesAsync(cancel).ConfigureAwait(false);
            var matched = EAProviderSupport.MatchGame(ownedGames, game, gameId);
            var offerIdCandidates = EAProviderSupport.BuildOfferIdCandidates(matched?.OriginOfferId, gameId);

            if (offerIdCandidates.Count == 0)
            {
                _logger?.Debug($"[EAAch] No EA owned game matched and could not build offer ID candidates from gameId={gameId}, game={game?.Name}.");
                return null;
            }

            if (matched == null)
            {
                _logger?.Debug($"[EAAch] Owned game lookup had no match for gameId={gameId}. Falling back to offer IDs: {string.Join(", ", offerIdCandidates)}.");
            }

            List<EaAchievementItem> items = null;
            string lastOfferId = null;

            foreach (var offerId in offerIdCandidates)
            {
                lastOfferId = offerId;

                var candidateItems = await _apiClient.GetAchievementsAsync(
                    offerId, playerPsd, cancel).ConfigureAwait(false);

                if (candidateItems.Count > 0)
                {
                    items = candidateItems;
                    _logger?.Debug($"[EAAch] Found {candidateItems.Count} achievements for gameId={gameId} using offer ID={offerId}.");
                    break;
                }

                items = candidateItems;
                _logger?.Debug($"[EAAch] No achievements returned for gameId={gameId} using offer ID={offerId}.");
            }

            if (items == null || items.Count == 0)
            {
                _logger?.Debug($"[EAAch] No achievements returned for gameId={gameId} after trying {offerIdCandidates.Count} offer ID candidate(s). Last offer ID={lastOfferId}.");
            }

            return EAProviderSupport.MapToGameData(game, items);
        }

        private async Task<ExophaseMetadataEnricher> CreateMetadataEnricherAsync(CancellationToken cancel)
        {
            if (_providerSettings?.UseExophaseForRarity != true)
            {
                return null;
            }

            var enricher = new ExophaseMetadataEnricher(_playniteApi, _logger, _settings, _pluginUserDataPath);
            await enricher.InitializeAsync(cancel).ConfigureAwait(false);
            return enricher;
        }

        private static async Task EnrichMetadataAsync(
            Game game,
            GameAchievementData data,
            ExophaseMetadataEnricher metadataEnricher,
            CancellationToken cancel)
        {
            if (metadataEnricher == null || data?.Achievements == null || data.Achievements.Count == 0)
            {
                return;
            }

            await metadataEnricher.EnrichAsync(
                game,
                data.Achievements,
                "origin",
                "EA",
                cancel,
                ExophaseMetadataFields.Rarity | ExophaseMetadataFields.IconPaths).ConfigureAwait(false);
        }
    }
}
