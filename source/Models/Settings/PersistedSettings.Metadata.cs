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
        public const string PlayStation = "PlayStation";
        public const string Xbox = "Xbox";
        public const string TrueGaming = "TrueGaming";

        /// <summary>Platforms whose scanners can take rarity from Exophase.</summary>
        public static readonly IReadOnlyCollection<string> ExophaseRarityProviders =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Xbox", "Xenia", "EA", "Ubisoft", "RPCS3", "ShadPS4", "Vita3K"
            };

        /// <summary>
        /// Platforms whose achievements carry their own points, on every achievement: they never
        /// take points from a source, so they have no points choice.
        /// </summary>
        public static readonly IReadOnlyCollection<string> OwnPointsProviders =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Epic", "RetroAchievements", "FFXIV", "GW2", "Hypixel", "Hoyoverse", "BattleNet", "GameJolt", "Exophase"
            };

        /// <summary>
        /// Platforms whose achievements come without icons (EA) or with only unlocked ones
        /// (Ubisoft), so by default their icons come from Exophase.
        /// </summary>
        public static readonly IReadOnlyCollection<string> ExophaseIconProviders =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "EA", "Ubisoft" };

        /// <summary>Platforms whose achievements carry their own trophy types, so they have no trophy type choice.</summary>
        public static readonly IReadOnlyCollection<string> OwnTrophyTypeProviders =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "RetroAchievements", "Hoyoverse", "BattleNet", "GameJolt", "Exophase"
            };

        /// <summary>
        /// Platforms whose games no source has, so they take nothing from a source and have no
        /// choices at all.
        /// </summary>
        public static readonly IReadOnlyCollection<string> NoSourceProviders =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "RetroAchievements", "BattleNet", "GameJolt", "Riot", "FFXIV", "Minecraft", "GW2", "Hypixel", "Hoyoverse"
            };

        /// <summary>Whether a platform can take any field from a source; the defaults row (null) can.</summary>
        public static bool TakesSources(string providerKey) =>
            string.IsNullOrWhiteSpace(providerKey) || !NoSourceProviders.Contains(providerKey.Trim());

        /// <summary>Whether a platform can take points from a source; the defaults row (null) can.</summary>
        public static bool TakesPoints(string providerKey) =>
            TakesSources(providerKey) &&
            (string.IsNullOrWhiteSpace(providerKey) || !OwnPointsProviders.Contains(providerKey.Trim()));

        /// <summary>Whether a platform can take trophy types from a source; the defaults row (null) can.</summary>
        public static bool TakesTrophyTypes(string providerKey) =>
            TakesSources(providerKey) &&
            (string.IsNullOrWhiteSpace(providerKey) || !OwnTrophyTypeProviders.Contains(providerKey.Trim()));

        public string Icons { get; set; }

        /// <summary>The source whose categories replace the platform's own; empty keeps the platform's.</summary>
        public string Categories { get; set; }

        /// <summary>The source whose achievement tags replace the platform's own; empty keeps the platform's.</summary>
        public string Tags { get; set; }

        public string Rarity { get; set; }

        public string CapstoneArt { get; set; }

        /// <summary>Trophy types (platinum, gold, silver, bronze).</summary>
        public string TrophyTypes { get; set; }

        public string Points { get; set; }

        /// <summary>The source whose achievement names and descriptions replace the platform's; empty keeps the platform's.</summary>
        public string Text { get; set; }

        public bool IsEmpty() =>
            Icons == null && Categories == null && Tags == null && Rarity == null && CapstoneArt == null &&
            TrophyTypes == null && Points == null && Text == null;

        /// <summary>
        /// Every field set: each platform's own icons, SteamHunters categories, TrueGaming tags,
        /// Exophase rarity (which only the platforms in <see cref="ExophaseRarityProviders"/> take),
        /// PlayStation trophy types and the platinum for auto capstones where one matches, and
        /// Xbox points, with each platform's own text.
        /// </summary>
        public static MetadataChoices CreateDefaults()
        {
            return new MetadataChoices
            {
                Icons = string.Empty,
                Categories = SteamHunters,
                Tags = TrueGaming,
                Rarity = Exophase,
                CapstoneArt = PlayStation,
                TrophyTypes = PlayStation,
                Points = Xbox,
                Text = string.Empty
            };
        }

        /// <summary>
        /// The defaults with every field set, taking <see cref="CreateDefaults"/>'s value for any
        /// field a configuration saved before the field existed lacks.
        /// </summary>
        internal static MetadataChoices CompleteDefaults(MetadataChoices stored)
        {
            var defaults = CreateDefaults();
            if (stored == null)
            {
                return defaults;
            }

            var complete = stored.Clone();
            complete.Icons = complete.Icons ?? defaults.Icons;
            complete.Categories = complete.Categories ?? defaults.Categories;
            complete.Tags = complete.Tags ?? defaults.Tags;
            complete.Rarity = complete.Rarity ?? defaults.Rarity;
            complete.CapstoneArt = complete.CapstoneArt ?? defaults.CapstoneArt;
            complete.TrophyTypes = complete.TrophyTypes ?? defaults.TrophyTypes;
            complete.Points = complete.Points ?? defaults.Points;
            complete.Text = complete.Text ?? defaults.Text;
            return complete;
        }

        public bool SameAs(MetadataChoices other)
        {
            return other != null &&
                   Icons == other.Icons &&
                   Categories == other.Categories &&
                   Tags == other.Tags &&
                   Rarity == other.Rarity &&
                   CapstoneArt == other.CapstoneArt &&
                   TrophyTypes == other.TrophyTypes &&
                   Points == other.Points &&
                   Text == other.Text;
        }

        public MetadataChoices Clone()
        {
            return new MetadataChoices
            {
                Icons = Icons,
                Categories = Categories,
                Tags = Tags,
                Rarity = Rarity,
                CapstoneArt = CapstoneArt,
                TrophyTypes = TrophyTypes,
                Points = Points,
                Text = Text
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
        private MetadataConsoleVersion _metadataConsoleVersion = MetadataConsoleVersion.Newest;

        public MetadataChoices MetadataDefaults
        {
            get => _metadataDefaults;
            set => SetValue(ref _metadataDefaults, MetadataChoices.CompleteDefaults(value));
        }

        /// <summary>Per-platform overrides keyed by provider key; absent platforms follow the defaults.</summary>
        public Dictionary<string, MetadataChoices> MetadataPlatformChoices
        {
            get => _metadataPlatformChoices;
            set => SetValue(ref _metadataPlatformChoices, NormalizePlatformChoices(value));
        }

        /// <summary>
        /// Which console's list the PlayStation and Xbox sources take for a game on several
        /// consoles of the same family.
        /// </summary>
        public MetadataConsoleVersion MetadataConsoleVersion
        {
            get => _metadataConsoleVersion;
            set => SetValue(ref _metadataConsoleVersion, value);
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

        /// <summary>
        /// The effective choices for a game: each field from the game's own choices where set,
        /// else its platform's override, else the defaults. A platform no source has
        /// (<see cref="MetadataChoices.NoSourceProviders"/>) gets Off everywhere, whatever is stored.
        /// </summary>
        public MetadataChoices ResolveMetadataChoices(string providerKey, MetadataChoices game = null)
        {
            if (!MetadataChoices.TakesSources(providerKey))
            {
                return new MetadataChoices
                {
                    Icons = string.Empty,
                    Categories = string.Empty,
                    Tags = string.Empty,
                    Rarity = string.Empty,
                    CapstoneArt = string.Empty,
                    TrophyTypes = string.Empty,
                    Points = string.Empty,
                    Text = string.Empty
                };
            }

            var defaults = ResolvePlatformDefaults(providerKey);
            MetadataChoices platform = null;
            if (!string.IsNullOrWhiteSpace(providerKey))
            {
                MetadataPlatformChoices?.TryGetValue(providerKey, out platform);
            }

            return new MetadataChoices
            {
                Icons = game?.Icons ?? platform?.Icons ?? defaults.Icons ?? string.Empty,
                Categories = game?.Categories ?? platform?.Categories ?? defaults.Categories ?? string.Empty,
                Tags = game?.Tags ?? platform?.Tags ?? defaults.Tags ?? string.Empty,
                Rarity = !string.IsNullOrWhiteSpace(providerKey) && MetadataChoices.ExophaseRarityProviders.Contains(providerKey.Trim())
                    ? platform?.Rarity ?? defaults.Rarity ?? string.Empty
                    : string.Empty,
                CapstoneArt = game?.CapstoneArt ?? platform?.CapstoneArt ?? defaults.CapstoneArt ?? string.Empty,
                TrophyTypes = MetadataChoices.TakesTrophyTypes(providerKey)
                    ? game?.TrophyTypes ?? platform?.TrophyTypes ?? defaults.TrophyTypes ?? string.Empty
                    : string.Empty,
                Points = MetadataChoices.TakesPoints(providerKey)
                    ? game?.Points ?? platform?.Points ?? defaults.Points ?? string.Empty
                    : string.Empty,
                Text = game?.Text ?? platform?.Text ?? defaults.Text ?? string.Empty
            };
        }

        /// <summary>
        /// What a platform row's Default stands for: the defaults row, with Exophase icons for the
        /// platforms whose achievements lack their own (<see cref="MetadataChoices.ExophaseIconProviders"/>).
        /// </summary>
        public MetadataChoices ResolvePlatformDefaults(string providerKey)
        {
            var defaults = (MetadataDefaults ?? MetadataChoices.CreateDefaults()).Clone();
            if (!string.IsNullOrWhiteSpace(providerKey) && MetadataChoices.ExophaseIconProviders.Contains(providerKey.Trim()))
            {
                defaults.Icons = MetadataChoices.Exophase;
            }

            return defaults;
        }

        /// <summary>Fresh installs keep SteamHunters categories on Steam games, as before the Metadata tab.</summary>
        internal static Dictionary<string, MetadataChoices> CreateDefaultMetadataPlatformChoices()
        {
            return new Dictionary<string, MetadataChoices>(StringComparer.OrdinalIgnoreCase)
            {
                ["Steam"] = new MetadataChoices
                {
                    Categories = MetadataChoices.SteamHunters
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
