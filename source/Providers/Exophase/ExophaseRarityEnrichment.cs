using PlayniteAchievements.Providers.EA;
using PlayniteAchievements.Providers.RPCS3;
using PlayniteAchievements.Providers.ShadPS4;
using PlayniteAchievements.Providers.Xbox;
using PlayniteAchievements.Providers.Xenia;

namespace PlayniteAchievements.Providers.Exophase
{
    /// <summary>
    /// Maps servicing providers to their Exophase rarity enrichment opt-in. The provider set and
    /// per-provider flag mirror the scanner gates that construct <see cref="ExophaseMetadataEnricher"/>
    /// (e.g. XboxScanner.CreateRarityEnricherAsync); keep them in sync when a provider gains or
    /// loses enrichment support.
    /// </summary>
    internal static class ExophaseRarityEnrichment
    {
        public static bool SupportsProvider(string providerKey)
        {
            switch ((providerKey ?? string.Empty).Trim().ToUpperInvariant())
            {
                case "XBOX":
                case "XENIA":
                case "EA":
                case "RPCS3":
                case "SHADPS4":
                    return true;
                default:
                    return false;
            }
        }

        public static bool IsEnabledForProvider(string providerKey)
        {
            switch ((providerKey ?? string.Empty).Trim().ToUpperInvariant())
            {
                case "XBOX":
                    return ProviderRegistry.Settings<XboxSettings>().UseExophaseForRarity;
                case "XENIA":
                    return ProviderRegistry.Settings<XeniaSettings>().UseExophaseForRarity;
                case "EA":
                    return ProviderRegistry.Settings<EASettings>().UseExophaseForRarity;
                case "RPCS3":
                    return ProviderRegistry.Settings<Rpcs3Settings>().UseExophaseForRarity;
                case "SHADPS4":
                    return ProviderRegistry.Settings<ShadPS4Settings>().UseExophaseForRarity;
                default:
                    return false;
            }
        }
    }
}
