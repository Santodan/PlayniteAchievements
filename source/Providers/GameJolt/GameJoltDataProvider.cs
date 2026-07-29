using Playnite.SDK;
using Playnite.SDK.Models;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Providers.Overrides;
using PlayniteAchievements.Providers.Settings;
using PlayniteAchievements.Services.GameCustomData;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.GameJolt
{
    /// <summary>
    /// Data provider for GameJolt trophies (achievements). Claims games imported by the GameJolt Library
    /// Playnite plugin (whose GameId is the numeric GameJolt game id) and games given a manual GameJolt
    /// id override. Achievement data is read from GameJolt's cookie-authenticated site-api.
    /// </summary>
    internal sealed class GameJoltDataProvider : DataProviderBase<GameJoltSettings>, IDataProvider, IProviderOverride
    {
        // GameJolt Library (third-party Playnite plugin) extension id. Games it imports carry the numeric
        // GameJolt game id as Game.GameId, which the site-api trophy endpoints take directly.
        private static readonly Guid GameJoltLibraryPluginId = new Guid("555D58FD-A000-401B-972C-9230BED81AED");

        public ProviderOverrideDescriptor OverrideDescriptor { get; } = ProviderOverrideDescriptor.Text(
            "LOCPlayAch_ManageAchievements_Overrides_ProviderValueLabel_GameJolt",
            raw =>
            {
                if (int.TryParse((raw ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var gameId) &&
                    gameId > 0)
                {
                    return ProviderOverrideValidation.Valid(gameId.ToString(CultureInfo.InvariantCulture));
                }

                return ProviderOverrideValidation.Invalid("LOCPlayAch_Menu_GameJoltGameId_InvalidId");
            });

        private readonly ILogger _logger;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly GameJoltSessionManager _sessionManager;
        private readonly GameJoltApiClient _apiClient;

        public GameJoltDataProvider(
            ILogger logger,
            PlayniteAchievementsSettings settings,
            IPlayniteAPI playniteApi,
            string pluginUserDataPath)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _sessionManager = new GameJoltSessionManager(playniteApi, logger, pluginUserDataPath);
            _apiClient = new GameJoltApiClient(playniteApi, logger, _sessionManager.CookieSnapshotStore);
        }

        public string ProviderName => ResourceProvider.GetString("LOCPlayAch_Provider_GameJolt");
        public string ProviderKey => "GameJolt";
        public string ProviderIconKey => "ProviderIconGameJolt";
        public string ProviderColorHex => "#CCFF00";

        public bool IsAuthenticated => _sessionManager?.IsAuthenticated ?? false;

        public ISessionManager AuthSession => _sessionManager;

        public PlayniteAchievements.Models.Friends.IFriendsProvider Friends => null;

        public bool IsCapable(Game game)
        {
            if (game == null || game.Id == Guid.Empty)
            {
                return false;
            }

            if (!ProviderSettings.IsEnabled)
            {
                return false;
            }

            if (game.PluginId == GameJoltLibraryPluginId)
            {
                return true;
            }

            return GameCustomDataLookup.TryGetProviderOverrideValue(game.Id, ProviderKey, out _);
        }

        public async Task<RebuildPayload> RefreshAsync(
            IReadOnlyList<Game> gamesToRefresh,
            Action<Game> onGameStarting,
            Func<Game, GameAchievementData, Task> onGameCompleted,
            CancellationToken cancel)
        {
            var summary = new RebuildSummary();
            var payload = new RebuildPayload { Summary = summary };

            if (gamesToRefresh == null || gamesToRefresh.Count == 0)
            {
                return payload;
            }

            var username = _sessionManager?.Username?.Trim();
            if (_sessionManager?.IsAuthenticated != true || string.IsNullOrWhiteSpace(username))
            {
                _logger?.Warn("[GameJolt] Not authenticated at refresh start. Refresh aborted.");
                payload.AuthRequired = true;
                return payload;
            }

            _apiClient.BeginCookieSession();
            try
            {
                foreach (var game in gamesToRefresh)
                {
                    if (cancel.IsCancellationRequested)
                    {
                        break;
                    }

                    if (game == null || game.Id == Guid.Empty || !IsCapable(game))
                    {
                        continue;
                    }

                    onGameStarting?.Invoke(game);

                    try
                    {
                        var data = await RefreshGameAsync(game, username, cancel).ConfigureAwait(false);

                        if (onGameCompleted != null)
                        {
                            await onGameCompleted(game, data).ConfigureAwait(false);
                        }

                        summary.GamesRefreshed++;
                        summary.RefreshedGameIds.Add(game.Id);
                        if (data != null && data.HasAchievements)
                        {
                            summary.GamesWithAchievements++;
                        }
                        else
                        {
                            summary.GamesWithoutAchievements++;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger?.Error(ex, $"[GameJolt] Failed to refresh game '{game.Name}' ({game.Id})");
                        summary.GamesWithoutAchievements++;

                        if (onGameCompleted != null)
                        {
                            await onGameCompleted(game, null).ConfigureAwait(false);
                        }
                    }
                }

                return payload;
            }
            finally
            {
                _apiClient.EndCookieSession();
            }
        }

        private async Task<GameAchievementData> RefreshGameAsync(Game game, string username, CancellationToken cancel)
        {
            var gameJoltId = ResolveGameJoltId(game);
            if (string.IsNullOrWhiteSpace(gameJoltId))
            {
                _logger?.Warn($"[GameJolt] Could not resolve a GameJolt game id for '{game.Name}'.");
                return CreateGameResult(game, gameJoltId, false, new List<AchievementDetail>());
            }

            var achievements = await _apiClient.GetAchievementsAsync(gameJoltId, username, cancel).ConfigureAwait(false);
            var hasAchievements = achievements != null && achievements.Count > 0;
            return CreateGameResult(game, gameJoltId, hasAchievements, achievements ?? new List<AchievementDetail>());
        }

        /// <summary>
        /// Resolves the numeric GameJolt game id: a manual per-game override wins, otherwise the
        /// GameJolt Library plugin's Game.GameId (already the numeric id).
        /// </summary>
        private string ResolveGameJoltId(Game game)
        {
            if (game == null)
            {
                return null;
            }

            if (GameCustomDataLookup.TryGetProviderOverrideValue(game.Id, ProviderKey, out var overrideId) &&
                !string.IsNullOrWhiteSpace(overrideId))
            {
                return overrideId.Trim();
            }

            return string.IsNullOrWhiteSpace(game.GameId) ? null : game.GameId.Trim();
        }

        private GameAchievementData CreateGameResult(
            Game game,
            string gameJoltId,
            bool hasAchievements,
            List<AchievementDetail> achievements)
        {
            return new GameAchievementData
            {
                LastUpdatedUtc = DateTime.UtcNow,
                ProviderKey = ProviderKey,
                LibrarySourceName = game?.Source?.Name,
                HasAchievements = hasAchievements,
                GameName = game?.Name,
                ProviderGameKey = string.IsNullOrWhiteSpace(gameJoltId) ? null : gameJoltId,
                PlayniteGameId = game?.Id ?? Guid.Empty,
                Achievements = achievements ?? new List<AchievementDetail>()
            };
        }

        public ProviderSettingsViewBase CreateSettingsView() => new GameJoltSettingsView(_sessionManager);
    }
}
