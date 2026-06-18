using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Playnite.SDK;
using Playnite.SDK.Models;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Providers.Exophase;
using PlayniteAchievements.Services;

namespace PlayniteAchievements.Providers.BattleNet
{
    internal sealed class BattleNetScanner
    {
        private readonly Sc2GameStrategy _sc2;
        private readonly WowGameStrategy _wow;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly BattleNetSettings _providerSettings;
        private readonly ILogger _logger;
        private readonly IPlayniteAPI _playniteApi;
        private readonly string _pluginUserDataPath;

        public BattleNetScanner(
            BattleNetApiClient client,
            PlayniteAchievementsSettings settings,
            ILogger logger)
            : this(client, settings, ProviderRegistry.Settings<BattleNetSettings>(), logger, null, null)
        {
        }

        public BattleNetScanner(
            BattleNetApiClient client,
            PlayniteAchievementsSettings settings,
            BattleNetSettings providerSettings,
            ILogger logger,
            IPlayniteAPI playniteApi,
            string pluginUserDataPath)
        {
            _sc2 = new Sc2GameStrategy(client, logger);
            _wow = new WowGameStrategy(client, logger);
            _settings = settings;
            _providerSettings = providerSettings;
            _logger = logger;
            _playniteApi = playniteApi;
            _pluginUserDataPath = pluginUserDataPath ?? string.Empty;
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

            var rarityEnricher = await CreateRarityEnricherAsync(cancel).ConfigureAwait(false);

            var payload = await ProviderRefreshExecutor.RunProviderGamesAsync(
                gamesToRefresh,
                onGameStarting,
                async (game, token) =>
                {
                    var data = await FetchForGameAsync(game, locale, token).ConfigureAwait(false);
                    await EnrichRarityAsync(game, data, rarityEnricher, locale, token).ConfigureAwait(false);
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

        private async Task<ExophaseMetadataEnricher> CreateRarityEnricherAsync(CancellationToken cancel)
        {
            if (_providerSettings?.UseExophaseForRarity != true)
            {
                return null;
            }

            var enricher = new ExophaseMetadataEnricher(_playniteApi, _logger, _settings, _pluginUserDataPath);
            await enricher.InitializeAsync(cancel).ConfigureAwait(false);
            return enricher;
        }

        private async Task EnrichRarityAsync(
            Game game,
            GameAchievementData data,
            ExophaseMetadataEnricher rarityEnricher,
            string locale,
            CancellationToken cancel)
        {
            if (rarityEnricher == null || data?.Achievements == null || data.Achievements.Count == 0)
            {
                return;
            }

            if (_wow.MatchesGame(game) && WowGameStrategy.RequiresEnglishMetadataProjection(locale))
            {
                var projection = await TryCreateWowEnglishMetadataProjectionAsync(game, data.Achievements, cancel)
                    .ConfigureAwait(false);

                if (projection != null && projection.Count > 0)
                {
                    await rarityEnricher.EnrichAsync(
                        game,
                        projection,
                        "blizzard",
                        "BattleNet",
                        cancel,
                        ExophaseMetadataFields.Rarity).ConfigureAwait(false);

                    var copied = WowGameStrategy.ApplyProjectedRarity(data.Achievements, projection);
                    _logger?.Info($"[BattleNet/WoW] Copied Exophase rarity from English metadata projection to {copied}/{data.Achievements.Count} localized achievements.");
                    return;
                }
            }

            await rarityEnricher.EnrichAsync(
                game,
                data.Achievements,
                "blizzard",
                "BattleNet",
                cancel,
                ExophaseMetadataFields.Rarity).ConfigureAwait(false);
        }

        private async Task<List<AchievementDetail>> TryCreateWowEnglishMetadataProjectionAsync(
            Game game,
            IList<AchievementDetail> nativeAchievements,
            CancellationToken cancel)
        {
            try
            {
                var englishData = await _wow
                    .FetchAchievementsAsync(game, WowGameStrategy.EnglishMetadataLocale, cancel)
                    .ConfigureAwait(false);
                if (englishData?.Achievements == null || englishData.Achievements.Count == 0)
                {
                    _logger?.Warn($"[BattleNet/WoW] English metadata projection unavailable for '{game?.Name}'. Falling back to localized Exophase title matching.");
                    return null;
                }

                return WowGameStrategy.CreateEnglishMetadataProjection(
                    nativeAchievements,
                    englishData.Achievements);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"[BattleNet/WoW] Failed to build English metadata projection for '{game?.Name}'. Falling back to localized Exophase title matching.");
                return null;
            }
        }

        private static string Bool(bool value) => value ? "true" : "false";

        private static string GameLabel(Game game)
        {
            if (game == null)
            {
                return "<null>";
            }

            return $"{game.Name ?? "<unnamed>"} ({game.Id})";
        }
    }
}
