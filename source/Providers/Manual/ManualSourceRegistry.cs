using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using Playnite.SDK;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Providers.Exophase;
using PlayniteAchievements.Providers.Steam;

namespace PlayniteAchievements.Providers.Manual
{
    /// <summary>
    /// Owns the manual-link source implementations used by both the Manual provider and manual-link UI.
    /// </summary>
    public sealed class ManualSourceRegistry : IDisposable
    {
        private readonly HttpClient _steamHttpClient;
        private readonly IReadOnlyList<IManualSource> _sources;
        private readonly Dictionary<string, IManualSource> _sourcesByKey;
        private bool _disposed;

        public ManualSourceRegistry(
            ILogger logger,
            PlayniteAchievementsSettings settings,
            IPlayniteAPI playniteApi)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (playniteApi == null) throw new ArgumentNullException(nameof(playniteApi));

            var exophaseSessionManager = new ExophaseSessionManager(playniteApi, logger);

            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                AllowAutoRedirect = true
            };

            _steamHttpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(30)
            };
            _steamHttpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

            _sources = new List<IManualSource>
            {
                new SteamManualSource(
                    _steamHttpClient,
                    logger,
                    () => ProviderRegistry.Settings<SteamSettings>().SteamApiKey),
                new ExophaseManualSource(
                    playniteApi,
                    exophaseSessionManager,
                    logger,
                    () => settings.Persisted.GlobalLanguage)
            }.AsReadOnly();

            _sourcesByKey = _sources
                .Where(source => !string.IsNullOrWhiteSpace(source?.SourceKey))
                .ToDictionary(source => source.SourceKey, source => source, StringComparer.OrdinalIgnoreCase);
        }

        public IReadOnlyList<IManualSource> GetAllSources() => _sources;

        public IManualSource GetSourceByKey(string sourceKey)
        {
            if (string.IsNullOrWhiteSpace(sourceKey))
            {
                return null;
            }

            _sourcesByKey.TryGetValue(sourceKey, out var source);
            return source;
        }

        public IManualSource GetDefaultSource()
        {
            return GetSourceByKey("Steam") ?? _sources.FirstOrDefault();
        }

        public Action<ManualAchievementLink, IList<AchievementDetail>> GetPostProcessorByKey(string sourceKey)
        {
            if (string.Equals(sourceKey, "Exophase", StringComparison.OrdinalIgnoreCase))
            {
                return ExophaseDataProvider.ApplyManualSourceRarity;
            }

            return null;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _steamHttpClient?.Dispose();
        }
    }
}
