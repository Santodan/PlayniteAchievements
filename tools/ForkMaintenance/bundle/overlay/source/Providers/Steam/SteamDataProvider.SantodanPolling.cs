using PlayniteAchievements.Providers.Metadata;
using System;
using Playnite.SDK;
using PlayniteAchievements.Providers.Local;
using PlayniteAchievements.Services.InGameMonitoring;

namespace PlayniteAchievements.Providers.Steam
{
    internal sealed partial class SteamDataProvider
    {
        private Lazy<SteamHuntersCategoryEnricher> _santodanMetadata;
        private static bool UseSantodanMetadata => SantodanPollingScope.IsActive &&
            ProviderRegistry.Settings<LocalSettings>()?.UseCustomPollingMetadataCache == true;

        private void InitializeSantodanMetadataCache(SteamHuntersApiClient client, ILogger logger,
            SteamCategoryArtDownloader downloader)
        {
            _santodanMetadata = new Lazy<SteamHuntersCategoryEnricher>(
                () => new SteamHuntersCategoryEnricher(client, logger, downloader));
        }

        private SteamHuntersCategoryEnricher GetSantodanExternalCategoryEnricher() =>
            UseSantodanMetadata ? _santodanMetadata.Value : _steamHuntersCategoryEnricher;

        private void ResetSantodanMetadataCache()
        {
            if (!UseSantodanMetadata && _santodanMetadata?.IsValueCreated == true)
                _santodanMetadata.Value.ClearCache();
        }
    }
}
