using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Providers.Settings;
using PlayniteAchievements.Services;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Playnite.SDK;
using Playnite.SDK.Models;

namespace PlayniteAchievements.Providers.Steam
{
    internal sealed class SteamDataProvider : IDataProvider, IRefreshTargetFilter, IDisposable
    {
        internal static readonly Guid SteamPluginId = ResolveSteamPluginId();

        private readonly SteamHttpClient _steamClient;
        private readonly SteamScanner _scanner;
        private readonly SteamSessionManager _sessionManager;
        private readonly IPlayniteAPI _api;
        private SteamSettings _providerSettings;

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

            _api = api;
            _sessionManager = new SteamSessionManager(api, logger);

            // Initialize provider settings from persisted settings dictionary
            _providerSettings = ProviderRegistry.Settings<SteamSettings>();

            // Create Steam-specific dependencies
            _steamClient = new SteamHttpClient(api, logger, _sessionManager, pluginUserDataPath);
            var steamApiClient = new SteamApiClient(_steamClient.ApiHttpClient, logger);
            var tokenResolver = new SteamWebApiTokenResolver(_sessionManager, _steamClient.GetWebApiTokenAsync, logger);
            _scanner = new SteamScanner(settings, _steamClient, steamApiClient, tokenResolver, api, logger);

            // Wire the session token delegate so LocalSavesProvider can use the authenticated token.
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
            !string.IsNullOrWhiteSpace(_providerSettings.SteamUserId);

        public ISessionManager AuthSession => _sessionManager;

        public bool IsCapable(Game game) =>
            IsSteamCapable(game);

        private static bool IsSteamCapable(Game game)
        {
            return game.PluginId == SteamPluginId;
        }

        internal static bool TryGetSteamAccountOverride(Guid gameId, out string steamAccountId)
        {
            return GameCustomDataLookup.TryGetSteamAccountIdOverride(gameId, out steamAccountId);
        }

        internal static bool TrySetSteamAccountOverride(
            Guid gameId,
            string steamAccountId,
            string gameName,
            Action persistSettingsForUi,
            ILogger logger)
        {
            if (gameId == Guid.Empty || string.IsNullOrWhiteSpace(steamAccountId))
            {
                return false;
            }

            var customDataStore = PlayniteAchievementsPlugin.Instance?.GameCustomDataStore;
            if (customDataStore == null)
            {
                return false;
            }

            customDataStore.Update(gameId, customData =>
            {
                customData.SteamAccountIdOverride = steamAccountId.Trim();
            });

            persistSettingsForUi?.Invoke();
            logger?.Info($"Set Steam account override for '{gameName}' to accountId='{steamAccountId}'.");
            return true;
        }

        internal static bool TryClearSteamAccountOverride(
            Guid gameId,
            string gameName,
            Action persistSettingsForUi,
            ILogger logger)
        {
            if (gameId == Guid.Empty)
            {
                return false;
            }

            var customDataStore = PlayniteAchievementsPlugin.Instance?.GameCustomDataStore;
            if (customDataStore == null ||
                !customDataStore.TryLoad(gameId, out var customData) ||
                string.IsNullOrWhiteSpace(customData?.SteamAccountIdOverride))
            {
                return false;
            }

            customDataStore.Update(gameId, data =>
            {
                data.SteamAccountIdOverride = null;
            });

            persistSettingsForUi?.Invoke();
            logger?.Info($"Cleared Steam account override for '{gameName}'.");
            return true;
        }

        public Task<RebuildPayload> RefreshAsync(
            IReadOnlyList<Game> gamesToRefresh,
            Action<Game> onGameStarting,
            Func<Game, GameAchievementData, Task> onGameCompleted,
            CancellationToken cancel)
        {
            return _scanner.RefreshAsync(gamesToRefresh, onGameStarting, onGameCompleted, cancel);
        }

        public Task<IReadOnlyList<Game>> FilterRefreshTargetsAsync(
            IReadOnlyList<Game> gamesToRefresh,
            CancellationToken cancel)
        {
            return _scanner.FilterOwnedGamesAsync(gamesToRefresh, cancel);
        }

        /// <inheritdoc />
        public IProviderSettings GetSettings() => _providerSettings;

        /// <inheritdoc />
        public void ApplySettings(IProviderSettings settings)
        {
            if (settings is SteamSettings steamSettings)
            {
                _providerSettings.CopyFrom(steamSettings);
            }
        }

        /// <inheritdoc />
        public ProviderSettingsViewBase CreateSettingsView() => new SteamSettingsView(_sessionManager, _api);

        public void Dispose()
        {
            _steamClient?.Dispose();
        }

        private static Guid ResolveSteamPluginId()
        {
            try
            {
                return BuiltinExtensions.GetIdFromExtension(BuiltinExtension.SteamLibrary);
            }
            catch
            {
                return Guid.Parse("CB91DFC9-B977-43BF-8E70-55F46E410FAB");
            }
        }
    }
}
