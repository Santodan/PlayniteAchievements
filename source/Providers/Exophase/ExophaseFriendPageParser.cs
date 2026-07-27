using HtmlAgilityPack;
using System;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;

namespace PlayniteAchievements.Providers.Exophase
{
    internal sealed class ExophaseProfileMetadata
    {
        public string DisplayName { get; set; }
        public string AvatarUrl { get; set; }
    }

    /// <summary>
    /// Parses the few Exophase pages the plugin still renders: the profile header
    /// (display name/avatar) and the game page header banner. Game libraries and
    /// unlocks come from the public JSON API, not from page scraping.
    /// </summary>
    internal static class ExophaseFriendPageParser
    {
        private static readonly Regex PlaytimeHoursMinutesRegex = new Regex(@"(?:(\d+(?:[.,]\d+)?)\s*h(?:ours?)?)?\s*(?:(\d+)\s*m(?:in(?:utes?)?)?)?", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex WhitespaceRunRegex = new Regex(@"\s+", RegexOptions.Compiled);

        public static ExophaseProfileMetadata ParseProfile(string html)
        {
            var doc = LoadDocument(html);
            if (doc?.DocumentNode == null)
            {
                return new ExophaseProfileMetadata();
            }

            var header = doc.DocumentNode.SelectSingleNode("//section[contains(@class, 'section-profile-header')]")
                ?? doc.DocumentNode;
            return new ExophaseProfileMetadata
            {
                DisplayName = FirstNonEmpty(
                    Clean(header.SelectSingleNode(".//div[contains(@class, 'column-username')]//h2")?.InnerText),
                    Clean(header.SelectSingleNode(".//h2")?.InnerText)),
                AvatarUrl = NormalizeUrl(FirstNonEmpty(
                    header.SelectSingleNode(".//div[contains(@class, 'avatar')]//img")?.GetAttributeValue("src", null),
                    header.SelectSingleNode(".//img[contains(@src, '/forums/data/avatars/')]")?.GetAttributeValue("src", null)))
            };
        }

        public static string ParseGameHeaderImageUrl(string html)
        {
            var doc = LoadDocument(html);
            if (doc?.DocumentNode == null)
            {
                return null;
            }

            return NormalizeUrl(FirstNonEmpty(
                ExophaseApiClient.ResolveImageUrl(doc.DocumentNode.SelectSingleNode("//div[contains(@class, 'col-game-information')]//a[contains(@class, 'image')]")),
                ExophaseApiClient.ResolveImageUrl(doc.DocumentNode.SelectSingleNode("//div[contains(@class, 'feature-header')]")),
                ExophaseApiClient.ResolveImageUrl(doc.DocumentNode.SelectSingleNode("//a[contains(@class, 'image')]"))));
        }

        // Parses a display playtime string ("51h 24m", "12,5 h") into minutes. The API's
        // playtimeUnits field is preferred; this is the fallback for rows that only carry
        // the display string.
        public static int ParsePlaytimeMinutes(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return 0;
            }

            var match = PlaytimeHoursMinutesRegex.Match(text);
            if (!match.Success)
            {
                return 0;
            }

            var total = 0;
            // Accept a comma decimal (e.g. French "12,5 h") by normalizing it to a dot before
            // parsing with the invariant culture.
            var hoursText = match.Groups[1].Value.Replace(',', '.');
            if (double.TryParse(hoursText, NumberStyles.Float, CultureInfo.InvariantCulture, out var hours))
            {
                total += (int)Math.Round(hours * 60);
            }

            if (int.TryParse(match.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes))
            {
                total += minutes;
            }

            return Math.Max(0, total);
        }

        private static HtmlDocument LoadDocument(string html)
        {
            if (string.IsNullOrWhiteSpace(html))
            {
                return null;
            }

            var doc = new HtmlDocument();
            doc.LoadHtml(html);
            return doc;
        }

        private static string NormalizeUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return null;
            }

            url = WebUtility.HtmlDecode(url.Trim());
            if (url.StartsWith("//", StringComparison.Ordinal))
            {
                return "https:" + url;
            }

            if (url.StartsWith("/", StringComparison.Ordinal))
            {
                return "https://www.exophase.com" + url;
            }

            return url;
        }

        private static string Clean(string value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? null
                : WhitespaceRunRegex.Replace(WebUtility.HtmlDecode(value), " ").Trim();
        }

        private static string FirstNonEmpty(params string[] values)
        {
            return values?.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        }
    }
}
