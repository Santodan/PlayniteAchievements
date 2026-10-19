using HtmlAgilityPack;
using PlayniteAchievements.Models.Achievements;
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
    }

    /// <summary>A parsed PSPrices trophy page: the trophy set's NPWR id and its trophies.</summary>
    internal sealed class PsPricesTrophyPage
    {
        public string NpCommunicationId { get; set; }

        public string Title { get; set; }

        public List<AchievementDetail> Trophies { get; } = new List<AchievementDetail>();
    }

    /// <summary>
    /// Reads PSPrices pages. The trophy page carries the NPWR id in its title
    /// ("God of War Ragnarök (NPWR22392_00) — Trophies") and links each trophy's icon on Sony's
    /// own image servers at full size, so it serves both as the name-to-NPWR lookup and, without
    /// a PSN sign-in, as the trophy list itself.
    /// </summary>
    internal static class PsPricesPageParser
    {
        public const string BaseUrl = "https://psprices.com";

        private static readonly Regex NpwrInTitle = new Regex(
            @"\((NPWR\d{5}_\d{2})\)",
            RegexOptions.CultureInvariant);

        private static readonly Regex GamePagePath = new Regex(
            @"^/region-[a-z0-9-]+/game/\d+/[^?#""']+$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex TrophyPagePath = new Regex(
            @"^/game-trophies/\d+-[^?#""'/]+$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        // Trophy types are drawn as small cached images; their file names identify the type.
        // Unknown names leave the type blank, which only costs the platinum lookup.
        private static readonly Dictionary<string, string> TypeIconNames =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["18097f983b35f53c58d956accbf2b7ba"] = "platinum",
                ["57fc024b481d7fb41fe6cc4a3ba78d43"] = "gold",
                ["b60a264f036a64de1335690fee9c2f15"] = "silver",
                ["c50466dc7ae9aa89376e835639b2d614"] = "bronze"
            };

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
            if (npwr == null || !npwr.Success)
            {
                return null;
            }

            var page = new PsPricesTrophyPage
            {
                NpCommunicationId = npwr.Groups[1].Value,
                Title = Clean(titleText.Substring(0, npwr.Index))
            };

            foreach (var row in document.DocumentNode.SelectNodes("//tbody/tr") ?? Enumerable.Empty<HtmlNode>())
            {
                var icon = row.SelectSingleNode(".//img[contains(@src,'psnobj') or contains(@src,'image.api.playstation.com')]");
                var name = Clean(icon?.GetAttributeValue("alt", null));
                if (icon == null || name == null)
                {
                    continue;
                }

                var type = row.SelectNodes(".//img[starts-with(@src,'/cache/')]")?
                    .Select(img => TypeFromIcon(img.GetAttributeValue("src", string.Empty)))
                    .FirstOrDefault(value => value != null);

                page.Trophies.Add(new AchievementDetail
                {
                    ApiName = $"{page.NpCommunicationId}:{page.Trophies.Count}",
                    DisplayName = name,
                    Description = Clean(row.SelectSingleNode(".//div[contains(concat(' ', normalize-space(@class), ' '), ' mt-1 ')]")?.InnerText),
                    UnlockedIconPath = icon.GetAttributeValue("src", null),
                    TrophyType = type,
                    IsCapstone = string.Equals(type, "platinum", StringComparison.OrdinalIgnoreCase)
                });
            }

            return page;
        }

        /// <summary>True for Cloudflare's interstitial rather than the requested page.</summary>
        public static bool IsChallengePage(string html)
        {
            return string.IsNullOrEmpty(html) ||
                   html.IndexOf("<title>Just a moment", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   html.IndexOf("challenge-platform/h/", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   html.IndexOf("Verifying you are human", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string TypeFromIcon(string src)
        {
            var fileName = System.IO.Path.GetFileNameWithoutExtension(src ?? string.Empty);
            var at = fileName.IndexOf('@');
            if (at >= 0)
            {
                fileName = fileName.Substring(0, at);
            }

            return TypeIconNames.TryGetValue(fileName, out var type) ? type : null;
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
