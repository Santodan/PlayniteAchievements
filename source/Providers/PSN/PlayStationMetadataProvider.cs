using Playnite.SDK;
using PlayniteAchievements.Models;
using PlayniteAchievements.Providers.Metadata;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.PSN
{
    /// <summary>
    /// PlayStation trophy icons (512px for PS5, 240px for PS4) and platinum art for games another
    /// provider services. A game links to either a trophy set id (NPWR, fetched from the PSN API)
    /// or a PSPrices trophy page path (read from the page, which links Sony's full-size icons, so
    /// no PSN sign-in is needed). Matching tries the sets the user played first, then PSPrices.
    /// </summary>
    internal sealed class PlayStationMetadataProvider : IMetadataProvider
    {
        public const string MetadataKey = "PlayStation";

        private static readonly HashSet<string> NativeProviders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "PSN", "RPCS3", "ShadPS4", "Vita3K"
        };

        private readonly PsnDataProvider _psn;
        private readonly PsPricesClient _psPrices;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly ILogger _logger;
        private IDisposable _psPricesRun;

        public PlayStationMetadataProvider(
            PsnDataProvider psn,
            IPlayniteAPI api,
            PlayniteAchievementsSettings settings,
            ILogger logger)
        {
            _psn = psn;
            _psPrices = new PsPricesClient(api, logger);
            _settings = settings;
            _logger = logger;
        }

        public string Key => MetadataKey;

        public string DisplayNameKey => "LOCPlayAch_Metadata_Source_PlayStation";

        public MetadataField SupportedFields => MetadataField.Icons | MetadataField.CapstoneArt;

        public MetadataCategoryMode CategoryMode => MetadataCategoryMode.Off;

        private bool PsnSignedIn => _psn?.Scanner != null && _psn.IsAuthenticated;

        public bool IsNativeFor(string servicingProviderKey) =>
            !string.IsNullOrWhiteSpace(servicingProviderKey) && NativeProviders.Contains(servicingProviderKey);

        public Task<bool> BeginRunAsync(CancellationToken ct)
        {
            _psPricesRun = _psPrices.BeginRun();
            return Task.FromResult(PsnSignedIn || _psPrices.IsAvailable);
        }

        public void EndRun()
        {
            Interlocked.Exchange(ref _psPricesRun, null)?.Dispose();
        }

        public async Task<MetadataSourceLink> ResolveLinkAsync(MetadataLinkRequest request, CancellationToken ct)
        {
            var name = request?.Game?.Name;
            if (string.IsNullOrWhiteSpace(name) || _settings?.Persisted?.MetadataAllowNameSearch == false)
            {
                return null;
            }

            if (PsnSignedIn)
            {
                try
                {
                    var played = await _psn.Scanner.FindPlayedTrophySetByNameAsync(name, ct).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(played?.NpCommunicationId))
                    {
                        return new MetadataSourceLink(played.NpCommunicationId, played.Title);
                    }
                }
                catch (PsnAuthRequiredException)
                {
                    _logger?.Debug($"[PlayStationMetadata] PSN sign-in expired while matching '{name}'.");
                }
            }

            var path = await _psPrices.FindTrophyPagePathAsync(name, ct).ConfigureAwait(false);
            if (path == null)
            {
                return null;
            }

            var page = await _psPrices.GetTrophyPageAsync(path, ct).ConfigureAwait(false);
            var label = page == null ? null : $"{page.Title} · {page.NpCommunicationId}";
            return new MetadataSourceLink(path, label);
        }

        public async Task<AchievementSourceSet> FetchAsync(MetadataLinkRequest request, string linkId, CancellationToken ct)
        {
            if (IsPsPricesPath(linkId))
            {
                var page = await _psPrices.GetTrophyPageAsync(linkId.Trim(), ct).ConfigureAwait(false);
                return page == null || page.Trophies.Count == 0
                    ? null
                    : new AchievementSourceSet(Key, linkId, page.Trophies);
            }

            if (!PsnSignedIn)
            {
                _logger?.Debug($"[PlayStationMetadata] {linkId} needs a PSN sign-in; link a PSPrices trophy page instead to use it signed out.");
                return null;
            }

            try
            {
                var rows = await _psn.Scanner.FetchTrophyDefinitionsAsync(linkId, ct).ConfigureAwait(false);
                return rows == null || rows.Count == 0 ? null : new AchievementSourceSet(Key, linkId, rows);
            }
            catch (PsnAuthRequiredException)
            {
                _logger?.Debug($"[PlayStationMetadata] PSN sign-in required to fetch {linkId}.");
                return null;
            }
        }

        /// <summary>A link entered as a PSPrices trophy page (path or full URL) rather than an NPWR id.</summary>
        internal static bool IsPsPricesPath(string linkId)
        {
            return !string.IsNullOrWhiteSpace(linkId) &&
                   linkId.IndexOf("/game-trophies/", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
