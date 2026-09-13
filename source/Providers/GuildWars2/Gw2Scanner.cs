using Playnite.SDK;
using Playnite.SDK.Models;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Services.Refresh;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.GuildWars2
{
    /// <summary>
    /// Runs the Guild Wars 2 refresh. Achievement progress is account-wide rather than per-game, so
    /// it is fetched once up front and every capable game is built from the same snapshot.
    /// </summary>
    internal sealed class Gw2Scanner
    {
        private readonly ILogger _logger;
        private readonly Gw2Settings _settings;
        private readonly Gw2ApiClient _apiClient;
        private readonly Gw2CatalogCache _catalogCache;
        private readonly Func<string> _globalLanguageAccessor;

        public Gw2Scanner(
            ILogger logger,
            Gw2Settings settings,
            Gw2ApiClient apiClient,
            Gw2CatalogCache catalogCache,
            Func<string> globalLanguageAccessor)
        {
            _logger = logger;
            _settings = settings;
            _apiClient = apiClient;
            _catalogCache = catalogCache;
            _globalLanguageAccessor = globalLanguageAccessor;
        }

        public async Task<RebuildPayload> RefreshAsync(
            IReadOnlyList<Game> gamesToRefresh,
            Action<Game> onGameStarting,
            Func<Game, GameAchievementData, Task> onGameCompleted,
            CancellationToken cancel)
        {
            var payload = new RebuildPayload { Summary = new RebuildSummary() };

            if (gamesToRefresh == null || gamesToRefresh.Count == 0)
            {
                return payload;
            }

            if (!_settings.HasCredentials)
            {
                _logger?.Warn("[GW2] No API key is configured. Refresh aborted.");
                payload.AuthRequired = true;
                return payload;
            }

            List<Gw2AccountAchievement> accountAchievements;
            try
            {
                accountAchievements = await _apiClient
                    .GetAccountAchievementsAsync(_settings.ApiKey, cancel)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Gw2AuthorizationException ex)
            {
                _logger?.Warn(ex, "[GW2] The API key was rejected. It may have been revoked, or it may lack the progression scope.");
                payload.AuthRequired = true;
                return payload;
            }

            var language = Gw2Parsing.MapGlobalLanguage(_globalLanguageAccessor?.Invoke());
            var catalog = await _catalogCache
                .GetCatalogAsync(_apiClient, language, null, cancel)
                .ConfigureAwait(false);

            if (catalog == null || !catalog.IsUsable)
            {
                // Writing an empty payload would erase the cached achievements, so fault the provider
                // instead and let the run surface the failure with the rest of the cache intact.
                throw new Gw2ApiException(
                    "No Guild Wars 2 achievement definitions are available from the API or the local cache.");
            }

            var progressIndex = Gw2AchievementMapper.BuildProgressIndex(accountAchievements);
            var achievements = Gw2AchievementMapper.BuildAchievements(catalog, progressIndex);

            _logger?.Info(
                $"[GW2] Built {achievements.Count} achievements from {catalog.Achievements.Count} definitions " +
                $"and {accountAchievements.Count} account entries.");

            return await ProviderRefreshExecutor.RunProviderGamesAsync(
                gamesToRefresh,
                onGameStarting,
                (game, token) => Task.FromResult(new ProviderRefreshExecutor.ProviderGameResult
                {
                    Data = BuildGameData(game, achievements)
                }),
                onGameCompleted,
                isAuthRequiredException: ex => ex is Gw2AuthorizationException,
                onGameError: (game, ex, consecutiveErrors) =>
                    _logger?.Warn(ex, $"[GW2] Failed to build achievements for '{game?.Name}'."),
                delayBetweenGamesAsync: null,
                delayAfterErrorAsync: null,
                cancel).ConfigureAwait(false);
        }

        /// <summary>
        /// Every capable game gets its own copy of the achievement list, since the refresh pipeline
        /// rewrites icon paths on the objects it is handed.
        /// </summary>
        private GameAchievementData BuildGameData(Game game, IReadOnlyList<AchievementDetail> achievements)
        {
            return new GameAchievementData
            {
                LastUpdatedUtc = DateTime.UtcNow,
                ProviderKey = "GW2",
                LibrarySourceName = game?.Source?.Name,
                GameName = game?.Name,
                ProviderGameKey = _settings.AccountId,
                PlayniteGameId = game?.Id ?? Guid.Empty,
                HasAchievements = achievements.Count > 0,
                Achievements = CloneAchievements(achievements)
            };
        }

        private static List<AchievementDetail> CloneAchievements(IReadOnlyList<AchievementDetail> source)
        {
            var copies = new List<AchievementDetail>(source.Count);
            foreach (var achievement in source)
            {
                copies.Add(new AchievementDetail
                {
                    ApiName = achievement.ApiName,
                    DisplayName = achievement.DisplayName,
                    Description = achievement.Description,
                    UnlockedIconPath = achievement.UnlockedIconPath,
                    LockedIconPath = achievement.LockedIconPath,
                    Points = achievement.Points,
                    Unlocked = achievement.Unlocked,
                    UnlockTimeUtc = achievement.UnlockTimeUtc,
                    Category = achievement.Category,
                    CategoryType = achievement.CategoryType,
                    Hidden = achievement.Hidden,
                    GlobalPercentUnlocked = achievement.GlobalPercentUnlocked,
                    Rarity = achievement.Rarity,
                    ProgressNum = achievement.ProgressNum,
                    ProgressDenom = achievement.ProgressDenom
                });
            }

            return copies;
        }
    }
}
