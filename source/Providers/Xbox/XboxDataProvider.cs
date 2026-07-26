using Playnite.SDK;
using Playnite.SDK.Models;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Providers.Overrides;
using PlayniteAchievements.Providers.Settings;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.GameCustomData;
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
    internal sealed class XboxDataProvider : DataProviderBase<XboxSettings>, IDataProvider, IProviderOverride, IDisposable
    {
        public ProviderOverrideDescriptor OverrideDescriptor { get; } = ProviderOverrideDescriptor.Text(
            "LOCPlayAch_ManageAchievements_Overrides_ProviderValueLabel_Xbox",
            raw => XboxTitleIdResolver.TryNormalizeTitleId(raw, out var titleId)
                ? ProviderOverrideValidation.Valid(titleId)
                : ProviderOverrideValidation.Invalid(
                    "LOCPlayAch_Menu_XboxTitleId_InvalidId"));

        // Xbox library plugin ID from Playnite
        internal static readonly Guid XboxLibraryPluginId = Guid.Parse("7e4fbb5e-2ae3-48d4-8ba0-6b30e7a4e287");

        private readonly XboxSessionManager _sessionManager;
        private readonly XboxScanner _scanner;
        private readonly XboxApiClient _apiClient;

        public XboxDataProvider(
            ILogger logger,
            PlayniteAchievementsSettings settings,
            IPlayniteAPI playniteApi,
            string pluginUserDataPath)
        {
            if (logger == null) throw new ArgumentNullException(nameof(logger));
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (playniteApi == null) throw new ArgumentNullException(nameof(playniteApi));
            if (string.IsNullOrWhiteSpace(pluginUserDataPath)) throw new ArgumentException("Plugin user data path is required.", nameof(pluginUserDataPath));

            _sessionManager = new XboxSessionManager(playniteApi, logger, pluginUserDataPath);

            _apiClient = new XboxApiClient(logger, settings.Persisted.GlobalLanguage);
            _scanner = new XboxScanner(settings, ProviderSettings, _sessionManager, _apiClient, logger, playniteApi, pluginUserDataPath);
        }

        public string ProviderName => ResourceProvider.GetString("LOCPlayAch_Provider_Xbox");
        public string ProviderKey => "Xbox";
        public string ProviderIconKey => "ProviderIconXbox";
        public string ProviderColorHex => "#107C10";  // Xbox green

        /// <summary>
        /// Checks if Xbox authentication is properly configured.
        /// </summary>
        public bool IsAuthenticated => _sessionManager.IsAuthenticated;

        public ISessionManager AuthSession => _sessionManager;

        public PlayniteAchievements.Models.Friends.IFriendsProvider Friends => null;

        /// <summary>
        /// Determines if this provider can handle the specified game.
        /// </summary>
        public bool IsCapable(Game game)
        {
            if (game == null) return false;

            // A per-game override forces this game to be treated as Xbox.
            if (GameCustomDataLookup.TryGetProviderOverrideValue(game.Id, "Xbox", out _))
            {
                return true;
            }

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

            // Game Pass platform: catches games from third-party importers (e.g. Game Pass
            // Catalog Browser) or customized libraries whose Source was renamed away from the
            // strings below. Title ID still resolves via the PFN GameId in the scanner.
            if (game.Platforms?.Any(p =>
                    p?.Name?.IndexOf("Game Pass", StringComparison.OrdinalIgnoreCase) >= 0) == true)
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
        public ProviderSettingsViewBase CreateSettingsView() => new XboxSettingsView(_sessionManager);
    }
}






