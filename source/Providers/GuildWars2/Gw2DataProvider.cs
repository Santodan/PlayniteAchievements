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
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.GuildWars2
{
    /// <summary>
    /// Data provider for Guild Wars 2 achievements, read from ArenaNet's official v2 API. The API
    /// is account-wide and keyed on a personal access token the player creates at account.arena.net;
    /// it publishes no unlock timestamps and no global completion rates, so neither is reported.
    /// </summary>
    internal sealed class Gw2DataProvider : DataProviderBase<Gw2Settings>, IDataProvider, IProviderOverride, IDisposable
    {
        /// <summary>
        /// Presence-only override: there is no per-game Guild Wars 2 identifier to enter, since
        /// achievements belong to the account configured in settings rather than to a game.
        /// </summary>
        public ProviderOverrideDescriptor OverrideDescriptor { get; } = ProviderOverrideDescriptor.None();

        private readonly ILogger _logger;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly Gw2ApiClient _apiClient;
        private readonly Gw2CatalogCache _catalogCache;
        private readonly Gw2Scanner _scanner;

        public Gw2DataProvider(
            ILogger logger,
            PlayniteAchievementsSettings settings,
            string pluginUserDataPath)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));

            _apiClient = new Gw2ApiClient(logger);
            _catalogCache = new Gw2CatalogCache(logger, pluginUserDataPath);
            _scanner = new Gw2Scanner(
                logger,
                ProviderSettings,
                _apiClient,
                _catalogCache,
                () => _settings.Persisted?.GlobalLanguage);
        }

        public string ProviderName => ResourceProvider.GetString("LOCPlayAch_Provider_GW2");
        public string ProviderKey => "GW2";
        public string ProviderIconKey => "ProviderIconGW2";
        public string ProviderColorHex => "#B5121B";

        public bool IsAuthenticated => ProviderSettings.HasCredentials;

        // The key is entered in settings rather than obtained through a login flow, so there is no
        // live session to probe.
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

            return Gw2Parsing.IsGuildWars2Title(game.Name);
        }

        public Task<RebuildPayload> RefreshAsync(
            IReadOnlyList<Game> gamesToRefresh,
            Action<Game> onGameStarting,
            Func<Game, GameAchievementData, Task> onGameCompleted,
            CancellationToken cancel)
        {
            return _scanner.RefreshAsync(gamesToRefresh, onGameStarting, onGameCompleted, cancel);
        }

        public ProviderSettingsViewBase CreateSettingsView() => new Gw2SettingsView(ValidateKeyAsync);

        /// <summary>
        /// Validates the key in the settings view's working copy and caches the resolved account on
        /// that copy, so committing the edit session carries it through. Returns the account name to
        /// show on the card.
        /// </summary>
        private async Task<string> ValidateKeyAsync(Gw2Settings settings, CancellationToken cancel)
        {
            // tokeninfo first: a key can be perfectly valid and still be missing the one scope that
            // makes it useful here, and that reads as an empty achievement list rather than an error.
            var tokenInfo = await _apiClient.GetTokenInfoAsync(settings.ApiKey, cancel).ConfigureAwait(false);

            var hasProgression = tokenInfo?.Permissions?
                .Any(p => string.Equals(p, Gw2ApiClient.ProgressionPermission, StringComparison.OrdinalIgnoreCase)) == true;

            if (!hasProgression)
            {
                throw new Gw2MissingPermissionException(
                    Gw2ApiClient.ProgressionPermission,
                    "The Guild Wars 2 API key does not grant the progression scope.");
            }

            var account = await _apiClient.GetAccountAsync(settings.ApiKey, cancel).ConfigureAwait(false);

            settings.AccountId = account?.Id;
            settings.AccountName = account?.Name;

            return account?.Name;
        }

        public void Dispose()
        {
            _apiClient?.Dispose();
        }
    }
}
