using Playnite.SDK;
using Playnite.SDK.Models;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Providers.Settings;
using PlayniteAchievements.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.Xbox
{
    /// <summary>
    /// Data provider for Xbox achievement data.
    /// Supports Xbox One/Series X|S, Xbox 360, and PC Game Pass games.
    /// </summary>
    internal sealed class XboxDataProvider : IDataProvider, IDisposable
    {
        // Xbox library plugin ID from Playnite
        internal static readonly Guid XboxLibraryPluginId = Guid.Parse("7e4fbb5b-4594-4c5a-8a69-1e3f41b39c52");

        private readonly XboxSessionManager _sessionManager;
        private readonly XboxScanner _scanner;
        private readonly XboxApiClient _apiClient;
        private readonly ILogger _logger;
        private readonly PlayniteAchievementsSettings _settings;
        private XboxSettings _providerSettings;

        public XboxDataProvider(
            ILogger logger,
            PlayniteAchievementsSettings settings,
            XboxSessionManager sessionManager)
        {
            if (logger == null) throw new ArgumentNullException(nameof(logger));
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (sessionManager == null) throw new ArgumentNullException(nameof(sessionManager));

            _logger = logger;
            _settings = settings;
            _sessionManager = sessionManager;

            _providerSettings = ProviderSettings.Load<XboxSettings>();
            _apiClient = new XboxApiClient(logger, settings.Persisted.GlobalLanguage);
            _scanner = new XboxScanner(settings, _providerSettings, sessionManager, _apiClient, logger);
        }

        public string ProviderName => ResourceProvider.GetString("LOCPlayAch_Provider_Xbox");
        public string ProviderKey => "Xbox";
        public string ProviderIconKey => "ProviderIconXbox";
        public string ProviderColorHex => "#107C10";  // Xbox green

        /// <summary>
        /// Checks if Xbox authentication is properly configured.
        /// </summary>
        public bool IsAuthenticated => _sessionManager.IsAuthenticated;

        /// <summary>
        /// Determines if this provider can handle the specified game.
        /// </summary>
        public bool IsCapable(Game game)
        {
            if (game == null) return false;

            // Console games: GameId = "CONSOLE_{titleId}"
            if (game.GameId?.StartsWith("CONSOLE_") == true)
            {
                return true;
            }

            // Xbox library plugin
            if (game.PluginId == XboxLibraryPluginId)
            {
                return true;
            }

            // Source name matching for PC games
            var source = game.Source?.Name?.Trim();
            if (string.IsNullOrWhiteSpace(source)) return false;

            return string.Equals(source, "Xbox", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(source, "Xbox Game Pass", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(source, "Microsoft Store", StringComparison.OrdinalIgnoreCase);
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
            _apiClient?.Dispose();
        }

        /// <inheritdoc />
        public IProviderSettings GetSettings() => _providerSettings;

        /// <inheritdoc />
        public IProviderSettings CreateDefaultSettings() => new XboxSettings();

        /// <inheritdoc />
        public void ApplySettings(IProviderSettings settings)
        {
            if (settings is XboxSettings xboxSettings)
            {
                _providerSettings = xboxSettings;
                xboxSettings.Save();
            }
        }
    }
}
