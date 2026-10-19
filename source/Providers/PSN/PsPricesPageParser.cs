using HtmlAgilityPack;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;

namespace PlayniteAchievements.Providers.PSN
{
    /// <summary>A PSPrices search result: the game page's path, its title and its platform badges.</summary>
    internal sealed class PsPricesSearchResult
    {
        public PsPricesSearchResult(string path, string title, IReadOnlyList<string> platforms)
        {
            Path = path;
            Title = title;
            Platforms = platforms ?? new List<string>();
        }

        public string Path { get; }

        public string Title { get; }

        /// <summary>Badge names such as "PlayStation 4"; other stores (Switch, iOS) also appear in results.</summary>
        public IReadOnlyList<string> Platforms { get; }

        public bool IsPlayStation => Platforms.Any(platform =>
            platform.StartsWith("PlayStation", StringComparison.OrdinalIgnoreCase));

        public bool IsPs5 => Platforms.Any(platform =>
            string.Equals(platform, "PlayStation 5", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A parsed PSPrices trophy page: the trophy set's NPWR id and title.</summary>
    internal sealed class PsPricesTrophyPage
    {
        public string NpCommunicationId { get; set; }

        public string Title { get; set; }
    }

    /// <summary>
    /// Reads PSPrices pages to find a game's trophy set id. The trophy page carries the NPWR id in
    /// its title ("God of War Ragnarök (NPWR22392_00) — Trophies"); the trophies themselves come
    /// from the PSN API.
    /// </summary>
    internal static class PsPricesPageParser
    {
        public const string BaseUrl = "https://psprices.com";

        private static readonly Regex NpwrAnywhere = new Regex(
            @"NPWR\d{5}_\d{2}",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex NpwrInTitle = new Regex(
            @"\((NPWR\d{5}_\d{2})\)",
            RegexOptions.CultureInvariant);

        private static readonly Regex GamePagePath = new Regex(
            @"^/region-[a-z0-9-]+/game/\d+/[^?#""']+$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex TrophyPagePath = new Regex(
            @"^/game-trophies/\d+-[^?#""'/]+$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static string BuildSearchUrl(string query) =>
            $"{BaseUrl}/region-us/games/?q={Uri.EscapeDataString(query ?? string.Empty)}";

        /// <summary>Game page links in a search results page, in page order, each once.</summary>
        public static List<PsPricesSearchResult> ParseSearchResults(string html)
        {
            var results = new List<PsPricesSearchResult>();
            var document = Load(html);
            if (document == null)
            {
                return results;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var anchor in document.DocumentNode.SelectNodes("//a[@href]") ?? Enumerable.Empty<HtmlNode>())
            {
                var path = ToPath(anchor.GetAttributeValue("href", string.Empty));
                if (path == null || !GamePagePath.IsMatch(path) || !seen.Add(path))
                {
                    continue;
                }

                // Cards carry the title in an <h3>; the first images are the platform badges.
                var title = Clean(anchor.SelectSingleNode(".//h3")?.InnerText) ??
                            Clean(anchor.GetAttributeValue("title", null));
                var platforms = (anchor.SelectNodes(".//span[contains(@class,'deal-strip')]//img[@alt]") ?? Enumerable.Empty<HtmlNode>())
                    .Select(img => Clean(img.GetAttributeValue("alt", null)))
                    .Where(alt => alt != null)
                    .ToList();
                results.Add(new PsPricesSearchResult(path, title, platforms));
            }

            return results;
        }

        /// <summary>The trophy page path a game page links to, or null.</summary>
        public static string FindTrophyPagePath(string html)
        {
            var document = Load(html);
            return document?.DocumentNode.SelectNodes("//a[@href]")?
                .Select(anchor => ToPath(anchor.GetAttributeValue("href", string.Empty)))
                .FirstOrDefault(path => path != null && TrophyPagePath.IsMatch(path));
        }

        public static PsPricesTrophyPage ParseTrophyPage(string html)
        {
            var document = Load(html);
            var titleText = Clean(document?.DocumentNode.SelectSingleNode("//title")?.InnerText);
            var npwr = titleText == null ? null : NpwrInTitle.Match(titleText);
            if (npwr != null && npwr.Success)
            {
                return new PsPricesTrophyPage
                {
                    NpCommunicationId = npwr.Groups[1].Value,
                    Title = Clean(titleText.Substring(0, npwr.Index))
                };
            }

            // Some pages leave the id out of the title; every trophy icon's URL carries it, so the
            // id the page's images use most is the set's.
            var fromIcons = NpwrAnywhere.Matches(html ?? string.Empty)
                .Cast<Match>()
                .GroupBy(match => match.Value, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(group => group.Count())
                .Select(group => group.Key)
                .FirstOrDefault();
            if (fromIcons == null)
            {
                return null;
            }

            var dash = titleText?.IndexOf(" — ", StringComparison.Ordinal) ?? -1;
            return new PsPricesTrophyPage
            {
                NpCommunicationId = fromIcons.ToUpperInvariant(),
                Title = Clean(dash > 0 ? titleText.Substring(0, dash) : titleText)
            };
        }

        /// <summary>True for Cloudflare's interstitial rather than the requested page.</summary>
        public static bool IsChallengePage(string html)
        {
            return string.IsNullOrEmpty(html) ||
                   html.IndexOf("<title>Just a moment", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   html.IndexOf("challenge-platform/h/", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   html.IndexOf("Verifying you are human", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string ToPath(string href)
        {
            if (string.IsNullOrWhiteSpace(href))
            {
                return null;
            }

            var value = WebUtility.HtmlDecode(href.Trim());
            if (value.StartsWith(BaseUrl, StringComparison.OrdinalIgnoreCase))
            {
                value = value.Substring(BaseUrl.Length);
            }

            return value.StartsWith("/", StringComparison.Ordinal) ? value : null;
        }

        private static string Clean(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            var decoded = Regex.Replace(WebUtility.HtmlDecode(text), @"\s+", " ").Trim();
            return decoded.Length == 0 ? null : decoded;
        }

        private static HtmlDocument Load(string html)
        {
            if (string.IsNullOrWhiteSpace(html))
            {
                return null;
            }

            var document = new HtmlDocument();
            document.LoadHtml(html);
            return document;
        }
    }
}
