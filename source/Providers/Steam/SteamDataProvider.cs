using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Providers.Settings;
using PlayniteAchievements.Services;
using System;
using System.Collections.Generic;
using System.Linq;
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
        private readonly SteamApiClient _steamApiClient;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly SteamSessionManager _sessionManager;
        private SteamSettings _providerSettings;

        public SteamDataProvider(
            ILogger logger,
            PlayniteAchievementsSettings settings,
            IPlayniteAPI api,
            SteamSessionManager sessionManager,
            string pluginUserDataPath)
        {
            if (logger == null) throw new ArgumentNullException(nameof(logger));
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (api == null) throw new ArgumentNullException(nameof(api));
            if (sessionManager == null) throw new ArgumentNullException(nameof(sessionManager));

            _settings = settings;
            _sessionManager = sessionManager;

            // Initialize provider settings from persisted settings dictionary
            _providerSettings = ProviderSettingsHelper.Load<SteamSettings>(settings.Persisted, "Steam");

            // Create Steam-specific dependencies
            _steamClient = new SteamHttpClient(api, logger, _sessionManager, pluginUserDataPath);
            _steamApiClient = new SteamApiClient(_steamClient.ApiHttpClient, logger);

            _scanner = new SteamScanner(settings, _providerSettings, _steamClient, _sessionManager, _steamApiClient, api, logger);
        }

        public string ProviderName => ResourceProvider.GetString("LOCPlayAch_Provider_Steam");
        public string ProviderKey => "Steam";
        public string ProviderIconKey => "ProviderIconSteam";
        public string ProviderColorHex => "#B0B0B0";

        /// <summary>
        /// Checks if Steam authentication is properly configured.
        /// Requires SteamUserId, SteamApiKey, and web session auth (cached SteamId64).
        /// Does NOT check SteamEnabled - that is handled by ProviderRegistry.
        /// </summary>
        public bool IsAuthenticated =>
            !string.IsNullOrWhiteSpace(_providerSettings.SteamUserId) &&
            !string.IsNullOrWhiteSpace(_providerSettings.SteamApiKey) &&
            !string.IsNullOrWhiteSpace(_sessionManager.GetCachedSteamId64());

        public bool IsCapable(Game game) =>
            IsSteamCapable(game);

        public static bool IsSteamCapable(Game game)
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
        public IProviderSettings CreateDefaultSettings() => new SteamSettings();

        /// <inheritdoc />
        public void ApplySettings(IProviderSettings settings)
        {
            if (settings is SteamSettings steamSettings)
            {
                _providerSettings = steamSettings;
                ProviderSettingsHelper.Save(_settings.Persisted, steamSettings);
            }
        }

        public void Dispose()
        {
            _steamClient?.Dispose();
        }
    }
}
