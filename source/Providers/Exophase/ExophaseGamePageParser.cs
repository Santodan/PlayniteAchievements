using HtmlAgilityPack;
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

    /// <summary>
    /// Reads the game details of an Exophase award page (the "Game Info" list) and builds the
    /// localized trophy page addresses that list's "Languages" links follow.
    /// </summary>
    internal static class ExophaseGamePageParser
    {
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

        /// <summary>A PlayStation trophy page, in a language when <paramref name="languageCode"/> is given.</summary>
        public static string BuildTrophyPageUrl(string slug, string languageCode)
        {
            if (string.IsNullOrWhiteSpace(slug))
            {
                return null;
            }

            var url = $"https://www.exophase.com/game/{slug.Trim()}/trophies/";
            return string.IsNullOrWhiteSpace(languageCode) ? url : url + languageCode + "/";
        }
    }
}
