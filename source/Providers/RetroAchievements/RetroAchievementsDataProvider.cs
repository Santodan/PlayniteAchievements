using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Providers.RetroAchievements.Hashing;
using PlayniteAchievements.Providers.Settings;
using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.RetroAchievements
{
    internal sealed class RetroAchievementsDataProvider : IDataProvider, IDisposable
    {
        private readonly ILogger _logger;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly string _pluginUserDataPath;
        private readonly RetroAchievementsPathResolver _pathResolver;
        private RetroAchievementsSettings _providerSettings;

        private readonly object _initLock = new object();
        private RetroAchievementsApiClient _apiClient;
        private RetroAchievementsHashIndexStore _hashIndexStore;
        private RetroAchievementsHashCacheStore _hashCacheStore;
        private RetroAchievementsScanner _scanner;

        private string _clientUsername;
        private string _clientApiKey;
        private string _clientLanguage;

        public RetroAchievementsDataProvider(ILogger logger, PlayniteAchievementsSettings settings, IPlayniteAPI playniteApi, string pluginUserDataPath)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _pluginUserDataPath = pluginUserDataPath ?? string.Empty;
            _pathResolver = new RetroAchievementsPathResolver(playniteApi);

            _providerSettings = ProviderRegistry.Settings<RetroAchievementsSettings>();
        }
        public string ProviderName => ResourceProvider.GetString("LOCPlayAch_Provider_RetroAchievements");
        public string ProviderKey => "RetroAchievements";
        public string ProviderIconKey => "ProviderIconRetroAchievements";
        public string ProviderColorHex => "#FFD700";

        /// <summary>
        /// Checks if RetroAchievements authentication is properly configured.
        /// Requires RaUsername and RaWebApiKey to be present.
        /// Does NOT check RetroAchievementsEnabled - that is handled by ProviderRegistry.
        /// </summary>
        public bool IsAuthenticated
        {
            get
            {
                var providerSettings = ProviderRegistry.Settings<RetroAchievementsSettings>();
                return !string.IsNullOrWhiteSpace(providerSettings.RaUsername) &&
                       !string.IsNullOrWhiteSpace(providerSettings.RaWebApiKey);
            }
        }

        public bool IsCapable(Game game)
        {
            if (game == null) return false;

            var providerSettings = ProviderRegistry.Settings<RetroAchievementsSettings>();
            if (string.IsNullOrWhiteSpace(providerSettings.RaUsername) || string.IsNullOrWhiteSpace(providerSettings.RaWebApiKey))
            {
                return false;
            }

            if (!RaConsoleIdResolver.TryResolve(game, out var consoleId))
            {
                return false;
            }

            var hasher = RaHasherFactory.Create(consoleId, _settings, _logger);
            if (hasher == null)
            {
                return false;
            }

            return _pathResolver.ResolveCandidateFilePaths(game).Any(p =>
                !string.IsNullOrWhiteSpace(p) &&
                (File.Exists(p) || ArchiveUtils.IsArchivePath(p)));
        }

        public Task<RebuildPayload> RefreshAsync(
            IReadOnlyList<Game> gamesToRefresh,
            Action<Game> onGameStarting,
            Func<Game, GameAchievementData, Task> onGameCompleted,
            CancellationToken cancel)
        {
            EnsureInitialized();
            return _scanner.RefreshAsync(gamesToRefresh, onGameStarting, onGameCompleted, cancel);
        }

        private void EnsureInitialized()
        {
            var providerSettings = ProviderRegistry.Settings<RetroAchievementsSettings>();
            var username = providerSettings.RaUsername?.Trim() ?? string.Empty;
            var apiKey = providerSettings.RaWebApiKey?.Trim() ?? string.Empty;
            var language = _settings.Persisted.GlobalLanguage?.Trim() ?? string.Empty;

            lock (_initLock)
            {
                if (_scanner != null && string.Equals(username, _clientUsername, StringComparison.Ordinal) &&
                    string.Equals(apiKey, _clientApiKey, StringComparison.Ordinal) &&
                    string.Equals(language, _clientLanguage, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                _apiClient?.Dispose();
                _apiClient = new RetroAchievementsApiClient(_logger, username, apiKey, language);
                _hashIndexStore = new RetroAchievementsHashIndexStore(_logger, _settings, _apiClient, _pluginUserDataPath);
                _hashCacheStore = new RetroAchievementsHashCacheStore(_logger, _pluginUserDataPath);
                _scanner = new RetroAchievementsScanner(_logger, _settings, _apiClient, _hashIndexStore, _pathResolver, _hashCacheStore);

                _clientUsername = username;
                _clientApiKey = apiKey;
                _clientLanguage = language;
            }
        }

        // private bool TryResolveConsoleId(Game game, out int consoleId)
        //     => RaConsoleIdResolver.TryResolve(game, out consoleId);

        public void Dispose()
        {
            _apiClient?.Dispose();
        }

        /// <inheritdoc />
        public IProviderSettings GetSettings() => _providerSettings;

        /// <inheritdoc />
        public void ApplySettings(IProviderSettings settings)
        {
            if (settings is RetroAchievementsSettings raSettings)
            {
                _providerSettings.CopyFrom(raSettings);
            }
        }

        /// <inheritdoc />
        public ProviderSettingsViewBase CreateSettingsView() => new RetroAchievementsSettingsView(_pluginUserDataPath);
    }
}






