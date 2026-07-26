using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Friends;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Providers.Overrides;
using PlayniteAchievements.Providers.Settings;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.GameCustomData;
using PlayniteAchievements.Services.Refresh;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Playnite.SDK;
using Playnite.SDK.Models;

namespace PlayniteAchievements.Providers.Steam
{
    internal sealed class SteamDataProvider : DataProviderBase<SteamSettings>, IDataProvider, IAchievementPageLinkProvider, IProviderOverride, IRefreshAuthContextReceiver, IDisposable
    {
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

        public SteamDataProvider(
            ILogger logger,
            PlayniteAchievementsSettings settings,
            IPlayniteAPI api,
            string pluginUserDataPath)
        {
            if (logger == null) throw new ArgumentNullException(nameof(logger));
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (api == null) throw new ArgumentNullException(nameof(api));

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

        /// <inheritdoc />
        public ProviderSettingsViewBase CreateSettingsView() => new SteamSettingsView(_sessionManager);

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
