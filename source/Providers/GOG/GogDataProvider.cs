using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Providers.Settings;
using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.GOG
{
    /// <summary>
    /// IDataProvider implementation for GOG achievements.
    /// Uses WebView-based authentication and GOG gameplay API.
    /// </summary>
    public sealed class GogDataProvider : IDataProvider, IDisposable
    {
        internal static readonly Guid GogPluginId = Guid.Parse("AEBE8B7C-6DC3-4A66-AF31-E7375C6B5E9E");
        internal static readonly Guid GogOSSPluginId = Guid.Parse("03689811-3F33-4DFB-A121-2EE168FB9A5C");

        private readonly PlayniteAchievementsSettings _settings;
        private readonly GogSessionManager _sessionManager;
        private readonly GogScanner _scanner;
        private readonly HttpClient _httpClient;
        private GogSettings _providerSettings;

        public GogDataProvider(
            ILogger logger,
            PlayniteAchievementsSettings settings,
            IPlayniteAPI playniteApi,
            string pluginUserDataPath,
            GogSessionManager sessionManager)
        {
            if (logger == null) throw new ArgumentNullException(nameof(logger));
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (playniteApi == null) throw new ArgumentNullException(nameof(playniteApi));
            if (string.IsNullOrWhiteSpace(pluginUserDataPath)) throw new ArgumentException("Plugin user data path is required.", nameof(pluginUserDataPath));
            if (sessionManager == null) throw new ArgumentNullException(nameof(sessionManager));

            _settings = settings;
            _httpClient = new HttpClient();
            var clientIdCacheStore = new GogClientIdCacheStore(pluginUserDataPath, logger);
            var apiClient = new GogApiClient(_httpClient, logger, sessionManager, clientIdCacheStore);

            _sessionManager = sessionManager;
            _scanner = new GogScanner(settings, apiClient, sessionManager, logger);

            _providerSettings = ProviderRegistry.Settings<GogSettings>();
        }

        public string ProviderName => ResourceProvider.GetString("LOCPlayAch_Provider_GOG");

        public string ProviderKey => "GOG";

        public string ProviderIconKey => "ProviderIconGOG";

        public string ProviderColorHex => "#A855F7";

        public bool IsAuthenticated => _sessionManager.IsAuthenticated;

        public bool IsCapable(Game game) =>
            IsGogCapable(game);

        public static bool IsGogCapable(Game game)
        {
            return game != null && (game.PluginId == GogPluginId || game.PluginId == GogOSSPluginId);
        }

        public Task<RebuildPayload> RefreshAsync(
            IReadOnlyList<Game> gamesToRefresh,
            Action<Game> onGameStarting,
            Func<Game, GameAchievementData, Task> onGameCompleted,
            CancellationToken cancel)
        {
            return _scanner.RefreshAsync(gamesToRefresh, onGameStarting, onGameCompleted, cancel);
        }

        public void Dispose()
        {
            _httpClient?.Dispose();
        }

        /// <inheritdoc />
        public IProviderSettings GetSettings() => _providerSettings;

        /// <inheritdoc />
        public void ApplySettings(IProviderSettings settings)
        {
            if (settings is GogSettings gogSettings)
            {
                _providerSettings.CopyFrom(gogSettings);
            }
        }

        /// <inheritdoc />
        public ProviderSettingsViewBase CreateSettingsView() => new GogSettingsView(_sessionManager);
    }
}






