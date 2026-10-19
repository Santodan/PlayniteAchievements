using Playnite.SDK.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Providers.Xbox;

namespace PlayniteAchievements.Providers.Exophase
{
    /// <summary>
    /// Exophase rarity enrichment helpers. Whether a platform uses it is
    /// <see cref="PersistedSettings.UsesExophaseRarity"/>.
    /// </summary>
    internal static class ExophaseRarityEnrichment
    {

        /// <summary>
        /// The name-derived slug the enricher would try for this game, mirroring
        /// <see cref="ExophaseMetadataEnricher"/>'s default-slug generation and the scanners'
        /// platform slug choices. PSN slugs carry no platform suffix, so RPCS3/ShadPS4/Vita3K candidates
        /// are the bare normalized name. RPCS3 multipack collections (a '+'-joined provider game
        /// key) have no collection-level Exophase page and enrich per trophy set instead, so no
        /// candidate is offered.
        /// </summary>
        public static string GetCandidateSlug(string providerKey, Game game, string providerGameKey = null)
        {
            if (game == null || string.IsNullOrWhiteSpace(game.Name))
            {
                return null;
            }

            if (providerGameKey?.IndexOf('+') >= 0)
            {
                return null;
            }

            var normalizedName = ExophaseGameNameMatcher.NormalizeGameNameForSlug(game.Name);
            if (string.IsNullOrWhiteSpace(normalizedName))
            {
                return null;
            }

            switch ((providerKey ?? string.Empty).Trim().ToUpperInvariant())
            {
                case "XBOX":
                    return $"{normalizedName}-{(XboxScanner.IsXbox360Game(game) ? "xbox-360" : "xbox")}";
                case "XENIA":
                    return $"{normalizedName}-xbox-360";
                case "EA":
                    return $"{normalizedName}-origin";
                case "RPCS3":
                case "SHADPS4":
                case "VITA3K":
                    return normalizedName;
                default:
                    return null;
            }
        }
    }
}
