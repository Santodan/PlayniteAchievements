using HtmlAgilityPack;
using PlayniteAchievements.Models.Achievements;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;

namespace PlayniteAchievements.Providers.Exophase
{
    /// <summary>The same game's page on another platform, from a game page's "Also on" links.</summary>
    internal sealed class ExophaseRelatedPage
    {
        public ExophaseRelatedPage(string platform, string slug)
        {
            Platform = platform;
            Slug = slug;
        }

        /// <summary>The platform as the page names it ("PS5", "PS4", "Steam").</summary>
        public string Platform { get; }

        public string Slug { get; }
    }

    /// <summary>One award list of a game page: the base game's, or a DLC's under its heading.</summary>
    internal sealed class ExophaseAwardSection
    {
        public ExophaseAwardSection(string title, string artUrl, IReadOnlyList<string> apiNames)
        {
            Title = title;
            ArtUrl = artUrl;
            ApiNames = apiNames ?? Array.Empty<string>();
        }

        /// <summary>The DLC's name, or null for the base game's list.</summary>
        public string Title { get; }

        /// <summary>The DLC's own image, or null when the page shows the game's image for it.</summary>
        public string ArtUrl { get; }

        /// <summary>The awards in the list, keyed as the award parser keys them.</summary>
        public IReadOnlyList<string> ApiNames { get; }
    }

    /// <summary>What the metadata lookups read from one award page, kept instead of its source.</summary>
    internal sealed class ExophaseAwardPage
    {
        public ExophaseAwardPage(
            List<AchievementDetail> achievements,
            IReadOnlyList<ExophaseAwardSection> sections,
            string gameArtUrl,
            IReadOnlyList<ExophaseRelatedPage> relatedPages,
            bool isChallenge,
            string platformSlug = null)
        {
            Achievements = achievements;
            Sections = sections ?? Array.Empty<ExophaseAwardSection>();
            GameArtUrl = gameArtUrl;
            RelatedPages = relatedPages ?? Array.Empty<ExophaseRelatedPage>();
            IsChallenge = isChallenge;
            PlatformSlug = platformSlug;
        }

        /// <summary>
        /// An address Exophase answers with 404: no page there, in any language (its localized
        /// and English addresses answer alike).
        /// </summary>
        public static ExophaseAwardPage Missing { get; } = new ExophaseAwardPage(null, null, null, null, false) { IsMissing = true };

        /// <summary>The page's own platform as Exophase's slug for it ("ps3"); null when the page does not say.</summary>
        public string PlatformSlug { get; }

        public bool IsMissing { get; private set; }

        /// <summary>The page's awards; null when it lists none.</summary>
        public List<AchievementDetail> Achievements { get; }

        public IReadOnlyList<ExophaseAwardSection> Sections { get; }

        public string GameArtUrl { get; }

        public IReadOnlyList<ExophaseRelatedPage> RelatedPages { get; }

        /// <summary>True when Exophase answered with its human check rather than the page.</summary>
        public bool IsChallenge { get; }

        /// <summary>
        /// The language code of the address the page was read from ("de"); null for the English
        /// page, including one a localized request fell back to.
        /// </summary>
        public string LanguageCode { get; internal set; }
    }

    /// <summary>
    /// Reads the game details of an Exophase award page (the "Game Info" list and the DLC award
    /// lists) and builds the localized award page addresses that list's "Languages" links follow.
    /// </summary>
    internal static class ExophaseGamePageParser
    {
        // The image a DLC heading's style draws beside it.
        private static readonly Regex StyleImageUrl = new Regex(
            @"url\(\s*['""]?([^'"")\s]+)['""]?\s*\)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        // The word a DLC heading ends in after the DLC's name ("Far Harbor Trophies").
        private static readonly Regex ListWordSuffix = new Regex(
            @"\s+(?:Trophies|Achievements|Challenges)\s*$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>
        /// The page's award lists in order: the base game's, then each DLC's under its heading. A
        /// DLC's art is kept only when it is not the game's own image, which Exophase also shows
        /// beside DLC lists that have none.
        /// </summary>
        public static IReadOnlyList<ExophaseAwardSection> ParseAwardSections(string html) => ParseAwardSections(html, out _);

        /// <summary>The page's award lists, as above, and its own game image from the same parse.</summary>
        public static IReadOnlyList<ExophaseAwardSection> ParseAwardSections(string html, out string gameArtUrl)
        {
            gameArtUrl = null;
            if (string.IsNullOrWhiteSpace(html))
            {
                return Array.Empty<ExophaseAwardSection>();
            }

            var doc = new HtmlDocument();
            doc.LoadHtml(html);
            return ParseAwardSectionsFrom(doc, out gameArtUrl);
        }

        /// <summary>The award lists and game image of an already loaded page.</summary>
        public static IReadOnlyList<ExophaseAwardSection> ParseAwardSectionsFrom(HtmlDocument doc, out string gameArtUrl)
        {
            gameArtUrl = GameArt(doc);
            var nodes = doc.DocumentNode.SelectNodes(
                "//h3[contains(concat(' ', normalize-space(@class), ' '), ' dlc-add-on ')]" +
                " | //ul[contains(@class,'achievement') or contains(@class,'trophy') or contains(@class,'challenge')]");
            if (nodes == null)
            {
                return Array.Empty<ExophaseAwardSection>();
            }

            var gameArt = ImageFileName(gameArtUrl);
            var sections = new List<ExophaseAwardSection>();
            string title = null;
            string art = null;
            foreach (var node in nodes)
            {
                if (string.Equals(node.Name, "h3", StringComparison.OrdinalIgnoreCase))
                {
                    var heading = WebUtility.HtmlDecode(node.InnerText ?? string.Empty).Trim();
                    title = ListWordSuffix.Replace(heading, string.Empty).Trim();
                    art = HeadingArt(node);
                    if (art != null && string.Equals(ImageFileName(art), gameArt, StringComparison.OrdinalIgnoreCase))
                    {
                        art = null;
                    }

                    continue;
                }

                var apiNames = (node.SelectNodes("./li") ?? Enumerable.Empty<HtmlNode>())
                    .Select(item => ExophaseApiClient.TryParseStableAwardId(item, out var id)
                        ? ExophaseApiClient.ExophaseStableApiNamePrefix + id.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        : null)
                    .Where(apiName => apiName != null)
                    .ToList();
                if (apiNames.Count > 0)
                {
                    sections.Add(new ExophaseAwardSection(string.IsNullOrWhiteSpace(title) ? null : title, art, apiNames));
                }

                title = null;
                art = null;
            }

            return sections;
        }

        /// <summary>
        /// The page's own platform as Exophase's slug for it, from the award list's
        /// <c>platform-</c> class ("platform-ps3"), which reads the same in every language; null
        /// when no list carries one.
        /// </summary>
        public static string ParsePagePlatformFrom(HtmlDocument doc)
        {
            var lists = doc?.DocumentNode.SelectNodes("//ul[contains(@class,'platform-')]");
            foreach (var list in lists ?? Enumerable.Empty<HtmlNode>())
            {
                var token = list.GetAttributeValue("class", string.Empty)
                    .Split((char[])null, StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault(part => part.StartsWith("platform-", StringComparison.OrdinalIgnoreCase) && part.Length > "platform-".Length);
                if (token != null)
                {
                    return token.Substring("platform-".Length).ToLowerInvariant();
                }
            }

            return null;
        }

        /// <summary>The page's own game image (its <c>og:image</c>); null when it has none.</summary>
        public static string GameArt(string html)
        {
            if (string.IsNullOrWhiteSpace(html))
            {
                return null;
            }

            var doc = new HtmlDocument();
            doc.LoadHtml(html);
            return GameArt(doc);
        }

        private static string GameArt(HtmlDocument doc)
        {
            var url = doc.DocumentNode
                .SelectSingleNode("//meta[@property='og:image']")
                ?.GetAttributeValue("content", null);
            return string.IsNullOrWhiteSpace(url) ? null : WebUtility.HtmlDecode(url).Trim();
        }

        /// <summary>The image the style after a DLC heading gives it.</summary>
        private static string HeadingArt(HtmlNode heading)
        {
            for (var sibling = heading.NextSibling; sibling != null; sibling = sibling.NextSibling)
            {
                if (sibling.NodeType != HtmlNodeType.Element)
                {
                    continue;
                }

                if (!string.Equals(sibling.Name, "style", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                var match = StyleImageUrl.Match(sibling.InnerText ?? string.Empty);
                return match.Success ? WebUtility.HtmlDecode(match.Groups[1].Value) : null;
            }

            return null;
        }

        /// <summary>An image address's file name, the same across Exophase's sizes ("b85e734.png").</summary>
        private static string ImageFileName(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return null;
            }

            var path = url.Split('?', '#')[0];
            var slash = path.LastIndexOf('/');
            return slash >= 0 ? path.Substring(slash + 1) : path;
        }

        // An award list of another game page; the price and language links carry an extra segment.
        private static readonly Regex AwardPagePath = new Regex(
            @"/game/([^/?#]+)/(?:achievements|trophies|challenges)/?$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Dictionary<string, string> TrophyLanguageCodes =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "arabic", "ar" },
                { "czech", "cs" },
                { "german", "de" },
                { "greek", "el" },
                { "spanish", "es" },
                { "french", "fr" },
                { "hungarian", "hu" },
                { "italian", "it" },
                { "japanese", "jp" },
                { "koreana", "kr" },
                { "dutch", "nl" },
                { "polish", "pl" },
                { "portuguese", "pt" },
                { "brazilian", "pt-BR" },
                { "schinese", "zh-CN" },
                { "tchinese", "zh-TW" },
                { "russian", "ru" },
                { "thai", "th" },
                { "turkish", "tr" }
            };

        /// <summary>
        /// The other platforms' pages for the same game. Links are read by their address rather
        /// than the "Also on" label, so a page in any language parses the same.
        /// </summary>
        public static IReadOnlyList<ExophaseRelatedPage> ParseRelatedPages(string html)
        {
            if (string.IsNullOrWhiteSpace(html))
            {
                return Array.Empty<ExophaseRelatedPage>();
            }

            var doc = new HtmlDocument();
            doc.LoadHtml(html);
            return ParseRelatedPagesFrom(doc);
        }

        /// <summary>The other platforms' pages named by an already loaded page.</summary>
        public static IReadOnlyList<ExophaseRelatedPage> ParseRelatedPagesFrom(HtmlDocument doc)
        {
            var links = doc.DocumentNode.SelectNodes("//dl[contains(concat(' ', normalize-space(@class), ' '), ' details ')]//dd//a[@href]");
            if (links == null)
            {
                return Array.Empty<ExophaseRelatedPage>();
            }

            return links
                .Select(link => new
                {
                    Match = AwardPagePath.Match(WebUtility.HtmlDecode(link.GetAttributeValue("href", string.Empty)).Trim()),
                    Platform = WebUtility.HtmlDecode(link.InnerText ?? string.Empty).Trim()
                })
                .Where(item => item.Match.Success && item.Platform.Length > 0)
                .Select(item => new ExophaseRelatedPage(item.Platform, item.Match.Groups[1].Value))
                .GroupBy(page => page.Slug, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
        }

        /// <summary>
        /// Exophase's path code for a trophy list in the plugin's language, or null for English
        /// and languages Exophase does not translate trophies into.
        /// </summary>
        public static string TrophyLanguageCode(string globalLanguage)
        {
            return !string.IsNullOrWhiteSpace(globalLanguage) &&
                   TrophyLanguageCodes.TryGetValue(globalLanguage.Trim(), out var code)
                ? code
                : null;
        }

        /// <summary>An award page address with the language path appended, or unchanged without a language.</summary>
        public static string LocalizeAwardPageUrl(string awardPageUrl, string languageCode)
        {
            if (string.IsNullOrWhiteSpace(awardPageUrl) || string.IsNullOrWhiteSpace(languageCode))
            {
                return awardPageUrl;
            }

            return awardPageUrl.TrimEnd('/') + "/" + languageCode + "/";
        }
    }
}
