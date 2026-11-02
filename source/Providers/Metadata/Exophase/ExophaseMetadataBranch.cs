using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Providers.Exophase;
using System;
using System.Collections.Generic;
using System.Linq;

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
            generations: new[]
            {
                new ExophaseConsole("Xbox Series", "xbox", "-xbox"),
                new ExophaseConsole("Xbox One", "xbox-one", "-xbox-one", "-xbox"),
                new ExophaseConsole("Xbox 360", "xbox-360", "-xbox-360")
            },
            others: Array.Empty<ExophaseConsole>(),
            platformHint: "xbox",
            ownPlatform: false);

        public static readonly ExophaseMetadataBranch PlayStation = new ExophaseMetadataBranch(
            key: "PlayStation",
            displayNameKey: "LOCPlayAch_Metadata_Source_PlayStation",
            fields: MetadataField.Icons | MetadataField.TrophyTypes | MetadataField.Categories | MetadataField.CategoryArt | MetadataField.CapstoneArt | MetadataField.Text,
            nativeProviders: new[] { "PSN", "RPCS3", "ShadPS4", "Vita3K" },
            // -psn for recent games, the platform itself for older ones ("dark-souls-iii-ps4").
            generations: new[]
            {
                new ExophaseConsole("PS5", "ps5", "-psn"),
                new ExophaseConsole("PS4", "ps4", "-ps4", "-psn"),
                new ExophaseConsole("PS3", "ps3", "-ps3")
            },
            // A handheld's list usually differs from the home consoles', so it comes last either way.
            others: new[] { new ExophaseConsole("PS Vita", "psvita", "-psvita") },
            platformHint: "psn",
            ownPlatform: false);

        public static readonly ExophaseMetadataBranch Own = new ExophaseMetadataBranch(
            key: "Exophase",
            displayNameKey: "LOCPlayAch_Provider_Exophase",
            fields: MetadataField.Rarity | MetadataField.Icons,
            nativeProviders: Array.Empty<string>(),
            generations: Array.Empty<ExophaseConsole>(),
            others: Array.Empty<ExophaseConsole>(),
            platformHint: null,
            ownPlatform: true);

        public static readonly IReadOnlyList<ExophaseMetadataBranch> All = new[] { Xbox, PlayStation, Own };

        private ExophaseMetadataBranch(
            string key,
            string displayNameKey,
            MetadataField fields,
            IEnumerable<string> nativeProviders,
            IReadOnlyList<ExophaseConsole> generations,
            IReadOnlyList<ExophaseConsole> others,
            string platformHint,
            bool ownPlatform)
        {
            Key = key;
            DisplayNameKey = displayNameKey;
            Fields = fields;
            NativeProviders = new HashSet<string>(nativeProviders, StringComparer.OrdinalIgnoreCase);
            _generations = generations;
            _others = others;
            PlatformHint = platformHint;
            OwnPlatform = ownPlatform;
        }

        public string Key { get; }

        public string DisplayNameKey { get; }

        public MetadataField Fields { get; }

        /// <summary>The servicing providers whose achievements are already this branch's platform's.</summary>
        public ISet<string> NativeProviders { get; }

        // The borrowed platform's consoles, newest first, and those that follow them in either order.
        private readonly IReadOnlyList<ExophaseConsole> _generations;
        private readonly IReadOnlyList<ExophaseConsole> _others;

        /// <summary>True for a branch that borrows a family of consoles, so the console version setting applies to it.</summary>
        public bool HasConsoles => _generations.Count > 0;

        /// <summary>The borrowed platform's consoles, the one the setting prefers first; empty for the own-platform branch.</summary>
        public IReadOnlyList<ExophaseConsole> Consoles(MetadataConsoleVersion version) =>
            (version == MetadataConsoleVersion.Oldest ? _generations.Reverse() : _generations).Concat(_others).ToList();

        /// <summary>A borrowed page's slug suffixes, the preferred console's first.</summary>
        public IReadOnlyList<string> PageSuffixes(MetadataConsoleVersion version) =>
            Consoles(version).SelectMany(console => console.PageSuffixes).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        /// <summary>The Exophase platforms a search for the borrowed platform's page accepts, the preferred console's first.</summary>
        public IReadOnlyList<string> SearchPlatforms(MetadataConsoleVersion version) =>
            Consoles(version).Select(console => console.SearchSlug).ToList();

        /// <summary>
        /// The pages of the same game on consoles the setting prefers to every console the matched
        /// page can be on, best first. They come from the matched page's "Also on" links, which
        /// leave out the page's own console: a slug ending shared by two consoles ("-psn") can be
        /// either one not linked, and a slug with no known ending any console not linked. Empty
        /// when the matched page is already on the preferred console.
        /// </summary>
        public IReadOnlyList<ExophaseRelatedPage> PreferredVersions(
            string slug,
            IReadOnlyList<ExophaseRelatedPage> relatedPages,
            MetadataConsoleVersion version)
        {
            var consoles = Consoles(version).ToList();
            int Rank(string label) => consoles.FindIndex(console => string.Equals(console.Label, label, StringComparison.OrdinalIgnoreCase));

            var trimmed = slug?.Trim() ?? string.Empty;
            var related = (relatedPages ?? Array.Empty<ExophaseRelatedPage>())
                .Where(page => page != null && !string.Equals(page.Slug, trimmed, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var linked = new HashSet<string>(related.Select(page => page.Platform), StringComparer.OrdinalIgnoreCase);
            var byEnding = consoles
                .Where(console => console.PageSuffixes.Any(suffix => trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            var possible = (byEnding.Count > 0 ? byEnding : consoles)
                .Where(console => !linked.Contains(console.Label))
                .Select(console => Rank(console.Label))
                .ToList();
            var current = possible.Count > 0 ? possible.Min() : consoles.Count;

            return related
                .Select(page => (Page: page, Rank: Rank(page.Platform)))
                .Where(entry => entry.Rank >= 0 && entry.Rank < current)
                .OrderBy(entry => entry.Rank)
                .Select(entry => entry.Page)
                .ToList();
        }

        /// <summary>The borrowed platform's Exophase slug ("psn", "xbox"), which picks the page's address.</summary>
        public string PlatformHint { get; }

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

        // Exophase platform slugs that name one console, as a page's award list class gives them.
        private static readonly Dictionary<string, string> PlatformNames =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "ps3", "PS3" },
                { "ps4", "PS4" },
                { "ps5", "PS5" },
                { "psvita", "PS Vita" },
                { "xbox-360", "Xbox 360" },
                { "xbox-one", "Xbox One" }
            };

        /// <summary>The console an Exophase platform slug names ("PS3" for "ps3"); null when it names none.</summary>
        public static string PlatformName(string platformSlug) =>
            platformSlug != null && PlatformNames.TryGetValue(platformSlug.Trim(), out var name) ? name : null;

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

    /// <summary>One console of a borrowed platform, by the names Exophase gives it.</summary>
    internal sealed class ExophaseConsole
    {
        public ExophaseConsole(string label, string searchSlug, params string[] pageSuffixes)
        {
            Label = label;
            SearchSlug = searchSlug;
            PageSuffixes = pageSuffixes ?? Array.Empty<string>();
        }

        /// <summary>The console as a page's "Also on" list names it ("PS5", "Xbox 360").</summary>
        public string Label { get; }

        /// <summary>The console's platform slug in Exophase search ("ps5", "xbox-360").</summary>
        public string SearchSlug { get; }

        /// <summary>The slug endings of the console's pages; "-psn" and "-xbox" are each shared by two consoles.</summary>
        public IReadOnlyList<string> PageSuffixes { get; }
    }
}
