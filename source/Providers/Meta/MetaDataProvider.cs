using Playnite.SDK;
using Playnite.SDK.Models;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Providers.Overrides;
using PlayniteAchievements.Providers.Settings;
using PlayniteAchievements.Services.GameCustomData;
using PlayniteAchievements.Services.Refresh;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.Meta
{
    /// <summary>
    /// Data provider for Meta Quest and Oculus Rift achievements. Claims games imported by the Meta
    /// Quest/Oculus Library plugin (whose GameId is the Meta app id) and games given a manual Meta app
    /// id override. Each refresh reads the user's unlock feed once, then joins it against every game's
    /// achievement definitions by definition id.
    /// </summary>
    internal sealed class MetaDataProvider : DataProviderBase<MetaSettings>, IDataProvider, IProviderOverride, IEnglishTitleSource, IDisposable
    {
        // Meta Quest/Oculus Library plugin (Jeshibu). Rift and Quest games both carry the Meta app id
        // as Game.GameId.
        private static readonly Guid MetaLibraryPluginId = new Guid("77346DD6-B0CC-4F7D-80F0-C1D138CCAE58");

        public ProviderOverrideDescriptor OverrideDescriptor { get; } = ProviderOverrideDescriptor.Text(
            "LOCPlayAch_ManageAchievements_Overrides_ProviderValueLabel_Meta",
            raw =>
            {
                if (long.TryParse((raw ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var appId) &&
                    appId > 0)
                {
                    return ProviderOverrideValidation.Valid(appId.ToString(CultureInfo.InvariantCulture));
                }

                return ProviderOverrideValidation.Invalid(ProviderOverrideValidators.RequiredValueErrorKey);
            });

        private readonly ILogger _logger;
        private readonly MetaApiClient _apiClient;
        private readonly MetaSessionManager _sessionManager;

        public MetaDataProvider(ILogger logger, IPlayniteAPI playniteApi)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _apiClient = new MetaApiClient(logger);
            _sessionManager = new MetaSessionManager(playniteApi, logger, _apiClient);
        }

        public string ProviderName => ResourceProvider.GetString("LOCPlayAch_Provider_Meta");
        public string ProviderKey => "Meta";
        public string ProviderIconKey => "ProviderIconMeta";
        public string ProviderColorHex => "#0081FB";

        public bool IsAuthenticated => _sessionManager.IsAuthenticated;

        public ISessionManager AuthSession => _sessionManager;

        public PlayniteAchievements.Models.Friends.IFriendsProvider Friends => null;

        public bool IsCapable(Game game)
        {
            if (game == null || game.Id == Guid.Empty || !ProviderSettings.IsEnabled)
            {
                return false;
            }

            return game.PluginId == MetaLibraryPluginId ||
                   GameCustomDataLookup.TryGetProviderOverrideValue(game.Id, ProviderKey, out _);
        }

        /// <summary>The refresh's own rows: definitions are requested without a locale, which Meta serves in English.</summary>
        Task<IList<AchievementDetail>> IEnglishTitleSource.GetEnglishTitlesAsync(Game game, GameAchievementData data, CancellationToken ct)
        {
            return Task.FromResult<IList<AchievementDetail>>(data?.Achievements?
                .Where(achievement => achievement != null)
                .Select(achievement => new AchievementDetail
                {
                    ApiName = achievement.ApiName,
                    DisplayName = achievement.DisplayName,
                    Description = achievement.Description
                })
                .ToList());
        }

        public async Task<RebuildPayload> RefreshAsync(
            IReadOnlyList<Game> gamesToRefresh,
            Action<Game> onGameStarting,
            Func<Game, GameAchievementData, Task> onGameCompleted,
            CancellationToken cancel)
        {
            if (gamesToRefresh == null || gamesToRefresh.Count == 0)
            {
                return new RebuildPayload { Summary = new RebuildSummary() };
            }

            var userId = _sessionManager.UserId?.Trim();
            var token = string.IsNullOrEmpty(userId)
                ? null
                : await _sessionManager.GetAccessTokenAsync(cancel).ConfigureAwait(false);
            if (string.IsNullOrEmpty(token))
            {
                _logger.Warn("[Meta] Not signed in at refresh start. Refresh aborted.");
                return new RebuildPayload { Summary = new RebuildSummary(), AuthRequired = true };
            }

            Dictionary<string, MetaUnlock> unlocks;
            try
            {
                unlocks = MetaParsing.IndexUnlocks(
                    await _apiClient.GetUnlocksAsync(token, userId, cancel).ConfigureAwait(false));
            }
            catch (MetaAuthException ex)
            {
                _logger.Warn($"[Meta] {ex.Message}");
                return new RebuildPayload { Summary = new RebuildSummary(), AuthRequired = true };
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Without the feed every achievement would be written as locked; skip the run instead.
                _logger.Warn(ex, "[Meta] Unlock feed could not be read; skipping this run.");
                return new RebuildPayload { Summary = new RebuildSummary() };
            }

            return await ProviderRefreshExecutor.RunProviderGamesAsync(
                gamesToRefresh,
                onGameStarting,
                async (game, ct) =>
                {
                    if (!IsCapable(game))
                    {
                        return ProviderRefreshExecutor.ProviderGameResult.Skipped();
                    }

                    var appId = ResolveAppId(game);
                    if (string.IsNullOrEmpty(appId))
                    {
                        _logger.Warn($"[Meta] Could not resolve a Meta app id for '{game.Name}'.");
                        return ProviderRefreshExecutor.ProviderGameResult.Skipped();
                    }

                    var definitions = await _apiClient.GetDefinitionsAsync(token, appId, ct).ConfigureAwait(false);
                    var achievements = MetaParsing.MapAchievements(definitions, unlocks);
                    return new ProviderRefreshExecutor.ProviderGameResult
                    {
                        Data = CreateGameResult(game, appId, achievements)
                    };
                },
                onGameCompleted,
                isAuthRequiredException: ex => ex is MetaAuthException,
                onGameError: (game, ex, consecutiveErrors) =>
                {
                    _logger.Warn(ex, $"[Meta] Failed to refresh '{game?.Name}' after {consecutiveErrors} consecutive errors.");
                },
                delayBetweenGamesAsync: null,
                delayAfterErrorAsync: null,
                cancel).ConfigureAwait(false);
        }

        /// <summary>
        /// A manual per-game override wins; otherwise the library plugin's GameId, which is the app id.
        /// </summary>
        private string ResolveAppId(Game game)
        {
            if (GameCustomDataLookup.TryGetProviderOverrideValue(game.Id, ProviderKey, out var overrideId) &&
                !string.IsNullOrWhiteSpace(overrideId))
            {
                return overrideId.Trim();
            }

            return game.PluginId == MetaLibraryPluginId && !string.IsNullOrWhiteSpace(game.GameId)
                ? game.GameId.Trim()
                : null;
        }

        private GameAchievementData CreateGameResult(Game game, string appId, List<AchievementDetail> achievements)
        {
            return new GameAchievementData
            {
                LastUpdatedUtc = DateTime.UtcNow,
                ProviderKey = ProviderKey,
                LibrarySourceName = game?.Source?.Name,
                HasAchievements = achievements.Count > 0,
                GameName = game?.Name,
                ProviderGameKey = appId,
                PlayniteGameId = game?.Id,
                Achievements = achievements
            };
        }

        public ProviderSettingsViewBase CreateSettingsView() => new MetaSettingsView(_sessionManager);

        public void Dispose()
        {
            _apiClient.Dispose();
        }
    }
}
