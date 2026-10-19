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
    /// provider services. A game links to a trophy set id (NPWR) and its trophies come from the PSN
    /// API in the user's language, so a PSN sign-in is required. Matching tries the sets the user
    /// played first, then reads the NPWR from the game's PSPrices trophy page.
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
            if (!PsnSignedIn)
            {
                return Task.FromResult(false);
            }

            _psPricesRun = _psPrices.BeginRun();
            return Task.FromResult(true);
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

            if (!PsnSignedIn)
            {
                return null;
            }

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
                return null;
            }

            var path = await _psPrices.FindTrophyPagePathAsync(name, ct).ConfigureAwait(false);
            var page = path == null ? null : await _psPrices.GetTrophyPageAsync(path, ct).ConfigureAwait(false);
            return page == null ? null : new MetadataSourceLink(page.NpCommunicationId, page.Title);
        }

        public async Task<AchievementSourceSet> FetchAsync(MetadataLinkRequest request, string linkId, CancellationToken ct)
        {
            if (!PsnSignedIn)
            {
                return null;
            }

            var npCommunicationId = linkId?.Trim();
            if (IsPsPricesPath(npCommunicationId))
            {
                // A user may paste a PSPrices trophy page; it only supplies the NPWR.
                var page = await _psPrices.GetTrophyPageAsync(npCommunicationId, ct).ConfigureAwait(false);
                npCommunicationId = page?.NpCommunicationId;
            }

            if (string.IsNullOrWhiteSpace(npCommunicationId))
            {
                return null;
            }

            try
            {
                var rows = await _psn.Scanner.FetchTrophyDefinitionsAsync(npCommunicationId, ct).ConfigureAwait(false);
                return rows == null || rows.Count == 0 ? null : new AchievementSourceSet(Key, linkId, rows);
            }
            catch (PsnAuthRequiredException)
            {
                _logger?.Debug($"[PlayStationMetadata] PSN sign-in required to fetch {npCommunicationId}.");
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
