using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Playnite.SDK;
using Playnite.SDK.Models;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Providers.Overrides;
using PlayniteAchievements.Providers.Settings;

namespace PlayniteAchievements.Providers.BattleNet
{
    public sealed class BattleNetDataProvider : DataProviderBase<BattleNetSettings>, IDataProvider, IProviderOverride, IDisposable
    {
        internal static readonly Guid BattleNetPluginId = BattleNetGameSupport.BattleNetPluginId;

        public ProviderOverrideDescriptor OverrideDescriptor { get; } = ProviderOverrideDescriptor.Choice(
            "LOCPlayAch_ManageAchievements_Overrides_ProviderValueLabel_BattleNet",
            new[]
            {
                new ProviderOverrideChoice(BattleNetGameTitle.Wow.ToString(), "World of Warcraft"),
                new ProviderOverrideChoice(BattleNetGameTitle.Sc2.ToString(), "StarCraft II")
            },
            "LOCPlayAch_ManageAchievements_Overrides_ProviderInvalidChoice");

        private readonly BattleNetApiClient _apiClient;
        private readonly BattleNetSessionManager _sessionManager;
        private readonly DataForAzerothSessionManager _dataForAzerothSession;
        private readonly BattleNetScanner _scanner;
        private readonly ILogger _logger;

        public BattleNetDataProvider(
            ILogger logger,
            PlayniteAchievementsSettings settings,
            IPlayniteAPI playniteApi,
            string pluginUserDataPath = null)
        {
            if (logger == null) throw new ArgumentNullException(nameof(logger));
            if (settings == null) throw new ArgumentNullException(nameof(settings));

            _logger = logger;
            _apiClient = new BattleNetApiClient(logger, playniteApi);
            _sessionManager = new BattleNetSessionManager(playniteApi, _apiClient, logger);
            if (playniteApi != null)
            {
                _dataForAzerothSession = new DataForAzerothSessionManager(playniteApi, _apiClient, logger);
            }
            _scanner = new BattleNetScanner(
                _apiClient,
                _sessionManager,
                settings,
                logger,
                pluginUserDataPath);
        }

        public string ProviderName => ResourceProvider.GetString("LOCPlayAch_Provider_BattleNet");
        public string ProviderKey => "BattleNet";
        public string ProviderIconKey => "ProviderIconBattleNet";
        public string ProviderColorHex => "#14B8A6";

        public bool IsAuthenticated => true;

        // Intentionally null. Battle.net achievements come from the user's own OAuth credentials via
        // BattleNetSessionManager, which the scanner drives directly; and the Data for Azeroth site
        // session must never surface here, because the refresh pipeline drops providers whose
        // AuthSession does not probe clean. A third-party site's bot check would then stop WoW and SC2
        // achievements syncing altogether, when all it withholds is a rarity percentage.
        public ISessionManager AuthSession => null;

        public PlayniteAchievements.Models.Friends.IFriendsProvider Friends => null;

        public bool IsCapable(Game game) =>
            BattleNetGameSupport.IsSupported(game, ProviderSettings);

        public Task<RebuildPayload> RefreshAsync(
            IReadOnlyList<Game> gamesToRefresh,
            Action<Game> onGameStarting,
            Func<Game, GameAchievementData, Task> onGameCompleted,
            CancellationToken cancel)
        {
            return _scanner.RefreshAsync(gamesToRefresh, onGameStarting, onGameCompleted, cancel);
        }

        public ProviderSettingsViewBase CreateSettingsView()
        {
            return new BattleNetSettingsView(_apiClient, _sessionManager, _dataForAzerothSession, _logger);
        }

        public void Dispose()
        {
            _apiClient?.Dispose();
        }

        private static string Bool(bool value) => value ? "true" : "false";

        private static string MaskId(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "<empty>";
            }

            var trimmed = value.Trim();
            if (trimmed.Length <= 4)
            {
                return "****";
            }

            return $"{new string('*', Math.Min(8, trimmed.Length - 4))}{trimmed.Substring(trimmed.Length - 4)}";
        }

        private static string Presence(string value) => string.IsNullOrWhiteSpace(value) ? "missing" : "set";

        private static string SettingsSummary(BattleNetSettings settings)
        {
            if (settings == null)
            {
                return "<null settings>";
            }

            return string.Format(
                "enabled={0}, apiClientId={1}, apiClientSecret={2}, sc2Region={3}, sc2Realm={4}, sc2Profile={5}, wowRegion={6}, wowRealmSlug={7}, wowCharacter={8}, useDataForAzerothForWowRarity={9}",
                Bool(settings.IsEnabled),
                Presence(settings.BattleNetClientId),
                Presence(settings.BattleNetClientSecret),
                settings.Sc2RegionId,
                settings.Sc2RealmId,
                settings.Sc2ProfileId > 0 ? MaskId(settings.Sc2ProfileId.ToString()) : "<none>",
                string.IsNullOrWhiteSpace(settings.WowRegion) ? "<none>" : settings.WowRegion,
                string.IsNullOrWhiteSpace(settings.WowRealmSlug) ? "<none>" : settings.WowRealmSlug,
                Presence(settings.WowCharacter),
                Bool(settings.UseDataForAzerothForWowRarity));
        }
    }
}
