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
    /// provider services. A game is linked to a trophy set (NPWR id); the trophy list is
    /// title-scoped, so any set can be fetched once its id is known, played or not.
    /// </summary>
    internal sealed class PlayStationMetadataProvider : IMetadataProvider
    {
        public const string MetadataKey = "PlayStation";

        private static readonly HashSet<string> NativeProviders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "PSN", "RPCS3", "ShadPS4", "Vita3K"
        };

        private readonly PsnDataProvider _psn;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly ILogger _logger;

        public PlayStationMetadataProvider(PsnDataProvider psn, PlayniteAchievementsSettings settings, ILogger logger)
        {
            _psn = psn;
            _settings = settings;
            _logger = logger;
        }

        public string Key => MetadataKey;

        public string DisplayNameKey => "LOCPlayAch_Metadata_Source_PlayStation";

        public MetadataField SupportedFields => MetadataField.Icons | MetadataField.CapstoneArt;

        public MetadataCategoryMode CategoryMode => MetadataCategoryMode.Off;

        public bool IsNativeFor(string servicingProviderKey) =>
            !string.IsNullOrWhiteSpace(servicingProviderKey) && NativeProviders.Contains(servicingProviderKey);

        public Task<bool> BeginRunAsync(CancellationToken ct)
        {
            return Task.FromResult(_psn?.Scanner != null && _psn.IsAuthenticated);
        }

        public void EndRun()
        {
        }

        /// <summary>
        /// Matches the game to a trophy set the user has played under the same name. Sets the user
        /// never played need a link entered on the game until a name-to-NPWR source is added.
        /// </summary>
        public async Task<MetadataSourceLink> ResolveLinkAsync(MetadataLinkRequest request, CancellationToken ct)
        {
            var name = request?.Game?.Name;
            if (string.IsNullOrWhiteSpace(name) || _settings?.Persisted?.MetadataAllowNameSearch == false)
            {
                return null;
            }

            try
            {
                var set = await _psn.Scanner.FindPlayedTrophySetByNameAsync(name, ct).ConfigureAwait(false);
                return string.IsNullOrWhiteSpace(set?.NpCommunicationId)
                    ? null
                    : new MetadataSourceLink(set.NpCommunicationId, set.Title);
            }
            catch (PsnAuthRequiredException)
            {
                _logger?.Debug($"[PlayStationMetadata] PSN sign-in required to match '{name}'.");
                return null;
            }
        }

        public async Task<AchievementSourceSet> FetchAsync(MetadataLinkRequest request, string linkId, CancellationToken ct)
        {
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
    }
}
