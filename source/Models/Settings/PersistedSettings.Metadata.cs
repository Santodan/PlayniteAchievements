using PlayniteAchievements.Providers.Metadata;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Models.Settings
{
    /// <summary>
    /// Which metadata source supplies each field to a platform's games. In an override, a null field
    /// follows the defaults; an empty string means the platform's own data (or none, for categories).
    /// </summary>
    public sealed class MetadataChoices
    {
        public const string SteamHunters = "SteamHunters";
        public const string Exophase = "Exophase";

        /// <summary>Platforms whose scanners can take rarity from Exophase.</summary>
        public static readonly IReadOnlyCollection<string> ExophaseRarityProviders =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Xbox", "Xenia", "EA", "Ubisoft", "RPCS3", "ShadPS4", "Vita3K"
            };

        public string Icons { get; set; }

        public string Categories { get; set; }

        /// <summary>How <see cref="Categories"/> combines with the platform's own; set alongside it.</summary>
        public MetadataCategoryMode? CategoryMode { get; set; }

        public string Rarity { get; set; }

        public string CapstoneArt { get; set; }

        public bool IsEmpty() =>
            Icons == null && Categories == null && Rarity == null && CapstoneArt == null;

        /// <summary>Every field set: each platform's own data, no borrowed categories.</summary>
        public static MetadataChoices CreateDefaults()
        {
            return new MetadataChoices
            {
                Icons = string.Empty,
                Categories = string.Empty,
                CategoryMode = MetadataCategoryMode.Merge,
                Rarity = string.Empty,
                CapstoneArt = string.Empty
            };
        }

        public MetadataChoices Clone()
        {
            return new MetadataChoices
            {
                Icons = Icons,
                Categories = Categories,
                CategoryMode = CategoryMode,
                Rarity = Rarity,
                CapstoneArt = CapstoneArt
            };
        }
    }

    /// <summary>Metadata tab settings: defaults plus per-platform overrides.</summary>
    public partial class PersistedSettings
    {
        private MetadataChoices _metadataDefaults = MetadataChoices.CreateDefaults();
        private Dictionary<string, MetadataChoices> _metadataPlatformChoices =
            new Dictionary<string, MetadataChoices>(StringComparer.OrdinalIgnoreCase);
        private bool _metadataChoicesSeeded = true;

        public MetadataChoices MetadataDefaults
        {
            get => _metadataDefaults;
            set => SetValue(ref _metadataDefaults, value ?? MetadataChoices.CreateDefaults());
        }

        /// <summary>Per-platform overrides keyed by provider key; absent platforms follow the defaults.</summary>
        public Dictionary<string, MetadataChoices> MetadataPlatformChoices
        {
            get => _metadataPlatformChoices;
            set => SetValue(ref _metadataPlatformChoices, NormalizePlatformChoices(value));
        }

        /// <summary>
        /// One-time seed of the choices from the per-provider Exophase rarity toggles and the Steam
        /// SteamHunters mode. Defaults true so configs written by this build are never re-seeded;
        /// the migration keys off the flag's absence.
        /// </summary>
        public bool MetadataChoicesSeeded
        {
            get => _metadataChoicesSeeded;
            set => SetValue(ref _metadataChoicesSeeded, value);
        }

        /// <summary>The effective choices for a platform: its override where set, else the defaults.</summary>
        public MetadataChoices ResolveMetadataChoices(string providerKey)
        {
            var defaults = MetadataDefaults ?? MetadataChoices.CreateDefaults();
            MetadataChoices platform = null;
            if (!string.IsNullOrWhiteSpace(providerKey))
            {
                MetadataPlatformChoices?.TryGetValue(providerKey, out platform);
            }

            var categoriesFromPlatform = platform?.Categories != null;
            return new MetadataChoices
            {
                Icons = platform?.Icons ?? defaults.Icons ?? string.Empty,
                Categories = (categoriesFromPlatform ? platform.Categories : defaults.Categories) ?? string.Empty,
                CategoryMode = (categoriesFromPlatform ? platform.CategoryMode : defaults.CategoryMode) ?? MetadataCategoryMode.Merge,
                Rarity = platform?.Rarity ?? defaults.Rarity ?? string.Empty,
                CapstoneArt = platform?.CapstoneArt ?? defaults.CapstoneArt ?? string.Empty
            };
        }

        /// <summary>True when the platform can take rarity from Exophase and its choice turns that on.</summary>
        public bool UsesExophaseRarity(string providerKey)
        {
            return !string.IsNullOrWhiteSpace(providerKey) &&
                   MetadataChoices.ExophaseRarityProviders.Contains(providerKey.Trim()) &&
                   string.Equals(ResolveMetadataChoices(providerKey.Trim()).Rarity, MetadataChoices.Exophase, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Fresh installs keep SteamHunters categories on Steam games, as before the Metadata tab.</summary>
        internal static Dictionary<string, MetadataChoices> CreateDefaultMetadataPlatformChoices()
        {
            return new Dictionary<string, MetadataChoices>(StringComparer.OrdinalIgnoreCase)
            {
                ["Steam"] = new MetadataChoices
                {
                    Categories = MetadataChoices.SteamHunters,
                    CategoryMode = MetadataCategoryMode.Merge
                }
            };
        }

        internal static Dictionary<string, MetadataChoices> ClonePlatformChoices(Dictionary<string, MetadataChoices> source)
        {
            return NormalizePlatformChoices(source?.ToDictionary(pair => pair.Key, pair => pair.Value?.Clone()));
        }

        private static Dictionary<string, MetadataChoices> NormalizePlatformChoices(Dictionary<string, MetadataChoices> value)
        {
            var normalized = new Dictionary<string, MetadataChoices>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in value ?? new Dictionary<string, MetadataChoices>())
            {
                if (!string.IsNullOrWhiteSpace(pair.Key) && pair.Value != null && !pair.Value.IsEmpty())
                {
                    normalized[pair.Key.Trim()] = pair.Value;
                }
            }

            return normalized;
        }
    }
}
