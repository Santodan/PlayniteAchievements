using Playnite.SDK;
using Playnite.SDK.Models;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Providers.Settings;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.Hoyoverse
{
    internal sealed class HoyoverseDataProvider : IDataProvider, IDisposable
    {
        internal const string Key = "Hoyoverse";
        internal const string IconKey = "ProviderIconHoyoverse";
        internal const string ColorHex = "#D4ACF8";

        private readonly ILogger _logger;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly IPlayniteAPI _playniteApi;
        private readonly string _pluginUserDataPath;
        private readonly HttpClient _httpClient;
        private HoyoverseSettings _providerSettings;

        public HoyoverseDataProvider(ILogger logger, PlayniteAchievementsSettings settings, IPlayniteAPI playniteApi)
            : this(logger, settings, playniteApi, string.Empty)
        {
        }

        public HoyoverseDataProvider(ILogger logger, PlayniteAchievementsSettings settings, IPlayniteAPI playniteApi, string pluginUserDataPath)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _playniteApi = playniteApi;
            _pluginUserDataPath = pluginUserDataPath ?? string.Empty;
            _providerSettings = ProviderRegistry.Settings<HoyoverseSettings>();
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        }

        public string ProviderName
        {
            get
            {
                var value = ResourceProvider.GetString("LOCPlayAch_Provider_Hoyoverse");
                return string.IsNullOrWhiteSpace(value) ? "HoYoverse" : value;
            }
        }

        public string ProviderKey => Key;

        public string ProviderIconKey => IconKey;

        public string ProviderColorHex => ColorHex;

        public bool IsAuthenticated => true;

        public ISessionManager AuthSession => null;

        public bool IsCapable(Game game)
        {
            return HoyoverseGameCatalog.TryResolve(game, _providerSettings, out _);
        }

        public Task<RebuildPayload> RefreshAsync(
            IReadOnlyList<Game> gamesToRefresh,
            Action<Game> onGameStarting,
            Func<Game, GameAchievementData, Task> onGameCompleted,
            CancellationToken cancel)
        {
            var scanner = new HoyoverseScanner(
                _logger,
                _settings,
                _providerSettings,
                _pluginUserDataPath,
                new HoyoverseDefinitionClient(_httpClient, _logger, _pluginUserDataPath));

            return scanner.RefreshAsync(gamesToRefresh, onGameStarting, onGameCompleted, cancel);
        }

        public IProviderSettings GetSettings() => _providerSettings;

        public void ApplySettings(IProviderSettings settings)
        {
            if (settings is HoyoverseSettings hoyoverseSettings)
            {
                _providerSettings.CopyFrom(hoyoverseSettings);
            }
        }

        public ProviderSettingsViewBase CreateSettingsView()
        {
#if TEST
            return null;
#else
            return new HoyoverseSettingsView(_playniteApi);
#endif
        }

        public void Dispose()
        {
            _httpClient.Dispose();
        }
    }

    public sealed class HoyoverseSettings : ProviderSettingsBase
    {
        private bool _enableGenshinImpact = true;
        private bool _enableHonkaiStarRail = true;
        private bool _enableZenlessZoneZero = true;
        private string _genshinExportPath;
        private string _honkaiStarRailExportPath;
        private string _zenlessZoneZeroExportPath;

        public override string ProviderKey => HoyoverseDataProvider.Key;

        public bool EnableGenshinImpact
        {
            get => _enableGenshinImpact;
            set => SetValue(ref _enableGenshinImpact, value);
        }

        public bool EnableHonkaiStarRail
        {
            get => _enableHonkaiStarRail;
            set => SetValue(ref _enableHonkaiStarRail, value);
        }

        public bool EnableZenlessZoneZero
        {
            get => _enableZenlessZoneZero;
            set => SetValue(ref _enableZenlessZoneZero, value);
        }

        public string GenshinExportPath
        {
            get => _genshinExportPath;
            set => SetValue(ref _genshinExportPath, value);
        }

        public string HonkaiStarRailExportPath
        {
            get => _honkaiStarRailExportPath;
            set => SetValue(ref _honkaiStarRailExportPath, value);
        }

        public string ZenlessZoneZeroExportPath
        {
            get => _zenlessZoneZeroExportPath;
            set => SetValue(ref _zenlessZoneZeroExportPath, value);
        }
    }
}
