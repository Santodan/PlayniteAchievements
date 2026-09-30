using Playnite.SDK;
using Playnite.SDK.Models;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.Refresh;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.Riot
{
    /// <summary>
    /// Runs the Riot refresh. Challenge state is account-wide rather than per-game, so it is
    /// fetched once up front and every capable game is built from the same snapshot.
    /// </summary>
    internal sealed class RiotScanner
    {
        private readonly ILogger _logger;
        private readonly RiotSettings _settings;
        private readonly RiotChallengeMetadataClient _metadataClient;
        private readonly Func<IRiotChallengeSource> _sourceFactory;
        private readonly Func<string> _globalLanguageAccessor;

        public RiotScanner(
            ILogger logger,
            RiotSettings settings,
            RiotChallengeMetadataClient metadataClient,
            Func<IRiotChallengeSource> sourceFactory,
            Func<string> globalLanguageAccessor)
        {
            _logger = logger;
            _settings = settings;
            _metadataClient = metadataClient;
            _sourceFactory = sourceFactory;
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
                _logger?.Warn("[Riot] Riot ID or API key is not configured. Refresh aborted.");
                payload.AuthRequired = true;
                return payload;
            }

            RiotPlayerChallengeState playerState;
            try
            {
                playerState = await _sourceFactory().GetPlayerStateAsync(cancel).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (RiotAuthorizationException ex)
            {
                _logger?.Warn(ex, "[Riot] The Riot API key was rejected. Development keys expire every 24 hours.");
                payload.AuthRequired = true;
                return payload;
            }
            catch (RiotAccountNotFoundException ex)
            {
                _logger?.Warn(ex, $"[Riot] Riot ID '{_settings.RiotId}' did not resolve on region '{_settings.PlatformRegion}'.");
                payload.AuthRequired = true;
                return payload;
            }

            var metadata = await _metadataClient
                .GetChallengesAsync(_globalLanguageAccessor?.Invoke(), cancel)
                .ConfigureAwait(false);

            if (metadata?.Challenges == null || metadata.Challenges.Count == 0)
            {
                // Writing an empty payload would erase the cached challenges, so fault the provider
                // instead and let the run surface the failure with the rest of the cache intact.
                throw new InvalidOperationException(
                    "No Riot challenge definitions are available from CommunityDragon or the local cache.");
            }

            var categoryNames = RiotChallengeCategories.BuildDisplayNames();
            var achievements = RiotChallengeMapper.BuildAchievements(
                metadata,
                playerState,
                categoryNames,
                DateTime.UtcNow);

            _logger?.Info($"[Riot] Built {achievements.Count} challenges for '{_settings.RiotId}'.");

            var categoryArt = RiotChallengeMapper.BuildCategoryArtPlan(metadata, categoryNames);

            return await ProviderRefreshExecutor.RunProviderGamesAsync(
                gamesToRefresh,
                onGameStarting,
                async (game, token) =>
                {
                    var data = BuildGameData(game, playerState, achievements);
                    await DownloadCategoryArtAsync(game?.Id ?? Guid.Empty, categoryArt, token).ConfigureAwait(false);
                    return new ProviderRefreshExecutor.ProviderGameResult { Data = data };
                },
                onGameCompleted,
                isAuthRequiredException: ex => ex is RiotAuthorizationException,
                onGameError: (game, ex, consecutiveErrors) =>
                    _logger?.Warn(ex, $"[Riot] Failed to build challenges for '{game?.Name}'."),
                delayBetweenGamesAsync: null,
                delayAfterErrorAsync: null,
                cancel).ConfigureAwait(false);
        }

        /// <summary>
        /// Downloads each capstone's token art as default category art for the category named after
        /// it. Uses the shared provider-default convention read by CategoryDefaultImageResolver:
        /// existing art is kept and user overrides win over defaults. Best-effort: failures never
        /// fail the refresh, and existing targets are skipped so later refreshes cost nothing.
        /// </summary>
        private async Task DownloadCategoryArtAsync(
            Guid playniteGameId,
            IReadOnlyList<(string Label, string IconUrl)> entries,
            CancellationToken cancel)
        {
            if (playniteGameId == Guid.Empty || entries == null || entries.Count == 0)
            {
                return;
            }

            var diskImageService = PlayniteAchievementsPlugin.Instance?.DiskImageService;
            if (diskImageService == null)
            {
                return;
            }

            var gameIdText = playniteGameId.ToString("D");
            foreach (var entry in entries)
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
                    _logger?.Debug(ex, $"[Riot] Default category image download failed for '{entry.Label}'.");
                }
            }
        }

        /// <summary>
        /// Every capable game gets its own copy of the achievement list, since the refresh pipeline
        /// rewrites icon paths on the objects it is handed.
        /// </summary>
        private GameAchievementData BuildGameData(
            Game game,
            RiotPlayerChallengeState playerState,
            IReadOnlyList<AchievementDetail> achievements)
        {
            return new GameAchievementData
            {
                LastUpdatedUtc = DateTime.UtcNow,
                ProviderKey = "Riot",
                LibrarySourceName = game?.Source?.Name,
                GameName = game?.Name,
                ProviderGameKey = playerState?.PlayerKey,
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
                    Unlocked = achievement.Unlocked,
                    UnlockTimeUtc = achievement.UnlockTimeUtc,
                    Category = achievement.Category,
                    CategoryType = achievement.CategoryType,
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
