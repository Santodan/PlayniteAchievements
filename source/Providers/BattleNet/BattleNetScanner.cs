using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Playnite.SDK;
using Playnite.SDK.Models;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.Refresh;

namespace PlayniteAchievements.Providers.BattleNet
{
    internal sealed class BattleNetScanner
    {
        private readonly Sc2GameStrategy _sc2;
        private readonly WowGameStrategy _wow;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly ILogger _logger;

        public BattleNetScanner(
            BattleNetApiClient client,
            BattleNetSessionManager sessionManager,
            PlayniteAchievementsSettings settings,
            ILogger logger,
            string pluginUserDataPath = null)
        {
            _sc2 = new Sc2GameStrategy(client, logger);
            _wow = new WowGameStrategy(client, sessionManager, logger, pluginUserDataPath);
            _settings = settings;
            _logger = logger;
        }

        public async Task<RebuildPayload> RefreshAsync(
            IReadOnlyList<Game> gamesToRefresh,
            Action<Game> onGameStarting,
            Func<Game, GameAchievementData, Task> onGameCompleted,
            CancellationToken cancel)
        {
            if (gamesToRefresh == null || gamesToRefresh.Count == 0)
            {
                _logger?.Info("[BattleNet] No Battle.net games found to scan.");
                return new RebuildPayload { Summary = new RebuildSummary() };
            }

            var locale = _settings?.Persisted?.GlobalLanguage ?? "en-US";

            _logger?.Info($"[BattleNet] Refresh started. games={gamesToRefresh.Count}, locale={locale}");

            var payload = await ProviderRefreshExecutor.RunProviderGamesAsync(
                gamesToRefresh,
                onGameStarting,
                async (game, token) =>
                {
                    var data = await FetchForGameAsync(game, locale, token).ConfigureAwait(false);
                    return data == null
                        ? ProviderRefreshExecutor.ProviderGameResult.Skipped()
                        : new ProviderRefreshExecutor.ProviderGameResult { Data = data };
                },
                onGameCompleted,
                isAuthRequiredException: _ => false,
                onGameError: (game, ex, consecutiveErrors) => { },
                delayBetweenGamesAsync: (index, token) => Task.CompletedTask,
                delayAfterErrorAsync: (consecutiveErrors, token) => Task.Delay(Math.Min(1000 * consecutiveErrors, 5000), token),
                cancel).ConfigureAwait(false);

            _logger?.Info($"[BattleNet] Refresh completed. refreshed={payload?.Summary?.GamesRefreshed ?? 0}, withAchievements={payload?.Summary?.GamesWithAchievements ?? 0}, withoutAchievements={payload?.Summary?.GamesWithoutAchievements ?? 0}, authRequired={Bool(payload?.AuthRequired ?? false)}");
            return payload;
        }

        private async Task<GameAchievementData> FetchForGameAsync(Game game, string locale, CancellationToken ct)
        {
            // A per-game override forces routing to a specific title strategy, bypassing name matching.
            if (BattleNetGameSupport.TryGetForcedTitle(game, out var forced))
            {
                switch (forced)
                {
                    case BattleNetGameTitle.Wow:
                        return await _wow.FetchAchievementsAsync(game, locale, ct);
                    case BattleNetGameTitle.Sc2:
                        return await _sc2.FetchAchievementsAsync(game, locale, ct);
                    default:
                        return null;
                }
            }

            if (_wow.MatchesGame(game))
            {
                return await _wow.FetchAchievementsAsync(game, locale, ct);
            }

            if (_sc2.MatchesGame(game))
            {
                return await _sc2.FetchAchievementsAsync(game, locale, ct);
            }
            return null;
        }

        private static string Bool(bool value) => value ? "true" : "false";
    }
}
