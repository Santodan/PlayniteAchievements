using HtmlAgilityPack;
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

        public static bool LooksLoggedOutStatsPayloadWithoutRows(string html, string finalUrl = null)
        {
            if (string.IsNullOrWhiteSpace(html))
            {
                return false;
            }

            var doc = TryParseHtmlDocument(html);
            if (!LooksLikeStatsPage(doc, finalUrl) || HasAchievementRowsInDom(doc))
            {
                return false;
            }

            var unauthSteamId = Regex.IsMatch(
                html,
                @"g_steamID\s*=\s*(?:false|""0""|0)\s*;",
                RegexOptions.IgnoreCase);

            return HasHeaderLoginLink(doc) || unauthSteamId || ContainsLoggedOutUserInfoFlag(html);
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
