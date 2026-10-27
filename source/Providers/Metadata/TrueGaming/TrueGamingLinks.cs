using PlayniteAchievements.Common;
using PlayniteAchievements.Providers.Exophase;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace PlayniteAchievements.Providers.Metadata.TrueGaming
{
    /// <summary>Page slugs and the one-name links of the TrueGaming source.</summary>
    internal static class TrueGamingLinks
    {
        private static readonly Regex PageAddress = new Regex(
            @"^(?:https?://)?(?:www\.)?(?:trueachievements|truetrophies|truesteamachievements)\.com/game/([^/?#]+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        // A link stored before links became one name ("ta:Hades, tt:Hades-PS5").
        private static readonly Regex SitePrefix = new Regex(
            @"^(?:ta|tt|tsa):", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>
        /// The one page name a link stands for on every site, without an edition suffix: from a
        /// name ("Elden-Ring"), a pasted address on any of the three sites, or a list stored by an
        /// earlier version (its first page). Null when nothing usable is given.
        /// </summary>
        internal static string Normalize(string linkId)
        {
            var slug = SitePrefix.Replace(((linkId ?? string.Empty).Split(',')[0]).Trim(), string.Empty).Trim().Trim('/');
            var address = PageAddress.Match(slug);
            if (address.Success)
            {
                slug = address.Groups[1].Value;
            }
            else if (slug.StartsWith("game/", StringComparison.OrdinalIgnoreCase))
            {
                slug = slug.Substring("game/".Length).Split('/')[0];
            }

            if (slug.Length == 0 || slug.IndexOfAny(new[] { '/', '?', '#', ' ', ':' }) >= 0)
            {
                return null;
            }

            return BaseSlug(slug);
        }

        private static readonly Regex SearchGame = new Regex(
            @"<Game>(?<game>.*?)</Game>",
            RegexOptions.Singleline | RegexOptions.CultureInvariant);

        private static readonly Regex SearchGameTitle = new Regex(@"<Title>(?<title>.*?)</Title>", RegexOptions.Singleline | RegexOptions.CultureInvariant);

        private static readonly Regex SearchGameUrl = new Regex(@"<Url>/game/(?<slug>[^/<]+)/", RegexOptions.CultureInvariant);

        /// <summary>The address of TrueAchievements' site search for game names; the other sites' searches return no games.</summary>
        internal static string SearchUrl(string name) =>
            $"https://{TrueGamingSite.TrueAchievements.Host}/true-api/sitesearch/search?q={Uri.EscapeDataString(name ?? string.Empty)}&category=Games";

        /// <summary>The games a site search answered with, as page slugs and titles, in its order.</summary>
        internal static IReadOnlyList<(string Slug, string Title)> ParseSearch(string text)
        {
            return SearchGame.Matches(text ?? string.Empty)
                .Cast<Match>()
                .Select(match => match.Groups["game"].Value)
                .Select(game => (
                    Slug: Uri.UnescapeDataString(SearchGameUrl.Match(game).Groups["slug"].Value),
                    Title: System.Net.WebUtility.HtmlDecode(SearchGameTitle.Match(game).Groups["title"].Value)))
                .Where(game => game.Slug.Length > 0)
                .ToList();
        }

        /// <summary>A slug without any site's edition suffix ("Elden-Ring-PS5" as "Elden-Ring").</summary>
        internal static string BaseSlug(string slug)
        {
            foreach (var suffix in TrueGamingSite.LookupOrder.SelectMany(site => site.Suffixes))
            {
                if (slug != null && slug.Length > suffix.Length && slug.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    return slug.Substring(0, slug.Length - suffix.Length);
                }
            }

            return slug;
        }

        /// <summary>The slugs a site's page for <paramref name="slug"/> may have: the name, then with each edition suffix.</summary>
        internal static IEnumerable<string> SlugsFor(TrueGamingSite site, string slug) =>
            new[] { slug }.Concat(site.Suffixes.Select(suffix => slug + suffix));

        /// <summary>
        /// The slugs to try on a site for a page name: with edition suffixes on the platform's own
        /// site or for a link the user set, else the name alone.
        /// </summary>
        internal static IEnumerable<string> SlugsToTry(TrueGamingSite site, TrueGamingSite ownSite, string slug, bool userSet) =>
            userSet || site == ownSite ? SlugsFor(site, slug) : new[] { slug };

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
            "trueachievements", "TrueAchievements", "ta", "www.trueachievements.com", "achievements", new[] { "-Xbox-One", "-Xbox-360" });

        // PlayStation versions are separate lists; the newest console's is tried first.
        public static readonly TrueGamingSite TrueTrophies = new TrueGamingSite(
            "truetrophies", "TrueTrophies", "tt", "www.truetrophies.com", "trophies", new[] { "-PS5", "-PS4", "-PS3", "-PS-Vita" });

        public static readonly TrueGamingSite TrueSteamAchievements = new TrueGamingSite(
            "truesteamachievements", "TrueSteamAchievements", "tsa", "truesteamachievements.com", "achievements", Array.Empty<string>());

        private static readonly HashSet<string> XboxProviders =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Xbox", "Xenia" };

        private static readonly HashSet<string> PlayStationProviders =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "PSN", "RPCS3", "ShadPS4", "Vita3K" };

        private TrueGamingSite(string name, string title, string prefix, string host, string listPath, IReadOnlyList<string> suffixes)
        {
            Name = name;
            Title = title;
            Prefix = prefix;
            Host = host;
            ListPath = listPath;
            Suffixes = suffixes;
        }

        public string Name { get; }

        /// <summary>The site's own name, as shown ("TrueTrophies").</summary>
        public string Title { get; }

        /// <summary>The short site tag ("ta", "tt", "tsa"), used in diagnosis file names.</summary>
        public string Prefix { get; }

        public string Host { get; }

        public string ListPath { get; }

        /// <summary>Slug endings of the site's other editions of a game, tried after the plain slug.</summary>
        public IReadOnlyList<string> Suffixes { get; }

        public string ListUrl(string slug) => $"https://{Host}/game/{Uri.EscapeDataString(slug)}/{ListPath}";

        /// <summary>The console a page slug's edition ending names ("Xbox One" for "BioShock-Xbox-One"); null for the plain slug.</summary>
        public string PlatformOf(string pageSlug)
        {
            var suffix = Suffixes.FirstOrDefault(ending => (pageSlug ?? string.Empty).EndsWith(ending, StringComparison.OrdinalIgnoreCase));
            return suffix == null ? null : suffix.Trim('-').Replace('-', ' ');
        }

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
    }
}
