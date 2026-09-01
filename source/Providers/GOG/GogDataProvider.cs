using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Providers.GOG.Local;
using PlayniteAchievements.Providers.Overrides;
using PlayniteAchievements.Providers.Settings;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.GameCustomData;
using PlayniteAchievements.Services.Refresh;
using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.GOG
{
    /// <summary>
    /// IDataProvider implementation for GOG achievements.
    /// Uses WebView-based authentication and GOG gameplay API.
    /// </summary>
    public sealed class GogDataProvider : DataProviderBase<GogSettings>, IDataProvider, IAchievementPageLinkProvider, IProviderOverride, IRefreshAuthContextReceiver, IInGameProgressSource, IDisposable
    {
        /// <summary>
        /// Resolved once at game start so the fast prong never repeats product/user discovery.
        /// GOG's remote coverage is the monitor's universal provider-refresh prong; this state is
        /// Galaxy-database only.
        /// </summary>
        private sealed class GogInGameState
        {
            public string ReleaseKey { get; set; }
            public long UserId { get; set; }
            public string DatabasePath { get; set; }
            public string GameName { get; set; }
        }

        public ProviderOverrideDescriptor OverrideDescriptor { get; } = ProviderOverrideDescriptor.Text(
            "LOCPlayAch_ManageAchievements_Overrides_ProviderValueLabel_GOG",
            ProviderOverrideValidators.RequiredText);

        internal static readonly Guid GogPluginId = Guid.Parse("AEBE8B7C-6DC3-4A66-AF31-E7375C6B5E9E");
        internal static readonly Guid GogOSSPluginId = Guid.Parse("03689811-3F33-4DFB-A121-2EE168FB9A5C");

        private readonly ILogger _logger;
        private readonly GogSessionManager _sessionManager;
        private readonly GogScanner _scanner;
        private readonly GogGalaxyDbReader _galaxyDbReader;
        private readonly HttpClient _httpClient;

        public GogDataProvider(
            ILogger logger,
            PlayniteAchievementsSettings settings,
            IPlayniteAPI playniteApi,
            string pluginUserDataPath)
        {
            if (logger == null) throw new ArgumentNullException(nameof(logger));
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (playniteApi == null) throw new ArgumentNullException(nameof(playniteApi));
            if (string.IsNullOrWhiteSpace(pluginUserDataPath)) throw new ArgumentException("Plugin user data path is required.", nameof(pluginUserDataPath));

            _logger = logger;
            _httpClient = HttpClientFactory.Create();
            _sessionManager = new GogSessionManager(playniteApi, logger);

            var clientIdCacheStore = new GogClientIdCacheStore(pluginUserDataPath, logger);
            var apiClient = new GogApiClient(_httpClient, logger, _sessionManager, clientIdCacheStore);
            _scanner = new GogScanner(settings, apiClient, _sessionManager, logger);
            _galaxyDbReader = new GogGalaxyDbReader(logger);
        }

        public string ProviderName => ResourceProvider.GetString("LOCPlayAch_Provider_GOG");

        public string ProviderKey => "GOG";

        public string ProviderIconKey => "ProviderIconGOG";

        public string ProviderColorHex => "#A855F7";

        public bool IsAuthenticated => _sessionManager.IsAuthenticated;

        public ISessionManager AuthSession => _sessionManager;

        public PlayniteAchievements.Models.Friends.IFriendsProvider Friends => null;

        public bool IsCapable(Game game) =>
            IsGogCapable(game);

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
            var links = context?.Game?.Links;
            if (links == null)
            {
                return false;
            }

            foreach (var link in links)
            {
                if (TryGetGogSlug(link?.Url, out var slug))
                {
                    url = $"https://www.gog.com/en/game/{Uri.EscapeDataString(slug)}";
                    return true;
                }
            }

            return false;
        }

        private static bool IsGogCapable(Game game)
        {
            return game != null &&
                   (game.PluginId == GogPluginId ||
                    game.PluginId == GogOSSPluginId ||
                    GameCustomDataLookup.TryGetProviderOverrideValue(game.Id, "GOG", out _));
        }

        internal static bool TryGetGogSlug(string linkUrl, out string slug)
        {
            slug = null;
            if (string.IsNullOrWhiteSpace(linkUrl) ||
                !Uri.TryCreate(linkUrl.Trim(), UriKind.Absolute, out var uri) ||
                !IsGogHost(uri.Host))
            {
                return false;
            }

            var segments = uri.AbsolutePath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < segments.Length - 1; i++)
            {
                if (string.Equals(segments[i], "game", StringComparison.OrdinalIgnoreCase))
                {
                    slug = Uri.UnescapeDataString(segments[i + 1]).Trim();
                    return !string.IsNullOrWhiteSpace(slug);
                }
            }

            return false;
        }

        private static bool IsGogHost(string host)
        {
            return string.Equals(host, "gog.com", StringComparison.OrdinalIgnoreCase) ||
                   host?.EndsWith(".gog.com", StringComparison.OrdinalIgnoreCase) == true;
        }

        public Task<RebuildPayload> RefreshAsync(
            IReadOnlyList<Game> gamesToRefresh,
            Action<Game> onGameStarting,
            Func<Game, GameAchievementData, Task> onGameCompleted,
            CancellationToken cancel)
        {
            return _scanner.RefreshAsync(gamesToRefresh, onGameStarting, onGameCompleted, cancel);
        }

        InGameProgressRegistration IInGameProgressSource.TryRegister(
            Game game,
            GameAchievementData cachedSchema)
        {
            if (game == null ||
                cachedSchema?.Achievements == null ||
                cachedSchema.Achievements.Count == 0 ||
                !string.Equals(cachedSchema.ProviderKey, ProviderKey, StringComparison.OrdinalIgnoreCase) ||
                !GogScanner.TryGetProductId(game, out var productId) ||
                !long.TryParse(
                    ProviderSettings?.UserId?.Trim(),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var userId))
            {
                return null;
            }

            var databasePath = GogGalaxyDbReader.GetDefaultDatabasePath();
            if (!File.Exists(databasePath))
            {
                // No readable Galaxy database (Galaxy not installed, or a nonstandard data
                // location). Decline: with no fast prong to offer, the monitor's universal
                // provider-refresh prong is this game's sole coverage.
                _logger?.Info(
                    $"[GogAch] No in-game fast source for '{game.Name}'; the provider refresh prong " +
                    $"covers it (no Galaxy database at '{databasePath}').");
                return null;
            }

            var releaseKey = "gog_" + productId;
            _logger?.Info(
                $"[GogAch] In-game tracking for '{game.Name}' via Galaxy database: {databasePath} ({releaseKey}).");
            return new InGameProgressRegistration
            {
                ProviderKey = ProviderKey,
                // Galaxy runs the database in WAL mode: unlock writes land in the -wal file and
                // reach the main file only on checkpoint, so both are watch targets.
                WatchTargets = new[] { databasePath, databasePath + "-wal" },
                PollInterval = InGameProgressRegistration.FileWatchSafetyPollInterval,
                // Galaxy persists a row roughly 20-30 seconds after the on-screen unlock, but
                // stamps unlockTime with the actual gameplay moment (UTC, second granularity).
                // The database write time therefore lags the moment worth capturing, which is the
                // opposite of Steam's near-instant file write: the provider-reported time is the
                // capture-grade anchor here, and ComputeClipWindow already accepts an anchor up to
                // one poll interval plus the pre-roll before observation before re-anchoring.
                UnlockAnchorPolicy = InGameUnlockAnchorPolicy.ProviderReported,
                State = new GogInGameState
                {
                    ReleaseKey = releaseKey,
                    UserId = userId,
                    DatabasePath = databasePath,
                    GameName = game.Name
                }
            };
        }

        /// <summary>
        /// Reads the Galaxy database, re-read on the file-watch safety cadence. Observations only
        /// ever assert an unlock — the progress writer is monotonic — so a busy or mid-checkpoint
        /// database can never retract what an earlier read reported, and a database that stops
        /// updating (Galaxy closed, game launched outside Galaxy) is covered by the monitor's
        /// universal provider-refresh prong rather than by a remote read here.
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
                var state = context?.Registration?.State as GogInGameState;
                if (state == null)
                {
                    results.Add(InGameProgressQueryResult.Failed(gameId, "registration_missing"));
                    continue;
                }

                if (!_galaxyDbReader.TryRead(
                    state.DatabasePath,
                    state.ReleaseKey,
                    state.UserId,
                    out var observations))
                {
                    results.Add(InGameProgressQueryResult.Failed(gameId, "file_unstable"));
                    continue;
                }

                results.Add(InGameProgressQueryResult.Succeeded(gameId, observations));
            }

            return Task.FromResult<IReadOnlyList<InGameProgressQueryResult>>(results);
        }

        public void Dispose()
        {
            _httpClient?.Dispose();
        }

        public void BeginRefreshAuthContext(RefreshAuthContext context)
        {
            _scanner?.BeginRefreshAuthContext(context);
        }

        public void EndRefreshAuthContext(RefreshAuthContext context)
        {
            _scanner?.EndRefreshAuthContext(context);
        }

        /// <inheritdoc />
        public ProviderSettingsViewBase CreateSettingsView() => new GogSettingsView(_sessionManager);
    }
}
