using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Providers.Settings;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Playnite.SDK;
using Playnite.SDK.Models;

namespace PlayniteAchievements.Providers.Steam
{
    internal sealed class SteamDataProvider : IDataProvider, IDisposable
    {
        internal static readonly Guid SteamPluginId = Guid.Parse("CB91DFC9-B977-43BF-8E70-55F46E410FAB");

        private readonly SteamHttpClient _steamClient;
        private readonly SteamScanner _scanner;
        private readonly SteamSessionManager _sessionManager;
        private SteamSettings _providerSettings;

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

            // Initialize provider settings from persisted settings dictionary
            _providerSettings = ProviderRegistry.Settings<SteamSettings>();

            // Create Steam-specific dependencies
            _steamClient = new SteamHttpClient(api, logger, _sessionManager, pluginUserDataPath);
            var steamApiClient = new SteamApiClient(_steamClient.ApiHttpClient, logger);
            _scanner = new SteamScanner(settings, _providerSettings, _steamClient, _sessionManager, steamApiClient, api, logger);
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

        public Task<RebuildPayload> RefreshAsync(
            IReadOnlyList<Game> gamesToRefresh,
            Action<Game> onGameStarting,
            Func<Game, GameAchievementData, Task> onGameCompleted,
            CancellationToken cancel)
        {
            return _scanner.RefreshAsync(gamesToRefresh, onGameStarting, onGameCompleted, cancel);
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
        public ProviderSettingsViewBase CreateSettingsView() => new SteamSettingsView(_sessionManager);

        public void Dispose()
        {
            _steamClient?.Dispose();
        }
    }
}
