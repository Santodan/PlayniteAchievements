using HtmlAgilityPack;
using PlayniteAchievements.Providers.Steam.Models;
using System;
using System.Text.RegularExpressions;

namespace PlayniteAchievements.Providers.Steam
{
    internal static class SteamStatsPageClassifier
    {
        public static bool LooksUnauthenticatedStatsPayload(string html, string finalUrl = null)
        {
            if (string.IsNullOrWhiteSpace(html))
            {
                return false;
            }

            var doc = TryParseHtmlDocument(html);
            if (!LooksLikeStatsPage(doc, finalUrl))
            {
                return false;
            }

            // If achievement rows exist, this is not an unauthenticated stats payload.
            if (HasAchievementRowsInDom(doc))
            {
                return false;
            }

            var hasLoginLink = HasHeaderLoginLink(doc);
            var hasFatalStatsBlock = HasStatsFatalErrorBlock(doc);
            var unauthSteamId = Regex.IsMatch(
                html,
                @"g_steamID\s*=\s*(?:false|""0""|0)\s*;",
                RegexOptions.IgnoreCase);

            var loggedOutFlag = ContainsLoggedOutUserInfoFlag(html);

            return (unauthSteamId || loggedOutFlag) && (hasLoginLink || hasFatalStatsBlock);
        }

        public static bool LooksPrivateOrRestrictedStatsPayload(string html, string finalUrl = null)
        {
            if (string.IsNullOrWhiteSpace(html))
            {
                return false;
            }

            var doc = TryParseHtmlDocument(html);
            if (!LooksLikeStatsPage(doc, finalUrl))
            {
                return false;
            }

            if (HasAchievementRowsInDom(doc))
            {
                return false;
            }

            if (LooksUnauthenticatedStatsPayload(html, finalUrl))
            {
                return false;
            }

            return HasProfilePrivateMarkers(doc) || HasStatsFatalErrorBlock(doc);
        }

        public static bool LooksProfileNotFoundStatsPayload(string html, string finalUrl = null)
        {
            if (string.IsNullOrWhiteSpace(html))
            {
                return false;
            }

            var doc = TryParseHtmlDocument(html);
            if (!LooksLikeStatsPage(doc, finalUrl))
            {
                return false;
            }

            if (HasAchievementRowsInDom(doc))
            {
                return false;
            }

            if (HasStatsFatalErrorBlock(doc))
            {
                return false;
            }

            return HasStatsErrorContainer(doc) && !HasProfileHeaderMarkers(doc);
        }

        public static bool LooksStructurallyUnavailableStatsPayload(string html, string finalUrl = null)
        {
            if (string.IsNullOrWhiteSpace(html))
            {
                return false;
            }

            var doc = TryParseHtmlDocument(html);
            if (!LooksLikeStatsPage(doc, finalUrl))
            {
                return false;
            }

            if (HasAchievementRowsInDom(doc))
            {
                return false;
            }

            if (LooksUnauthenticatedStatsPayload(html, finalUrl) ||
                LooksPrivateOrRestrictedStatsPayload(html, finalUrl) ||
                LooksProfileNotFoundStatsPayload(html, finalUrl))
            {
                return false;
            }

            return HasStatsFatalErrorBlock(doc) || HasStatsErrorContainer(doc);
        }

        public static bool LooksLoggedOutHeader(string html)
        {
            if (string.IsNullOrWhiteSpace(html))
            {
                return false;
            }

            var doc = TryParseHtmlDocument(html);
            if (HasHeaderLoginLink(doc))
            {
                return true;
            }

            return Regex.IsMatch(
                html,
                @"<a[^>]+class\s*=\s*[""'][^""']*\bglobal_action_link\b[^""']*[""'][^>]+href\s*=\s*[""'][^""']*/login[^""']*[""']",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
        }

        public static bool HasOnlyHiddenAchievementRows(string html)
        {
            var doc = TryParseHtmlDocument(html);
            if (doc?.DocumentNode == null)
            {
                return false;
            }

            var nodes = doc.DocumentNode.SelectNodes("//div[contains(@class,'achieveRow')]") ??
                        doc.DocumentNode.SelectNodes("//div[contains(@class,'achieveTxtHolder')]") ??
                        doc.DocumentNode.SelectNodes("//*[contains(@class,'achievement') and (.//h3 or .//div[contains(@class,'achieveUnlockTime')])]");

            if (nodes == null || nodes.Count == 0)
            {
                return false;
            }

            var hasHiddenRow = false;
            foreach (var row in nodes)
            {
                var isHidden = row.SelectSingleNode(".//div[contains(@class,'achieveHiddenBox')]") != null;
                if (!isHidden)
                {
                    return false;
                }

                hasHiddenRow = true;
            }

            return hasHiddenRow;
        }

        public static int? TryGetHiddenRemainingCount(string html)
        {
            var doc = TryParseHtmlDocument(html);
            var box = doc?.DocumentNode?.SelectSingleNode("//div[contains(@class,'achieveHiddenBox')]");
            if (box == null)
            {
                return null;
            }

            // The surrounding "N hidden achievements remaining" text is localized; only the bare
            // ASCII digits are read. Locales with non-ASCII digits yield null, meaning no evidence.
            var match = Regex.Match(box.InnerText ?? string.Empty, "[0-9]+");
            return match.Success && int.TryParse(match.Value, out var count) ? count : (int?)null;
        }

        /// <summary>
        /// Decides whether an AllHidden scrape result proves the user has zero unlocks, so an
        /// empty unlock set can be stored instead of failing the game as unreadable.
        ///
        /// A visible schema achievement always renders a normal row (locked or unlocked), so an
        /// all-hidden page cannot occur unless every schema achievement is hidden. An unlocked
        /// hidden achievement renders as a full parseable row, so the scrape would have been
        /// classified Scraped rather than AllHidden. A hidden-remaining count that parses to a
        /// number different from the schema total means the page and schema describe different
        /// achievement sets, so confirmation is withheld and the conservative failure stands.
        /// </summary>
        public static bool ConfirmsAllHiddenZeroUnlocks(SchemaAndPercentages schema, int? hiddenRemainingCount)
        {
            var achievements = schema?.Achievements;
            if (achievements == null || achievements.Count == 0)
            {
                return false;
            }

            foreach (var achievement in achievements)
            {
                if (achievement == null || achievement.Hidden == 0)
                {
                    return false;
                }
            }

            return hiddenRemainingCount == null || hiddenRemainingCount == achievements.Count;
        }

        private static HtmlDocument TryParseHtmlDocument(string html)
        {
            if (string.IsNullOrWhiteSpace(html))
            {
                return null;
            }

            try
            {
                var doc = new HtmlDocument();
                doc.LoadHtml(html);
                return doc;
            }
            catch
            {
                return null;
            }
        }

        private static bool LooksLikeStatsPage(HtmlDocument doc, string finalUrl)
        {
            return HasStatsRouteInUrl(finalUrl) || HasStatsLayoutMarkers(doc);
        }

        private static bool HasStatsRouteInUrl(string finalUrl)
        {
            if (string.IsNullOrWhiteSpace(finalUrl))
            {
                return false;
            }

            if (!Uri.TryCreate(finalUrl, UriKind.Absolute, out var uri))
            {
                return false;
            }

            return uri.AbsolutePath.IndexOf("/stats/", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool HasStatsFatalErrorBlock(HtmlDocument doc)
        {
            if (doc?.DocumentNode == null)
            {
                return false;
            }

            return doc.DocumentNode.SelectSingleNode("//*[contains(@class,'profile_fatalerror')]") != null ||
                   doc.DocumentNode.SelectSingleNode("//*[contains(@class,'profile_fatalerror_message')]") != null;
        }

        private static bool HasStatsErrorContainer(HtmlDocument doc)
        {
            if (doc?.DocumentNode == null)
            {
                return false;
            }

            return doc.DocumentNode.SelectSingleNode("//*[contains(@class,'error_ctn')]") != null;
        }

        private static bool HasProfileHeaderMarkers(HtmlDocument doc)
        {
            if (doc?.DocumentNode == null)
            {
                return false;
            }

            return doc.DocumentNode.SelectSingleNode("//*[contains(@class,'profile_small_header_bg')]") != null ||
                   doc.DocumentNode.SelectSingleNode("//*[contains(@class,'profile_header_bg')]") != null;
        }

        private static bool HasProfilePrivateMarkers(HtmlDocument doc)
        {
            if (doc?.DocumentNode == null)
            {
                return false;
            }

            return doc.DocumentNode.SelectSingleNode("//body[contains(@class,'private_profile')]") != null ||
                   doc.DocumentNode.SelectSingleNode("//*[contains(@class,'profile_private_info')]") != null;
        }

        private static bool HasStatsLayoutMarkers(HtmlDocument doc)
        {
            if (doc?.DocumentNode == null)
            {
                return false;
            }

            return doc.DocumentNode.SelectSingleNode("//div[@id='mainContents']") != null ||
                   doc.DocumentNode.SelectSingleNode("//div[@id='topSummaryBoxContent']") != null ||
                   doc.DocumentNode.SelectSingleNode("//div[@id='personalAchieve']") != null ||
                   doc.DocumentNode.SelectSingleNode("//div[@id='tabs']") != null ||
                   doc.DocumentNode.SelectSingleNode("//link[contains(translate(@href,'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'),'playerstats_generic.css')]") != null;
        }

        private static bool HasAchievementRowsInDom(HtmlDocument doc)
        {
            if (doc?.DocumentNode == null)
            {
                return false;
            }

            return doc.DocumentNode.SelectSingleNode("//div[contains(@class,'achieveRow')]") != null;
        }

        private static bool HasHeaderLoginLink(HtmlDocument doc)
        {
            if (doc?.DocumentNode == null)
            {
                return false;
            }

            var links = doc.DocumentNode.SelectNodes("//a[contains(translate(@href,'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'), '/login')]");
            if (links == null || links.Count == 0)
            {
                return false;
            }

            foreach (var link in links)
            {
                var classes = link?.GetAttributeValue("class", string.Empty) ?? string.Empty;
                if (classes.IndexOf("global_action_link", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    classes.IndexOf("menuitem", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool ContainsLoggedOutUserInfoFlag(string html)
        {
            if (string.IsNullOrWhiteSpace(html))
            {
                return false;
            }

            var compact = html
                .Replace(" ", string.Empty)
                .Replace("\r", string.Empty)
                .Replace("\n", string.Empty)
                .Replace("\t", string.Empty);

            return compact.IndexOf("\"logged_in\":false", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   compact.IndexOf("&quot;logged_in&quot;:false", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
