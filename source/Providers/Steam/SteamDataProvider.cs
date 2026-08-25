using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Friends;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Providers.Overrides;
using PlayniteAchievements.Providers.Settings;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.GameCustomData;
using PlayniteAchievements.Services.Refresh;
using PlayniteAchievements.Providers.Steam.Local;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Playnite.SDK;
using Playnite.SDK.Models;

namespace PlayniteAchievements.Providers.Steam
{
    internal sealed class SteamDataProvider : DataProviderBase<SteamSettings>, IDataProvider, IAchievementPageLinkProvider, IProviderOverride, IRefreshAuthContextReceiver, IInGameProgressSource, IOfflineRefreshFallbackProvider, IDisposable
    {
        /// <summary>
        /// Resolved once at game start so the fast prong never repeats path discovery. Steam's remote
        /// coverage is the monitor's universal provider-refresh prong, which runs for every tracked
        /// game on the user's in-game interval — this state is local-file only. A private remote read
        /// here would scrape the rate-limited community page twice per interval.
        /// </summary>
        private sealed class SteamInGameState
        {
            public string StatsPath { get; set; }
            public string SchemaPath { get; set; }
            public int AppId { get; set; }
            public string GameName { get; set; }
        }

        internal static readonly Guid SteamPluginId = SteamGameIdentity.SteamPluginId;

        public ProviderOverrideDescriptor OverrideDescriptor { get; } = ProviderOverrideDescriptor.Text(
            "LOCPlayAch_ManageAchievements_Overrides_ProviderValueLabel_Steam",
            raw =>
            {
                if (int.TryParse((raw ?? string.Empty).Trim(), out var appId) && appId > 0)
                {
                    return ProviderOverrideValidation.Valid(appId.ToString(CultureInfo.InvariantCulture));
                }

                return ProviderOverrideValidation.Invalid(
                    "LOCPlayAch_Menu_SteamAppId_InvalidId");
            });

        private readonly SteamHttpClient _steamClient;
        private readonly SteamScanner _scanner;
        private readonly SteamSessionManager _sessionManager;
        private readonly SteamWebApiTokenResolver _tokenResolver;
        private readonly SteamHuntersCategoryEnricher _steamHuntersCategoryEnricher;
        private readonly IFriendsProvider _friendsProvider;
        private readonly SteamLocalStatsReader _localStatsReader = new SteamLocalStatsReader();
        private readonly ILogger _logger;
        private readonly IPlayniteAPI _api;

        public SteamDataProvider(
            ILogger logger,
            PlayniteAchievementsSettings settings,
            IPlayniteAPI api,
            string pluginUserDataPath,
            SteamApiTokenService steamApiTokenService = null)
        {
            if (logger == null) throw new ArgumentNullException(nameof(logger));
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (api == null) throw new ArgumentNullException(nameof(api));

            _logger = logger;
            _api = api;
            _sessionManager = new SteamSessionManager(api, logger);

            // Create Steam-specific dependencies
            _steamClient = new SteamHttpClient(api, logger, _sessionManager, pluginUserDataPath);
            var steamApiClient = new SteamApiClient(_steamClient.ApiHttpClient, logger);
            // SteamHunters is fetched through the offscreen webview (the scan's shared leased
            // view): its WAF tarpits the .NET HTTP stack's TLS fingerprint but accepts CEF's.
            var steamHuntersApiClient = new SteamHuntersApiClient(
                (url, ct) => _sessionManager.OffscreenViews.GetPageTextAsync(url, ct),
                logger);
            _steamHuntersCategoryEnricher = new SteamHuntersCategoryEnricher(
                steamHuntersApiClient,
                logger,
                () => PlayniteAchievementsPlugin.Instance?.DiskImageService);
            _tokenResolver = new SteamWebApiTokenResolver(_sessionManager, logger);
            _sessionManager.SetClearInMemoryAuthState(_steamClient.ClearInMemoryAuthState);
            _scanner = new SteamScanner(settings, _steamClient, steamApiClient, _tokenResolver, _steamHuntersCategoryEnricher, api, logger);
            _friendsProvider = new SteamFriendsProvider(_steamClient, steamApiClient, _scanner, _tokenResolver, _steamHuntersCategoryEnricher, _sessionManager, logger);
            if (steamApiTokenService != null)
            {
                steamApiTokenService.GetSessionTokenAsync = _steamClient.GetWebApiTokenAsync;
            }
        }

        public string ProviderName => ResourceProvider.GetString("LOCPlayAch_Provider_Steam");
        public string ProviderKey => "Steam";
        public string ProviderIconKey => "ProviderIconSteam";
        public string ProviderColorHex => "#B0B0B0";

        /// <summary>
        /// Snapshot of the last known persisted Steam auth state.
        /// AuthSession is the authoritative auth check for runtime flows.
        /// </summary>
        public bool IsAuthenticated =>
            !string.IsNullOrWhiteSpace(ProviderSettings.SteamUserId);

        bool IOfflineRefreshFallbackProvider.CanAttemptOfflineRefresh
        {
            get
            {
                if (!SteamIdHelper.TryGetAccountId3(
                    ProviderSettings?.SteamUserId,
                    out _))
                {
                    return false;
                }

                var steamPath = SteamInstallLocator.ResolveSteamPath(
                    ProviderSettings?.SteamInstallPathOverride);
                return !string.IsNullOrWhiteSpace(steamPath) &&
                       Directory.Exists(Path.Combine(steamPath, "appcache", "stats"));
            }
        }

        public ISessionManager AuthSession => _sessionManager;

        public IFriendsProvider Friends => _friendsProvider;

        public bool IsCapable(Game game) =>
            IsSteamCapable(game);

        public bool CanResolveAchievementPageUrl(AchievementPageLinkContext context)
        {
            return TryBuildAchievementPageUrl(context, out _);
        }

        public Task<string> GetAchievementPageUrlAsync(
            AchievementPageLinkContext context,
            CancellationToken cancel)
        {
            return Task.FromResult(
                TryBuildAchievementPageUrl(context, out var url)
                    ? url
                    : null);
        }

        internal static bool TryBuildAchievementPageUrl(
            AchievementPageLinkContext context,
            out string url)
        {
            url = null;
            if (!TryResolveSteamAppId(context, out var appId))
            {
                return false;
            }

            url = $"https://steamcommunity.com/stats/{appId.ToString(CultureInfo.InvariantCulture)}/achievements";
            return true;
        }

        private static bool IsSteamCapable(Game game)
        {
            return game != null &&
                   (game.PluginId == SteamPluginId ||
                    GameCustomDataLookup.TryGetSteamAppIdOverride(game.Id, out _));
        }

        private static bool TryResolveSteamAppId(
            AchievementPageLinkContext context,
            out int appId)
        {
            appId = 0;
            if (context?.Game != null &&
                GameCustomDataLookup.TryGetSteamAppIdOverride(context.Game.Id, out appId))
            {
                return true;
            }

            if (string.Equals(context?.ManualLink?.SourceKey, "Steam", StringComparison.OrdinalIgnoreCase) &&
                TryGetPositiveId(context.ManualLink.SourceGameId, out appId))
            {
                return true;
            }

            var cachedAppId = context?.BestGameData?.AppId ?? 0;
            if (cachedAppId > 0)
            {
                appId = cachedAppId;
                return true;
            }

            return TryGetPositiveId(context?.Game?.GameId, out appId);
        }

        internal static bool TryGetSteamAppId(Game game, out int appId)
        {
            return SteamGameIdentity.TryGetSteamAppId(game, out appId);
        }

        internal static bool TryGetSteamAccountOverride(Guid gameId, out string steamAccountId)
        {
            steamAccountId = null;
            var store = PlayniteAchievementsPlugin.Instance?.GameCustomDataStore;
            return store != null &&
                   store.TryLoad(gameId, out var data) &&
                   !string.IsNullOrWhiteSpace(steamAccountId = data?.SteamAccountIdOverride);
        }

        internal static bool TrySetSteamAccountOverride(
            Guid gameId,
            string steamAccountId,
            string gameName,
            Action persistSettingsForUi,
            ILogger logger)
        {
            var store = PlayniteAchievementsPlugin.Instance?.GameCustomDataStore;
            if (gameId == Guid.Empty || store == null || string.IsNullOrWhiteSpace(steamAccountId))
            {
                return false;
            }

            store.Update(gameId, data => data.SteamAccountIdOverride = steamAccountId.Trim());
            persistSettingsForUi?.Invoke();
            logger?.Info($"Set Steam account override for '{gameName}'.");
            return true;
        }

        internal static bool TryClearSteamAccountOverride(
            Guid gameId,
            string gameName,
            Action persistSettingsForUi,
            ILogger logger)
        {
            var store = PlayniteAchievementsPlugin.Instance?.GameCustomDataStore;
            if (gameId == Guid.Empty || store == null ||
                !store.TryLoad(gameId, out var current) ||
                string.IsNullOrWhiteSpace(current?.SteamAccountIdOverride))
            {
                return false;
            }

            store.Update(gameId, data => data.SteamAccountIdOverride = null);
            persistSettingsForUi?.Invoke();
            logger?.Info($"Cleared Steam account override for '{gameName}'.");
            return true;
        }

        private static bool TryGetPositiveId(string value, out int id)
        {
            return int.TryParse(
                       (value ?? string.Empty).Trim(),
                       NumberStyles.Integer,
                       CultureInfo.InvariantCulture,
                       out id) &&
                   id > 0;
        }

        public async Task<RebuildPayload> RefreshAsync(
            IReadOnlyList<Game> gamesToRefresh,
            Action<Game> onGameStarting,
            Func<Game, GameAchievementData, Task> onGameCompleted,
            CancellationToken cancel)
        {
            using (_sessionManager.BeginOffscreenViewLease())
            {
                return await _scanner.RefreshAsync(gamesToRefresh, onGameStarting, onGameCompleted, cancel).ConfigureAwait(false);
            }
        }

        internal async Task EnrichSteamHuntersCategoriesForExternalProviderAsync(
            int appId,
            string gameName,
            IList<AchievementDetail> achievements,
            Guid? playniteGameId,
            CancellationToken cancel)
        {
            if (_steamHuntersCategoryEnricher == null)
            {
                return;
            }

            using (_sessionManager.BeginOffscreenViewLease())
            {
                await _steamHuntersCategoryEnricher
                    .EnrichAsync(appId, gameName, achievements, playniteGameId, cancel)
                    .ConfigureAwait(false);
            }
        }

        InGameProgressRegistration IInGameProgressSource.TryRegister(
            Game game,
            GameAchievementData cachedSchema)
        {
            if (game == null ||
                cachedSchema?.Achievements == null ||
                cachedSchema.Achievements.Count == 0 ||
                !string.Equals(cachedSchema.ProviderKey, ProviderKey, StringComparison.OrdinalIgnoreCase) ||
                !SteamIdHelper.TryGetAccountId3(ProviderSettings?.SteamUserId, out var accountId3))
            {
                return null;
            }

            var appId = cachedSchema.AppId;
            if (appId <= 0 && !TryGetSteamAppId(game, out appId))
            {
                return null;
            }

            var steamPath = SteamInstallLocator.ResolveSteamPath(
                ProviderSettings?.SteamInstallPathOverride);
            var statsPath = SteamInstallLocator.BuildUserGameStatsPath(steamPath, accountId3, appId);
            var schemaPath = SteamInstallLocator.BuildSchemaPath(steamPath, appId);
            var statsDirectory = string.IsNullOrWhiteSpace(statsPath)
                ? null
                : Path.GetDirectoryName(statsPath);
            var localUsable =
                !string.IsNullOrWhiteSpace(statsDirectory) &&
                Directory.Exists(statsDirectory) &&
                File.Exists(schemaPath);

            if (!localUsable)
            {
                // No readable local stats (Steam installed elsewhere, the game has never written
                // them, a non-default userdata layout). Decline: with no fast prong to offer, the
                // monitor's universal provider-refresh prong is this game's sole coverage, which is
                // exactly the remote read a registration here would have duplicated.
                _logger?.Info(
                    $"[SteamAch] No in-game fast source for '{game.Name}'; the provider refresh prong " +
                    $"covers it (no local stats at '{statsPath}' / schema at '{schemaPath}').");
                return null;
            }

            _logger?.Info(
                $"[SteamAch] In-game tracking for '{game.Name}' via local stats: {statsPath}.");
            return new InGameProgressRegistration
            {
                ProviderKey = ProviderKey,
                WatchTargets = new[] { statsPath },
                PollInterval = InGameProgressRegistration.FileWatchSafetyPollInterval,
                // AchievementTimes is in Steam's timestamp domain and can differ by seconds from
                // the Windows clock used by video segments. The local file change is the StoreStats
                // correlation point on that same Windows clock, so it is the capture-grade anchor.
                UnlockAnchorPolicy = InGameUnlockAnchorPolicy.SourceObservation,
                State = new SteamInGameState
                {
                    AppId = appId,
                    GameName = game.Name,
                    StatsPath = statsPath,
                    SchemaPath = schemaPath
                }
            };
        }

        /// <summary>
        /// Reads the local stats file, re-read on the file-watch safety cadence. Observations only
        /// ever assert an unlock — the progress writer is monotonic — so a locked or mid-write file
        /// can never retract what an earlier read reported, and a file that stops updating entirely
        /// (Steam offline, a sync engine holding the handle, cloud-save lag) is covered by the
        /// monitor's universal provider-refresh prong rather than by a remote read here.
        /// </summary>
        Task<IReadOnlyList<InGameProgressQueryResult>> IInGameProgressSource.QueryAsync(
            IReadOnlyList<InGameTrackingContext> games,
            CancellationToken cancellationToken)
        {
            var results = new List<InGameProgressQueryResult>();
            foreach (var context in games ?? Array.Empty<InGameTrackingContext>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var gameId = context?.Game?.Id ?? Guid.Empty;
                var state = context?.Registration?.State as SteamInGameState;
                if (state == null)
                {
                    results.Add(InGameProgressQueryResult.Failed(gameId, "registration_missing"));
                    continue;
                }

                var read = _localStatsReader.TryRead(state.StatsPath, state.SchemaPath);
                if (!read.Success)
                {
                    results.Add(InGameProgressQueryResult.Failed(gameId, "file_unstable"));
                    continue;
                }

                var observations = read.UnlockByApiName
                    .Select(pair => new AchievementProgressObservation
                    {
                        ApiName = pair.Key,
                        Unlocked = true,
                        UnlockTimeUtc = pair.Value
                    })
                    .ToList();
                results.Add(InGameProgressQueryResult.Succeeded(gameId, observations));
            }

            return Task.FromResult<IReadOnlyList<InGameProgressQueryResult>>(results);
        }

        /// <inheritdoc />
        public ProviderSettingsViewBase CreateSettingsView() => new SteamSettingsView(_sessionManager, _api);

        public void Dispose()
        {
            _steamClient?.Dispose();
        }

        public void BeginRefreshAuthContext(RefreshAuthContext context)
        {
            _steamHuntersCategoryEnricher?.ClearCache();
            _tokenResolver?.BeginRefreshAuthContext(context);
        }

        public void EndRefreshAuthContext(RefreshAuthContext context)
        {
            _tokenResolver?.EndRefreshAuthContext(context);
        }
    }
}
