using PlayniteAchievements.Common;
using PlayniteAchievements.Providers.Exophase;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace PlayniteAchievements.Providers.Metadata.TrueGaming
{
    /// <summary>Page slugs and stored links of the TrueGaming source.</summary>
    internal static class TrueGamingLinks
    {
        private static readonly Regex PageAddress = new Regex(
            @"^(?:https?://)?(?:www\.)?(trueachievements|truetrophies|truesteamachievements)\.com/game/([^/?#]+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>
        /// A stored or pasted link: a bare slug for the platform's own site, or a page address on
        /// any of the three sites.
        /// </summary>
        internal static bool TryParse(string linkId, string servicingProviderKey, out TrueGamingSite site, out string slug)
        {
            site = TrueGamingSite.For(servicingProviderKey);
            slug = (linkId ?? string.Empty).Trim().Trim('/');
            var address = PageAddress.Match(slug);
            if (address.Success)
            {
                site = TrueGamingSite.ByName(address.Groups[1].Value) ?? site;
                slug = address.Groups[2].Value;
            }
            else if (slug.StartsWith("game/", StringComparison.OrdinalIgnoreCase))
            {
                slug = slug.Substring("game/".Length).Split('/')[0];
            }

            return slug.Length > 0 && slug.IndexOfAny(new[] { '/', '?', '#', ' ' }) < 0;
        }

        /// <summary>The site slugs to try for each name: each name's slug, then with each suffix.</summary>
        internal static IEnumerable<string> CandidateSlugs(IEnumerable<string> names, IReadOnlyList<string> suffixes)
        {
            return (names ?? Enumerable.Empty<string>())
                .Select(ToSlug)
                .Where(slug => slug != null)
                .SelectMany(slug => new[] { slug }.Concat((suffixes ?? Array.Empty<string>()).Select(suffix => slug + suffix)))
                .Distinct(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The sites' slug for a title: accents and apostrophes dropped, other runs of punctuation
        /// and spaces as one hyphen, case kept ("Clair Obscur: Expedition 33" as
        /// "Clair-Obscur-Expedition-33").
        /// </summary>
        internal static string ToSlug(string name)
        {
            var normalized = GameNameNormalizer.StripApostrophes(
                ExophaseGameNameMatcher.NormalizeGameName(GameNameNormalizer.StripDiacritics(name ?? string.Empty)) ?? string.Empty);
            var slug = new StringBuilder();
            foreach (var c in normalized)
            {
                if (char.IsLetterOrDigit(c) && c < 128)
                {
                    slug.Append(c);
                }
                else if (slug.Length > 0 && slug[slug.Length - 1] != '-')
                {
                    slug.Append('-');
                }
            }

            var result = slug.ToString().Trim('-');
            return result.Length == 0 ? null : result;
        }
    }

    /// <summary>One of the three TrueGaming sites.</summary>
    internal sealed class TrueGamingSite
    {
        public static readonly TrueGamingSite TrueAchievements = new TrueGamingSite(
            "trueachievements", "www.trueachievements.com", "achievements", new[] { "-Xbox-One", "-Xbox-360" });

        // PlayStation versions are separate lists; the newest console's is tried first.
        public static readonly TrueGamingSite TrueTrophies = new TrueGamingSite(
            "truetrophies", "www.truetrophies.com", "trophies", new[] { "-PS5", "-PS4", "-PS3", "-PS-Vita" });

        public static readonly TrueGamingSite TrueSteamAchievements = new TrueGamingSite(
            "truesteamachievements", "truesteamachievements.com", "achievements", Array.Empty<string>());

        private static readonly HashSet<string> XboxProviders =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Xbox", "Xenia" };

        private static readonly HashSet<string> PlayStationProviders =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "PSN", "RPCS3", "ShadPS4", "Vita3K" };

        private TrueGamingSite(string name, string host, string listPath, IReadOnlyList<string> suffixes)
        {
            Name = name;
            Host = host;
            ListPath = listPath;
            Suffixes = suffixes;
        }

        public string Name { get; }

        public string Host { get; }

        public string ListPath { get; }

        /// <summary>Slug endings of the site's other editions of a game, tried after the plain slug.</summary>
        public IReadOnlyList<string> Suffixes { get; }

        public string ListUrl(string slug) => $"https://{Host}/game/{Uri.EscapeDataString(slug)}/{ListPath}";

        public static TrueGamingSite For(string servicingProviderKey)
        {
            var key = servicingProviderKey?.Trim() ?? string.Empty;
            return XboxProviders.Contains(key) ? TrueAchievements
                : PlayStationProviders.Contains(key) ? TrueTrophies
                : TrueSteamAchievements;
        }

        /// <summary>
        /// The sites to look a game up on: the platform's own first, then the others, since a game
        /// one site does not list can be on another with the same achievements. Xbox comes before
        /// PlayStation, as PC games more often also release there.
        /// </summary>
        public static IReadOnlyList<TrueGamingSite> LookupOrder(string servicingProviderKey)
        {
            var own = For(servicingProviderKey);
            return new[] { own }
                .Concat(new[] { TrueAchievements, TrueTrophies, TrueSteamAchievements }.Where(site => site != own))
                .ToList();
        }

        /// <summary>The stored link of a page: the bare slug on the platform's own site, else its address.</summary>
        public string LinkId(string slug, string servicingProviderKey) =>
            For(servicingProviderKey) == this ? slug : $"{Host}/game/{slug}";

        public static TrueGamingSite ByName(string name) =>
            new[] { TrueAchievements, TrueTrophies, TrueSteamAchievements }
                .FirstOrDefault(site => string.Equals(site.Name, name, StringComparison.OrdinalIgnoreCase));
    }
}
