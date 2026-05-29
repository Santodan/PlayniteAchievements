using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
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
    public sealed class GogDataProvider : IDataProvider, IAchievementPageLinkProvider, IDisposable
    {
        internal static readonly Guid GogPluginId = Guid.Parse("AEBE8B7C-6DC3-4A66-AF31-E7375C6B5E9E");
        internal static readonly Guid GogOSSPluginId = Guid.Parse("03689811-3F33-4DFB-A121-2EE168FB9A5C");

        private readonly GogSessionManager _sessionManager;
        private readonly GogScanner _scanner;
        private readonly HttpClient _httpClient;
        private GogSettings _providerSettings;

        public GogDataProvider(
            ILogger logger,
            PlayniteAchievementsSettings settings,
            IPlayniteAPI playniteApi,
            string pluginUserDataPath)
        {
            if (logger == null) throw new ArgumentNullException(nameof(logger));
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (playniteApi == null) throw new ArgumentNullException(nameof(playniteApi));
            if (string.IsNullOrWhiteSpace(pluginUserDataPath)) throw new ArgumentException("Plugin user data path is required.", nameof(pluginUserDataPath));

            _httpClient = new HttpClient();
            _sessionManager = new GogSessionManager(playniteApi, logger);

            var clientIdCacheStore = new GogClientIdCacheStore(pluginUserDataPath, logger);
            var apiClient = new GogApiClient(_httpClient, logger, _sessionManager, clientIdCacheStore);
            _scanner = new GogScanner(settings, apiClient, _sessionManager, logger);

            _providerSettings = ProviderRegistry.Settings<GogSettings>();
        }

        public string ProviderName => ResourceProvider.GetString("LOCPlayAch_Provider_GOG");

        public string ProviderKey => "GOG";

        public string ProviderIconKey => "ProviderIconGOG";

        public string ProviderColorHex => "#A855F7";

        public bool IsAuthenticated => _sessionManager.IsAuthenticated;

        public ISessionManager AuthSession => _sessionManager;

        public bool IsCapable(Game game) =>
            IsGogCapable(game);

        public bool CanResolveAchievementPageUrl(AchievementPageLinkContext context)
        {
            return TryBuildAchievementPageUrl(context, out _);
        }

        public Task<string> GetAchievementPageUrlAsync(
            AchievementPageLinkContext context,
            CancellationToken cancel)
        {
            return Task.FromResult(
                TryBuildAchievementPageUrl(context, out var url)
                    ? url
                    : null);
        }

        internal static bool TryBuildAchievementPageUrl(
            AchievementPageLinkContext context,
            out string url)
        {
            url = null;
            var links = context?.Game?.Links;
            if (links == null)
            {
                return false;
            }

            foreach (var link in links)
            {
                if (TryGetGogSlug(link?.Url, out var slug))
                {
                    url = $"https://www.gog.com/en/game/{Uri.EscapeDataString(slug)}";
                    return true;
                }
            }

            return false;
        }

        private static bool IsGogCapable(Game game)
        {
            return game != null && (game.PluginId == GogPluginId || game.PluginId == GogOSSPluginId);
        }

        internal static bool TryGetGogSlug(string linkUrl, out string slug)
        {
            slug = null;
            if (string.IsNullOrWhiteSpace(linkUrl) ||
                !Uri.TryCreate(linkUrl.Trim(), UriKind.Absolute, out var uri) ||
                !IsGogHost(uri.Host))
            {
                return false;
            }

            var segments = uri.AbsolutePath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < segments.Length - 1; i++)
            {
                if (string.Equals(segments[i], "game", StringComparison.OrdinalIgnoreCase))
                {
                    slug = Uri.UnescapeDataString(segments[i + 1]).Trim();
                    return !string.IsNullOrWhiteSpace(slug);
                }
            }

            return false;
        }

        private static bool IsGogHost(string host)
        {
            return string.Equals(host, "gog.com", StringComparison.OrdinalIgnoreCase) ||
                   host?.EndsWith(".gog.com", StringComparison.OrdinalIgnoreCase) == true;
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
