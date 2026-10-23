using Playnite.SDK;
using Playnite.SDK.Models;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Providers.Exophase;
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

namespace PlayniteAchievements.Providers.Ubisoft
{
    /// <summary>
    /// Data provider for Ubisoft Connect achievements. Claims games imported by the Ubisoft Connect
    /// library plugin (whose GameId is the launcher product id) and games given a manual product id
    /// override. Each refresh maps product ids to ubiservices space ids through the account's
    /// entitlements once, then reads each game's achievements with the Ubisoft Connect client's own
    /// GraphQL query.
    /// </summary>
    internal sealed class UbisoftDataProvider : DataProviderBase<UbisoftSettings>, IDataProvider, IProviderOverride, IEnglishTitleSource, IDisposable
    {
        private const string FallbackLocale = "en-US";

        private static readonly Guid UbisoftLibraryPluginId = ResolveUbisoftLibraryPluginId();

        public ProviderOverrideDescriptor OverrideDescriptor { get; } = ProviderOverrideDescriptor.Text(
            "LOCPlayAch_ManageAchievements_Overrides_ProviderValueLabel_Ubisoft",
            raw =>
            {
                var productId = UbisoftParsing.ParseProductId(raw);
                return productId.HasValue
                    ? ProviderOverrideValidation.Valid(productId.Value.ToString(CultureInfo.InvariantCulture))
                    : ProviderOverrideValidation.Invalid(ProviderOverrideValidators.RequiredValueErrorKey);
            });

        private readonly ILogger _logger;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly IPlayniteAPI _playniteApi;
        private readonly string _pluginUserDataPath;
        private readonly UbisoftApiClient _apiClient;
        private readonly UbisoftSessionManager _sessionManager;

        // The last refresh's product id to space id map, reused by English title lookups.
        private volatile Dictionary<long, string> _lastGameSpaces;

        public UbisoftDataProvider(
            ILogger logger,
            PlayniteAchievementsSettings settings,
            IPlayniteAPI playniteApi,
            string pluginUserDataPath)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _playniteApi = playniteApi ?? throw new ArgumentNullException(nameof(playniteApi));
            _pluginUserDataPath = pluginUserDataPath;
            _apiClient = new UbisoftApiClient(logger);
            _sessionManager = new UbisoftSessionManager(playniteApi, logger, _apiClient);
        }

        public string ProviderName => ResourceProvider.GetString("LOCPlayAch_Provider_Ubisoft");
        public string ProviderKey => "Ubisoft";
        public string ProviderIconKey => "ProviderIconUbisoft";
        public string ProviderColorHex => "#0044AA";

        public bool IsAuthenticated => _sessionManager.IsAuthenticated;

        public ISessionManager AuthSession => _sessionManager;

        public PlayniteAchievements.Models.Friends.IFriendsProvider Friends => null;

        public bool IsCapable(Game game)
        {
            if (game == null || game.Id == Guid.Empty || !ProviderSettings.IsEnabled)
            {
                return false;
            }

            return (UbisoftLibraryPluginId != Guid.Empty && game.PluginId == UbisoftLibraryPluginId) ||
                   GameCustomDataLookup.TryGetProviderOverrideValue(game.Id, ProviderKey, out _);
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

            UbisoftSession session;
            Dictionary<long, string> gameSpaces;
            try
            {
                session = await _sessionManager.GetSessionAsync(cancel).ConfigureAwait(false);
                if (session == null)
                {
                    _logger.Warn("[Ubisoft] Not signed in at refresh start. Refresh aborted.");
                    return new RebuildPayload { Summary = new RebuildSummary(), AuthRequired = true };
                }

                gameSpaces = await _apiClient.GetGameSpacesAsync(session, cancel).ConfigureAwait(false);
                _lastGameSpaces = gameSpaces;
            }
            catch (UbisoftAuthException ex)
            {
                _logger.Warn($"[Ubisoft] {ex.Message}");
                _sessionManager.InvalidateSession();
                return new RebuildPayload { Summary = new RebuildSummary(), AuthRequired = true };
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Without the space ids no game can be queried; skip the run rather than fail each game.
                _logger.Warn(ex, "[Ubisoft] Owned games could not be read; skipping this run.");
                return new RebuildPayload { Summary = new RebuildSummary() };
            }

            var locale = FormattingCulture.GetCultureTag(_settings.Persisted?.GlobalLanguage) ?? FallbackLocale;
            var rateLimiter = new RateLimiter(
                _settings.Persisted.ScanDelayMs,
                _settings.Persisted.MaxRetryAttempts);
            return await ProviderRefreshExecutor.RunProviderGamesAsync(
                gamesToRefresh,
                onGameStarting,
                async (game, ct) =>
                {
                    if (!IsCapable(game))
                    {
                        return ProviderRefreshExecutor.ProviderGameResult.Skipped();
                    }

                    var productId = ResolveProductId(game);
                    if (!productId.HasValue)
                    {
                        _logger.Warn($"[Ubisoft] Could not resolve a Ubisoft product id for '{game.Name}'.");
                        return ProviderRefreshExecutor.ProviderGameResult.Skipped();
                    }

                    if (!gameSpaces.TryGetValue(productId.Value, out var spaceId))
                    {
                        _logger.Debug($"[Ubisoft] Product {productId.Value} ('{game.Name}') is not an owned game on this account.");
                        return new ProviderRefreshExecutor.ProviderGameResult
                        {
                            Data = CreateGameResult(game, productId.Value, new List<AchievementDetail>())
                        };
                    }

                    var graphGame = await rateLimiter.ExecuteWithRetryAsync(
                        async () => await _apiClient.GetAchievementsAsync(
                            await _sessionManager.GetSessionAsync(ct).ConfigureAwait(false) ?? session,
                            spaceId,
                            productId.Value,
                            locale,
                            ct).ConfigureAwait(false),
                        IsTransientError,
                        ct).ConfigureAwait(false);

                    var achievements = UbisoftParsing.MapAchievements(graphGame?.Viewer?.Meta?.Achievements);
                    var data = CreateGameResult(game, productId.Value, achievements);
                    // Metadata sources search Ubisoft's own title first: the Playnite name can
                    // differ, and an overridden game's name can be anything.
                    data.MetadataHints = new MetadataHints { SearchName = graphGame?.Name };

                    return new ProviderRefreshExecutor.ProviderGameResult
                    {
                        Data = data
                    };
                },
                onGameCompleted,
                isAuthRequiredException: ex => ex is UbisoftAuthException,
                onGameError: (game, ex, consecutiveErrors) =>
                {
                    _logger.Warn(ex, $"[Ubisoft] Failed to refresh '{game?.Name}' after {consecutiveErrors} consecutive errors.");
                },
                rateLimiter,
                cancel).ConfigureAwait(false);
        }

        /// <summary>
        /// The game's achievements read again with the en-US locale. The space id comes from the
        /// last refresh's entitlements, fetched once here when no refresh has run this session.
        /// </summary>
        async Task<IList<AchievementDetail>> IEnglishTitleSource.GetEnglishTitlesAsync(Game game, GameAchievementData data, CancellationToken ct)
        {
            if (game == null)
            {
                return null;
            }

            var productId = ResolveProductId(game);
            if (!productId.HasValue)
            {
                return null;
            }

            var session = await _sessionManager.GetSessionAsync(ct).ConfigureAwait(false);
            if (session == null)
            {
                return null;
            }

            var gameSpaces = _lastGameSpaces;
            if (gameSpaces == null)
            {
                gameSpaces = await _apiClient.GetGameSpacesAsync(session, ct).ConfigureAwait(false);
                _lastGameSpaces = gameSpaces;
            }

            if (gameSpaces == null || !gameSpaces.TryGetValue(productId.Value, out var spaceId))
            {
                return null;
            }

            var graphGame = await _apiClient.GetAchievementsAsync(session, spaceId, productId.Value, FallbackLocale, ct).ConfigureAwait(false);
            if (graphGame == null)
            {
                return null;
            }

            return UbisoftParsing.MapAchievements(graphGame.Viewer?.Meta?.Achievements)
                .Select(achievement => new AchievementDetail
                {
                    ApiName = achievement.ApiName,
                    DisplayName = achievement.DisplayName,
                    Description = achievement.Description
                })
                .ToList();
        }

        /// <summary>
        /// A manual per-game override wins; otherwise the library plugin's GameId, which is the
        /// launcher product id.
        /// </summary>
        private long? ResolveProductId(Game game)
        {
            if (GameCustomDataLookup.TryGetProviderOverrideValue(game.Id, ProviderKey, out var overrideId))
            {
                var overridden = UbisoftParsing.ParseProductId(overrideId);
                if (overridden.HasValue)
                {
                    return overridden;
                }
            }

            return game.PluginId == UbisoftLibraryPluginId ? UbisoftParsing.ParseProductId(game.GameId) : null;
        }



        private static bool IsTransientError(Exception ex)
        {
            return TransientErrorClassifier.IsTransient(ex, e =>
                e is UbisoftAuthException ? false :
                e is TaskCanceledException ? true :
                (bool?)null);
        }

        private GameAchievementData CreateGameResult(Game game, long productId, List<AchievementDetail> achievements)
        {
            return new GameAchievementData
            {
                LastUpdatedUtc = DateTime.UtcNow,
                ProviderKey = ProviderKey,
                LibrarySourceName = game?.Source?.Name,
                HasAchievements = achievements.Count > 0,
                GameName = game?.Name,
                ProviderGameKey = productId.ToString(CultureInfo.InvariantCulture),
                PlayniteGameId = game?.Id,
                Achievements = achievements
            };
        }

        public ProviderSettingsViewBase CreateSettingsView() => new UbisoftSettingsView(_sessionManager);

        public void Dispose()
        {
            _apiClient.Dispose();
        }

        private static Guid ResolveUbisoftLibraryPluginId()
        {
            try
            {
                return BuiltinExtensions.GetIdFromExtension(BuiltinExtension.UplayLibrary);
            }
            catch
            {
                return Guid.Empty;
            }
        }
    }
}
