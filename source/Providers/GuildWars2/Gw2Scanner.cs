using Playnite.SDK;
using Playnite.SDK.Models;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.Refresh;
using System;
using System.Collections.Generic;
using System.Linq;
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

        private readonly object _progressLock = new object();

        /// <summary>
        /// Account progress as of the last refresh that actually wrote rows, and the games it wrote
        /// them for. Together they let a later refresh recognize that it would rewrite byte-identical
        /// data and decline to do it.
        /// </summary>
        private Dictionary<int, Gw2ProgressSignature> _deliveredProgress;

        private readonly HashSet<Guid> _deliveredGames = new HashSet<Guid>();

        private readonly Gw2LiveProgressState _liveProgress;

        public Gw2Scanner(
            ILogger logger,
            Gw2Settings settings,
            Gw2ApiClient apiClient,
            Gw2CatalogCache catalogCache,
            Func<string> globalLanguageAccessor,
            Gw2LiveProgressState liveProgress = null)
        {
            _logger = logger;
            _settings = settings;
            _apiClient = apiClient;
            _catalogCache = catalogCache;
            _globalLanguageAccessor = globalLanguageAccessor;
            _liveProgress = liveProgress;
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

            var snapshot = Gw2ProgressSnapshot.Build(accountAchievements);

            bool progressUnchanged;
            lock (_progressLock)
            {
                progressUnchanged = Gw2ProgressSnapshot.AreEquivalent(_deliveredProgress, snapshot);
            }

            // The catalog is only needed to build rows. When every target game is going to decline
            // the write, skipping the lookup also skips its build-id request, so a poll of a running
            // game costs exactly one call to the account endpoint.
            var everyGameSkips = gamesToRefresh.All(game => ShouldSkipUnchanged(game, snapshot, progressUnchanged));

            Gw2Catalog catalog = null;
            if (!everyGameSkips)
            {
                var language = Gw2Parsing.MapGlobalLanguage(_globalLanguageAccessor?.Invoke());
                catalog = await _catalogCache
                    .GetCatalogAsync(_apiClient, language, null, cancel)
                    .ConfigureAwait(false);

                if (catalog == null || !catalog.IsUsable)
                {
                    // Writing an empty payload would erase the cached achievements, so fault the
                    // provider instead and let the run surface the failure with the cache intact.
                    throw new Gw2ApiException(
                        "No Guild Wars 2 achievement definitions are available from the API or the local cache.");
                }
            }

            var progressIndex = Gw2AchievementMapper.BuildProgressIndex(accountAchievements);

            // Expanding the catalog into tier rows is the expensive half of a Guild Wars 2 refresh -
            // around 13,000 of them - so it is deferred until a game is known to need it. When the
            // in-game monitor polls a running game every few seconds and nothing has moved, it never
            // runs at all.
            var achievements = new Lazy<List<AchievementDetail>>(
                () =>
                {
                    var built = Gw2AchievementMapper.BuildAchievements(catalog, progressIndex);
                    _logger?.Info(
                        $"[GW2] Built {built.Count} achievements from {catalog.Achievements.Count} definitions " +
                        $"and {accountAchievements.Count} account entries.");
                    return built;
                },
                LazyThreadSafetyMode.ExecutionAndPublication);

            var result = await ProviderRefreshExecutor.RunProviderGamesAsync(
                gamesToRefresh,
                onGameStarting,
                async (game, token) =>
                {
                    if (ShouldSkipUnchanged(game, snapshot, progressUnchanged))
                    {
                        return ProviderRefreshExecutor.ProviderGameResult.Skipped();
                    }

                    var data = BuildGameData(game, achievements.Value);

                    lock (_progressLock)
                    {
                        _deliveredGames.Add(game.Id);
                    }

                    await DownloadCategoryArtAsync(game.Id, catalog, token).ConfigureAwait(false);
                    return new ProviderRefreshExecutor.ProviderGameResult { Data = data };
                },
                onGameCompleted,
                isAuthRequiredException: ex => ex is Gw2AuthorizationException,
                onGameError: (game, ex, consecutiveErrors) =>
                    _logger?.Warn(ex, $"[GW2] Failed to build achievements for '{game?.Name}'."),
                delayBetweenGamesAsync: null,
                delayAfterErrorAsync: null,
                cancel).ConfigureAwait(false);

            lock (_progressLock)
            {
                _deliveredProgress = snapshot;
            }

            return result;
        }

        /// <summary>
        /// Whether this refresh can decline to rewrite every row for a game. Two situations qualify,
        /// both only while the game is running:
        ///
        /// The account has not moved since the last write, so a rebuild would produce identical rows.
        ///
        /// Or it has moved, but the live in-game reader has already pushed exactly this progress into
        /// the cache. The monitor runs a full provider refresh every few seconds for as long as a game
        /// is open - its guaranteed floor - and for a game with this many rows, rebuilding all of them
        /// to reapply a change that has already been applied is the whole cost of playing.
        ///
        /// The running check is what keeps this safe. A refresh the user asked for, with the game
        /// closed, always rebuilds, so clearing the cache and refreshing still repopulates it, and the
        /// rebuild at game close reconciles anything the live reader missed.
        /// </summary>
        private bool ShouldSkipUnchanged(
            Game game,
            Dictionary<int, Gw2ProgressSignature> snapshot,
            bool progressUnchanged)
        {
            if (game == null || game.Id == Guid.Empty || !game.IsRunning)
            {
                return false;
            }

            lock (_progressLock)
            {
                if (!_deliveredGames.Contains(game.Id))
                {
                    return false;
                }
            }

            return progressUnchanged || _liveProgress?.HasApplied(snapshot) == true;
        }

        /// <summary>
        /// Every capable game gets its own copy of the achievement list, since the refresh pipeline
        /// rewrites icon paths on the objects it is handed.
        /// </summary>
        /// <summary>
        /// Downloads each category's icon as default category art for its "Group / Category" path.
        /// Uses the shared provider-default convention read by CategoryDefaultImageResolver:
        /// existing art is kept and user overrides win over defaults. Best-effort: failures never
        /// fail the refresh, and existing targets are skipped so later refreshes cost nothing.
        /// </summary>
        private async Task DownloadCategoryArtAsync(Guid playniteGameId, Gw2Catalog catalog, CancellationToken cancel)
        {
            if (playniteGameId == Guid.Empty)
            {
                return;
            }

            var diskImageService = PlayniteAchievementsPlugin.Instance?.DiskImageService;
            if (diskImageService == null)
            {
                return;
            }

            var gameIdText = playniteGameId.ToString("D");
            foreach (var entry in Gw2AchievementMapper.BuildCategoryArtPlan(catalog))
            {
                cancel.ThrowIfCancellationRequested();
                var label = AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(entry.Label);
                if (string.Equals(label, AchievementCategoryTypeHelper.DefaultCategoryLabel, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    var artTarget = diskImageService.GetDefaultCategoryImagePath(gameIdText, label);
                    // decodeSize 0 stores the original bytes: no square crop, original aspect.
                    await diskImageService.GetOrDownloadIconToPathAsync(entry.IconUrl, artTarget, decodeSize: 0, cancel)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancel.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger?.Debug(ex, $"[GW2] Default category image download failed for '{entry.Label}'.");
                }
            }
        }

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
