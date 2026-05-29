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

namespace PlayniteAchievements.Providers.Epic
{
    public sealed class EpicDataProvider : IDataProvider, IAchievementPageLinkProvider, IDisposable
    {
        private readonly EpicSessionManager _sessionManager;
        private readonly EpicScanner _scanner;
        private readonly HttpClient _httpClient;
        private EpicSettings _providerSettings;

        private static readonly Guid EpicPluginId = ResolveEpicPluginId();
        internal static readonly Guid LegendaryPluginId = Guid.Parse("EAD65C3B-2F8F-4E37-B4E6-B3DE6BE540C6");

        public EpicDataProvider(
            ILogger logger,
            PlayniteAchievementsSettings settings,
            IPlayniteAPI playniteApi)
        {
            if (logger == null) throw new ArgumentNullException(nameof(logger));
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (playniteApi == null) throw new ArgumentNullException(nameof(playniteApi));

            _httpClient = new HttpClient();
            _sessionManager = new EpicSessionManager(playniteApi, logger);

            var apiClient = new EpicApiClient(_httpClient, logger, _sessionManager, settings.Persisted);
            _scanner = new EpicScanner(settings, apiClient, _sessionManager, logger);

            _providerSettings = ProviderRegistry.Settings<EpicSettings>();
        }

        public string ProviderName => ResourceProvider.GetString("LOCPlayAch_Provider_Epic");

        public string ProviderKey => "Epic";

        public string ProviderIconKey => "ProviderIconEpic";

        public string ProviderColorHex => "#26BBFF";

        public bool IsAuthenticated => _sessionManager.IsAuthenticated;

        public ISessionManager AuthSession => _sessionManager;

        public bool IsCapable(Game game) => IsEpicCapable(game);

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
                if (TryGetEpicSlug(link?.Url, out var slug))
                {
                    url = $"https://store.epicgames.com/achievements/{Uri.EscapeDataString(slug)}";
                    return true;
                }
            }

            return false;
        }

        private static bool IsEpicCapable(Game game)
        {
            return game != null && (game.PluginId == EpicPluginId || game.PluginId == LegendaryPluginId);
        }

        internal static bool TryGetEpicSlug(string linkUrl, out string slug)
        {
            slug = null;
            if (string.IsNullOrWhiteSpace(linkUrl) ||
                !Uri.TryCreate(linkUrl.Trim(), UriKind.Absolute, out var uri) ||
                !IsEpicHost(uri.Host))
            {
                return false;
            }

            var segments = uri.AbsolutePath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < segments.Length - 1; i++)
            {
                if (string.Equals(segments[i], "p", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(segments[i], "achievements", StringComparison.OrdinalIgnoreCase))
                {
                    slug = Uri.UnescapeDataString(segments[i + 1]).Trim();
                    return !string.IsNullOrWhiteSpace(slug);
                }
            }

            return false;
        }

        private static bool IsEpicHost(string host)
        {
            return string.Equals(host, "epicgames.com", StringComparison.OrdinalIgnoreCase) ||
                   host?.EndsWith(".epicgames.com", StringComparison.OrdinalIgnoreCase) == true;
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
            if (settings is EpicSettings epicSettings)
            {
                _providerSettings.CopyFrom(epicSettings);
            }
        }

        /// <inheritdoc />
        public ProviderSettingsViewBase CreateSettingsView() => new EpicSettingsView(_sessionManager);

        private static Guid ResolveEpicPluginId()
        {
            try
            {
                return BuiltinExtensions.GetIdFromExtension(BuiltinExtension.EpicLibrary);
            }
            catch
            {
                return Guid.Empty;
            }
        }
    }
}






