using Playnite.SDK;
using Playnite.SDK.Models;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Providers.Overrides;
using PlayniteAchievements.Providers.Settings;
using PlayniteAchievements.Services.GameCustomData;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.Riot
{
    /// <summary>
    /// Data provider for League of Legends Challenges, Riot's account-wide tiered achievement
    /// system. Riot's other PC titles have no achievement surface of their own: Valorant and 2XKO
    /// expose achievements only on console (covered by the PSN and Xbox providers), Legends of
    /// Runeterra has none, and the Riot Forge titles ship on Steam/GOG/Epic.
    /// </summary>
    internal sealed class RiotDataProvider
        : DataProviderBase<RiotSettings>, IDataProvider, IProviderOverride, IRepeatableUnlockProvider, IDisposable
    {
        // Riot Games Library (third-party Playnite plugin, ASchoe311/RiotGamesLibrary). It imports
        // League, Valorant, Legends of Runeterra and 2XKO under one plugin id, so the game id
        // distinguishes them.
        private static readonly Guid RiotLibraryPluginId = new Guid("91D13C6F-63D3-42ED-A100-6F811A8387EA");

        private const string LeagueGameId = "rg-leagueoflegends";

        /// <summary>
        /// Names used by manually added League entries. Matched only when the game did not come
        /// from the Riot library plugin.
        /// </summary>
        private static readonly string[] LeagueGameNames =
        {
            "League of Legends",
            "Teamfight Tactics"
        };

        /// <summary>
        /// Presence-only override: there is no per-game Riot identifier to enter, since challenges
        /// belong to the account configured in settings rather than to a game.
        /// </summary>
        public ProviderOverrideDescriptor OverrideDescriptor { get; } = ProviderOverrideDescriptor.None();

        private readonly ILogger _logger;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly RiotApiClient _apiClient;
        private readonly RiotChallengeMetadataClient _metadataClient;
        private readonly RiotScanner _scanner;

        public RiotDataProvider(
            ILogger logger,
            PlayniteAchievementsSettings settings,
            string pluginUserDataPath)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));

            _apiClient = new RiotApiClient(logger);
            _metadataClient = new RiotChallengeMetadataClient(logger, pluginUserDataPath);
            _scanner = new RiotScanner(
                logger,
                ProviderSettings,
                _metadataClient,
                () => new RiotWebChallengeSource(
                    logger,
                    _apiClient,
                    ProviderSettings,
                    () => ProviderRegistry.Write(ProviderSettings, persistToDisk: true)),
                () => _settings.Persisted?.GlobalLanguage);
        }

        public string ProviderName => ResourceProvider.GetString("LOCPlayAch_Provider_Riot");
        public string ProviderKey => "Riot";
        public string ProviderIconKey => "ProviderIconRiot";
        public string ProviderColorHex => "#D13639";

        public bool IsAuthenticated => ProviderSettings.HasCredentials;

        // A challenge is earned again each time the player reaches a higher tier, and the Riot API
        // is the single source for when that happened, so a newer unlock time is a real new earn
        // rather than a second reader disagreeing about the first one.
        public bool ReportsRepeatUnlocks => true;

        // The Riot key is entered in settings rather than obtained through a login flow, so there
        // is no live session to probe.
        public ISessionManager AuthSession => null;

        public PlayniteAchievements.Models.Friends.IFriendsProvider Friends => null;

        public bool IsCapable(Game game)
        {
            if (game == null || game.Id == Guid.Empty)
            {
                return false;
            }

            if (!ProviderSettings.IsEnabled)
            {
                return false;
            }

            if (GameCustomDataLookup.TryGetProviderOverrideValue(game.Id, ProviderKey, out _))
            {
                return true;
            }

            if (game.PluginId == RiotLibraryPluginId)
            {
                return string.Equals(game.GameId?.Trim(), LeagueGameId, StringComparison.OrdinalIgnoreCase);
            }

            return IsLeagueGameName(game.Name);
        }

        private static bool IsLeagueGameName(string name)
        {
            var trimmed = name?.Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                return false;
            }

            foreach (var candidate in LeagueGameNames)
            {
                if (string.Equals(trimmed, candidate, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        public Task<RebuildPayload> RefreshAsync(
            IReadOnlyList<Game> gamesToRefresh,
            Action<Game> onGameStarting,
            Func<Game, GameAchievementData, Task> onGameCompleted,
            CancellationToken cancel)
        {
            return _scanner.RefreshAsync(gamesToRefresh, onGameStarting, onGameCompleted, cancel);
        }

        public ProviderSettingsViewBase CreateSettingsView() => new RiotSettingsView(ResolveAccountAsync);

        /// <summary>
        /// Validates the credentials in the settings view's working copy against Riot and caches the
        /// resolved PUUID on that copy, so committing the edit session carries it through.
        /// </summary>
        private async Task<string> ResolveAccountAsync(RiotSettings settings, CancellationToken cancel)
        {
            var account = await _apiClient
                .GetAccountByRiotIdAsync(
                    settings.PlatformRegion,
                    settings.RiotGameName,
                    settings.RiotTagLine,
                    settings.RiotApiKey,
                    cancel)
                .ConfigureAwait(false);

            settings.Puuid = account.Puuid;

            return string.IsNullOrWhiteSpace(account.GameName)
                ? settings.RiotId
                : $"{account.GameName}#{account.TagLine}";
        }

        public void Dispose()
        {
            _apiClient?.Dispose();
            _metadataClient?.Dispose();
        }
    }
}
