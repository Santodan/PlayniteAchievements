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

        private static readonly Regex SitePrefix = new Regex(
            @"^(ta|tt|tsa):(.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>
        /// A stored or pasted link: a bare slug for the platform's own site, a slug with a site
        /// prefix (<c>ta:</c>, <c>tt:</c>, <c>tsa:</c>), or a page address on any of the three sites.
        /// </summary>
        internal static bool TryParse(string linkId, string servicingProviderKey, out TrueGamingSite site, out string slug)
        {
            site = TrueGamingSite.For(servicingProviderKey);
            slug = (linkId ?? string.Empty).Trim().Trim('/');
            var address = PageAddress.Match(slug);
            var prefixed = SitePrefix.Match(slug);
            if (prefixed.Success)
            {
                site = TrueGamingSite.ByPrefix(prefixed.Groups[1].Value) ?? site;
                slug = prefixed.Groups[2].Value.Trim().Trim('/');
            }
            else if (address.Success)
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

        /// <summary>
        /// The pages a link names, comma-separated ("ELDEN-RING, tt:ELDEN-RING"), one per site in
        /// lookup order; a part that does not parse is skipped, as is a second page on one site.
        /// </summary>
        internal static List<(TrueGamingSite Site, string Slug)> ParsePages(string linkId, string servicingProviderKey)
        {
            var pages = new List<(TrueGamingSite Site, string Slug)>();
            foreach (var part in (linkId ?? string.Empty).Split(','))
            {
                if (TryParse(part, servicingProviderKey, out var site, out var slug) && pages.All(page => page.Site != site))
                {
                    pages.Add((site, slug));
                }
            }

            return pages.OrderBy(page => IndexOf(page.Site)).ToList();
        }

        /// <summary>The stored link of <paramref name="pages"/>, in lookup order.</summary>
        internal static string Format(IEnumerable<(TrueGamingSite Site, string Slug)> pages, string servicingProviderKey) =>
            string.Join(", ", (pages ?? Enumerable.Empty<(TrueGamingSite Site, string Slug)>())
                .OrderBy(page => IndexOf(page.Site))
                .Select(page => page.Site.LinkId(page.Slug, servicingProviderKey)));

        private static int IndexOf(TrueGamingSite site)
        {
            for (var i = 0; i < TrueGamingSite.LookupOrder.Count; i++)
            {
                if (TrueGamingSite.LookupOrder[i] == site)
                {
                    return i;
                }
            }

            return int.MaxValue;
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
            "trueachievements", "ta", "www.trueachievements.com", "achievements", new[] { "-Xbox-One", "-Xbox-360" });

        // PlayStation versions are separate lists; the newest console's is tried first.
        public static readonly TrueGamingSite TrueTrophies = new TrueGamingSite(
            "truetrophies", "tt", "www.truetrophies.com", "trophies", new[] { "-PS5", "-PS4", "-PS3", "-PS-Vita" });

        public static readonly TrueGamingSite TrueSteamAchievements = new TrueGamingSite(
            "truesteamachievements", "tsa", "truesteamachievements.com", "achievements", Array.Empty<string>());

        private static readonly HashSet<string> XboxProviders =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Xbox", "Xenia" };

        private static readonly HashSet<string> PlayStationProviders =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "PSN", "RPCS3", "ShadPS4", "Vita3K" };

        private TrueGamingSite(string name, string prefix, string host, string listPath, IReadOnlyList<string> suffixes)
        {
            Name = name;
            Prefix = prefix;
            Host = host;
            ListPath = listPath;
            Suffixes = suffixes;
        }

        public string Name { get; }

        /// <summary>The short site tag of a stored link to a site other than the platform's own.</summary>
        public string Prefix { get; }

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
        /// The sites a game is looked up on, in order, whatever its platform: a cross-platform
        /// release is often listed on only one of them.
        /// </summary>
        public static readonly IReadOnlyList<TrueGamingSite> LookupOrder =
            new[] { TrueAchievements, TrueTrophies, TrueSteamAchievements };

        /// <summary>The stored link of a page: the bare slug on the platform's own site, else the slug with the site's prefix.</summary>
        public string LinkId(string slug, string servicingProviderKey) =>
            For(servicingProviderKey) == this ? slug : $"{Prefix}:{slug}";

        public static TrueGamingSite ByPrefix(string prefix) =>
            LookupOrder.FirstOrDefault(site => string.Equals(site.Prefix, prefix, StringComparison.OrdinalIgnoreCase));

        public static TrueGamingSite ByName(string name) =>
            new[] { TrueAchievements, TrueTrophies, TrueSteamAchievements }
                .FirstOrDefault(site => string.Equals(site.Name, name, StringComparison.OrdinalIgnoreCase));
    }
}
