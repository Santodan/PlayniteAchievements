using System;
using System.Collections.Generic;

namespace PlayniteAchievements.Providers.Metadata.Exophase
{
    /// <summary>
    /// One metadata source read from Exophase's public award pages. The Xbox and PlayStation
    /// branches borrow that platform's page for a game another platform services; the Exophase
    /// branch reads the game's own platform's page for the figures only Exophase has (rarity from
    /// its players' unlocks) and the icons a platform lacks.
    /// </summary>
    internal sealed class ExophaseMetadataBranch
    {
        public static readonly ExophaseMetadataBranch Xbox = new ExophaseMetadataBranch(
            key: "Xbox",
            displayNameKey: "LOCPlayAch_Provider_Xbox",
            fields: MetadataField.Icons | MetadataField.Points | MetadataField.Categories | MetadataField.CategoryArt | MetadataField.Text,
            nativeProviders: new[] { "Xbox", "Xenia" },
            // Exophase lists current consoles under both -xbox and -xbox-one, and older games -xbox-360.
            pageSuffixes: new[] { "-xbox", "-xbox-one", "-xbox-360" },
            searchPlatforms: new[] { "xbox", "xbox-one", "xbox-360" },
            platformHint: "xbox",
            preferredPlatform: "Xbox Series",
            ownPlatform: false);

        public static readonly ExophaseMetadataBranch PlayStation = new ExophaseMetadataBranch(
            key: "PlayStation",
            displayNameKey: "LOCPlayAch_Metadata_Source_PlayStation",
            fields: MetadataField.Icons | MetadataField.TrophyTypes | MetadataField.Categories | MetadataField.CategoryArt | MetadataField.CapstoneArt | MetadataField.Text,
            nativeProviders: new[] { "PSN", "RPCS3", "ShadPS4", "Vita3K" },
            // Newest first: -psn for recent games, the platform itself for older ones ("dark-souls-iii-ps4").
            pageSuffixes: new[] { "-psn", "-ps4", "-ps3", "-psvita" },
            searchPlatforms: new[] { "ps5", "ps4", "ps3", "psvita" },
            platformHint: "psn",
            preferredPlatform: "PS5",
            ownPlatform: false);

        public static readonly ExophaseMetadataBranch Own = new ExophaseMetadataBranch(
            key: "Exophase",
            displayNameKey: "LOCPlayAch_Provider_Exophase",
            fields: MetadataField.Rarity | MetadataField.Icons,
            nativeProviders: Array.Empty<string>(),
            pageSuffixes: Array.Empty<string>(),
            searchPlatforms: Array.Empty<string>(),
            platformHint: null,
            preferredPlatform: null,
            ownPlatform: true);

        public static readonly IReadOnlyList<ExophaseMetadataBranch> All = new[] { Xbox, PlayStation, Own };

        private ExophaseMetadataBranch(
            string key,
            string displayNameKey,
            MetadataField fields,
            IEnumerable<string> nativeProviders,
            IReadOnlyList<string> pageSuffixes,
            IReadOnlyList<string> searchPlatforms,
            string platformHint,
            string preferredPlatform,
            bool ownPlatform)
        {
            Key = key;
            DisplayNameKey = displayNameKey;
            Fields = fields;
            NativeProviders = new HashSet<string>(nativeProviders, StringComparer.OrdinalIgnoreCase);
            PageSuffixes = pageSuffixes;
            SearchPlatforms = searchPlatforms;
            PlatformHint = platformHint;
            PreferredPlatform = preferredPlatform;
            OwnPlatform = ownPlatform;
        }

        public string Key { get; }

        public string DisplayNameKey { get; }

        public MetadataField Fields { get; }

        /// <summary>The servicing providers whose achievements are already this branch's platform's.</summary>
        public ISet<string> NativeProviders { get; }

        /// <summary>A borrowed page's slug suffixes, newest first.</summary>
        public IReadOnlyList<string> PageSuffixes { get; }

        /// <summary>The Exophase platforms a search for the borrowed platform's page accepts, newest first.</summary>
        public IReadOnlyList<string> SearchPlatforms { get; }

        /// <summary>The borrowed platform's Exophase slug ("psn", "xbox"), which picks the page's address.</summary>
        public string PlatformHint { get; }

        /// <summary>
        /// The "Also on" platform whose page replaces a borrowed match of the same game ("PS5"), for
        /// its larger icons; null to keep the match.
        /// </summary>
        public string PreferredPlatform { get; }

        /// <summary>
        /// True for the branch that reads the game's own platform's page, found by Exophase search
        /// with the scan's hints, rather than borrowing another platform's.
        /// </summary>
        public bool OwnPlatform { get; }

        // Endings that name one console. "-psn" (PS4 or PS5) and "-xbox" (Xbox One or Series) do not.
        private static readonly (string Suffix, string Platform)[] PlatformEndings =
        {
            ("-xbox-one", "Xbox One"),
            ("-xbox-360", "Xbox 360"),
            ("-ps4", "PS4"),
            ("-ps3", "PS3"),
            ("-psvita", "PS Vita")
        };

        /// <summary>The console a page slug's ending names ("PS4" for "bioshock-ps4"); null when it names none.</summary>
        public static string PlatformOfSlug(string slug)
        {
            var trimmed = slug?.Trim() ?? string.Empty;
            foreach (var (suffix, platform) in PlatformEndings)
            {
                if (trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    return platform;
                }
            }

            return null;
        }

        /// <summary>
        /// The Exophase platform slug of a servicing provider's achievements, for the own-platform
        /// branch; null for a platform Exophase rarity does not cover.
        /// </summary>
        public static string OwnPlatformSlug(string servicingProviderKey)
        {
            switch ((servicingProviderKey ?? string.Empty).Trim().ToUpperInvariant())
            {
                case "XBOX":
                    return "xbox";
                case "XENIA":
                    return "xbox-360";
                case "EA":
                    return "origin";
                case "UBISOFT":
                    return "uplay";
                case "RPCS3":
                    return "ps3";
                case "SHADPS4":
                    return "ps4";
                case "VITA3K":
                    return "psvita";
                default:
                    return null;
            }
        }
    }
}
