using HtmlAgilityPack;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PlayniteAchievements.Providers.Steam.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace PlayniteAchievements.Providers.Steam
{
    internal sealed class SteamCommunityFriend
    {
        public string SteamId { get; set; }
        public string DisplayName { get; set; }
        public string AvatarUrl { get; set; }
    }

    internal static class SteamCommunityPageParser
    {
        private static readonly Regex ProfileSteamIdPattern =
            new Regex(@"/profiles/(?<id>\d{17})(?:/|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex GamesJsonPattern =
            new Regex(@"(?:var|let|const)\s+rgGames\s*=\s*(?<json>\[.*?\]);", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

        private static readonly Regex AppPathPattern =
            new Regex("(?:^|[/:\"'])apps?/(?<id>\\d+)(?=$|[/?#\"'&<\\s])", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Achievement anchors on the games page link to /stats/{appid}. Keying the earned/total
        // hint off this numeric appid keeps the hint independent of display language and of the
        // localized "ACHIEVEMENTS" row text that FindLikelyGameRow otherwise relies on.
        private static readonly Regex StatsAppIdPattern =
            new Regex(@"/stats/(?<id>\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex AchievementRatioPattern =
            new Regex(@"(\d+)\s*/\s*(\d+)", RegexOptions.Compiled);

        public static bool LooksLikeFriendsPayload(string html)
        {
            return !string.IsNullOrWhiteSpace(html) &&
                   html.IndexOf("friends_list", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static List<SteamCommunityFriend> ParseFriends(string html)
        {
            var result = new List<SteamCommunityFriend>();
            if (string.IsNullOrWhiteSpace(html))
            {
                return result;
            }

            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            var nodes = doc.DocumentNode.SelectNodes(
                "//*[contains(concat(' ', normalize-space(@class), ' '), ' friend_block_v2 ')]");
            if (nodes == null || nodes.Count == 0)
            {
                return result;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var node in nodes)
            {
                var steamId = ExtractFriendSteamId(node);
                if (string.IsNullOrWhiteSpace(steamId) || !seen.Add(steamId))
                {
                    continue;
                }

                result.Add(new SteamCommunityFriend
                {
                    SteamId = steamId,
                    DisplayName = FirstNonEmpty(ExtractFriendName(node), steamId),
                    AvatarUrl = NormalizeText(node.SelectSingleNode(".//img")?.GetAttributeValue("src", null))
                });
            }

            return result;
        }

        public static bool LooksLikeOwnedGamesPayload(string html)
        {
            return !string.IsNullOrWhiteSpace(html) &&
                   (TryExtractXmlPayload(html, "gamesList") != null ||
                    TryExtractXmlPayload(html, "mostPlayedGames") != null ||
                    html.IndexOf("rgGames", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    html.IndexOf("games_list_rows", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    html.IndexOf("gameListRow", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    html.IndexOf("gameslistitems_", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    html.IndexOf("data-app", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    html.IndexOf("OwnedGames", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    html.IndexOf("playtime_forever", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    AppPathPattern.IsMatch(html));
        }

        public static List<SteamOwnedGame> ParseOwnedGames(string html)
        {
            var result = new Dictionary<int, SteamOwnedGame>();

            var fromXml = TryParseOwnedGamesXml(html);
            if (fromXml != null)
            {
                foreach (var game in fromXml)
                {
                    UpsertLargestPlaytime(result, game);
                }
            }

            var fromSteamSsr = ParseOwnedGamesSteamSsr(html);
            foreach (var game in fromSteamSsr)
            {
                UpsertLargestPlaytime(result, game);
            }

            var fromJson = ParseOwnedGamesJson(html);
            foreach (var game in fromJson)
            {
                UpsertLargestPlaytime(result, game);
            }

            foreach (var game in ParseOwnedGamesHtml(html))
            {
                UpsertLargestPlaytime(result, game);
            }

            // The modern games page embeds per-game achievement progress in the SSR React Query
            // cache (window.SSR.renderContext) as one {appid,unlocked,total,...} object per game,
            // covering the whole library, while the games themselves arrive via loaderData. This is
            // the authoritative, complete, locale-independent source for the "X/Y" hint, so apply it
            // across the merged result first.
            OverlayAchievementProgressFromSsr(result, html);

            // Fallback for the ~20 rows the page renders with a visible achievements anchor: key the
            // hint off the appid in each /stats/{appid} href. Fills only games the SSR pass missed.
            OverlayAchievementHintsByAppId(result, html);

            return result.Values.ToList();
        }

        private static void OverlayAchievementProgressFromSsr(Dictionary<int, SteamOwnedGame> result, string html)
        {
            if (result == null || result.Count == 0 || string.IsNullOrWhiteSpace(html) ||
                html.IndexOf("window.SSR", StringComparison.OrdinalIgnoreCase) < 0)
            {
                return;
            }

            var renderContextArgument = TryExtractJsonParseArgument(html, "window.SSR.renderContext");
            if (string.IsNullOrWhiteSpace(renderContextArgument))
            {
                return;
            }

            try
            {
                var renderContextJson = JsonConvert.DeserializeObject<string>(renderContextArgument);
                var renderContext = JObject.Parse(renderContextJson);
                var queryDataJson = renderContext["queryData"]?.Value<string>();
                if (string.IsNullOrWhiteSpace(queryDataJson))
                {
                    return;
                }

                // Each game's progress is an individual React Query entry
                // (state.data = {appid,unlocked,total,...}); a recursive walk collects them all.
                AddAchievementProgressFromToken(result, JObject.Parse(queryDataJson));
            }
            catch
            {
                // Malformed or absent SSR payload: leave whatever the other sources supplied.
            }
        }

        private static void OverlayAchievementHintsByAppId(Dictionary<int, SteamOwnedGame> result, string html)
        {
            if (result == null || result.Count == 0 || string.IsNullOrWhiteSpace(html) ||
                (html.IndexOf("tab=achievements", StringComparison.OrdinalIgnoreCase) < 0 &&
                 html.IndexOf("/stats/", StringComparison.OrdinalIgnoreCase) < 0))
            {
                return;
            }

            HtmlNodeCollection anchors;
            try
            {
                var doc = new HtmlDocument();
                doc.LoadHtml(html);
                anchors = doc.DocumentNode.SelectNodes(
                    "//a[contains(@href,'tab=achievements') or contains(@href,'/stats/')]");
            }
            catch
            {
                return;
            }

            if (anchors == null)
            {
                return;
            }

            foreach (var anchor in anchors)
            {
                var appId = ExtractStatsAppId(anchor.GetAttributeValue("href", null));
                if (appId <= 0 || !result.TryGetValue(appId, out var existing))
                {
                    continue;
                }

                // The SSR pass is authoritative; only fill games it did not cover.
                if (existing.AchievementsEarned.HasValue && existing.AchievementsTotal.HasValue)
                {
                    continue;
                }

                var (earned, total) = ExtractAchievementRatioNearAnchor(anchor);
                if (!earned.HasValue || !total.HasValue)
                {
                    continue;
                }

                result[appId] = new SteamOwnedGame
                {
                    AppId = existing.AppId,
                    Name = existing.Name,
                    PlaytimeForever = Math.Max(0, existing.PlaytimeForever),
                    Playtime2Weeks = existing.Playtime2Weeks,
                    LastPlayedUnixSeconds = existing.LastPlayedUnixSeconds,
                    AchievementsEarned = earned.Value,
                    AchievementsTotal = total.Value
                };
            }
        }

        private static int ExtractStatsAppId(string href)
        {
            if (string.IsNullOrWhiteSpace(href))
            {
                return 0;
            }

            var match = StatsAppIdPattern.Match(href);
            return match.Success
                ? ParseInt(match.Groups["id"].Value)
                : ExtractAppIdFromGameLink(href);
        }

        // Walks up from the achievements anchor to the nearest ancestor whose text contains an
        // "earned/total" ratio. Starting at the anchor and widening keeps the match scoped to the
        // same game row (rows are siblings), so it never picks up a neighbouring game's counts.
        private static (int? Earned, int? Total) ExtractAchievementRatioNearAnchor(HtmlNode anchor)
        {
            var node = anchor;
            for (var depth = 0; node != null && depth < 4; depth++, node = node.ParentNode)
            {
                var text = WebUtility.HtmlDecode(node.InnerText ?? string.Empty);
                var match = AchievementRatioPattern.Match(text);
                if (match.Success &&
                    int.TryParse(match.Groups[1].Value, out var earned) &&
                    int.TryParse(match.Groups[2].Value, out var total) &&
                    total > 0)
                {
                    return (Math.Max(0, earned), total);
                }
            }

            return (null, null);
        }

        private static List<SteamOwnedGame> ParseOwnedGamesSteamSsr(string html)
        {
            var result = new Dictionary<int, SteamOwnedGame>();
            if (string.IsNullOrWhiteSpace(html) ||
                (html.IndexOf("window.SSR", StringComparison.OrdinalIgnoreCase) < 0 &&
                 html.IndexOf("OwnedGames", StringComparison.OrdinalIgnoreCase) < 0))
            {
                return new List<SteamOwnedGame>();
            }

            foreach (var game in ParseOwnedGamesFromRenderContext(html))
            {
                UpsertLargestPlaytime(result, game);
            }

            foreach (var game in ParseOwnedGamesFromLoaderData(html))
            {
                UpsertLargestPlaytime(result, game);
            }

            return result.Values.ToList();
        }

        private static string ExtractFriendSteamId(HtmlNode node)
        {
            var steamId = NormalizeText(node?.GetAttributeValue("data-steamid", null));
            if (SteamWebAuthSession.NormalizeSteamId64(steamId) != null)
            {
                return steamId;
            }

            var href = NormalizeText(node?.GetAttributeValue("href", null));
            if (string.IsNullOrWhiteSpace(href))
            {
                href = NormalizeText(node?.SelectSingleNode(".//a")?.GetAttributeValue("href", null));
            }

            var match = string.IsNullOrWhiteSpace(href) ? null : ProfileSteamIdPattern.Match(href);
            return match?.Success == true
                ? SteamWebAuthSession.NormalizeSteamId64(match.Groups["id"].Value)
                : null;
        }

        private static string ExtractFriendName(HtmlNode node)
        {
            var contentNode = node.SelectSingleNode(
                ".//*[contains(concat(' ', normalize-space(@class), ' '), ' friend_block_content ')]");
            var directText = contentNode?.ChildNodes?
                .FirstOrDefault(child => child.NodeType == HtmlNodeType.Text && !string.IsNullOrWhiteSpace(child.InnerText));
            var fromDirectText = FirstTextLine(directText?.InnerText);
            if (!string.IsNullOrWhiteSpace(fromDirectText))
            {
                return fromDirectText;
            }

            var fromContent = FirstTextLine(contentNode?.InnerText);
            if (!string.IsNullOrWhiteSpace(fromContent))
            {
                return fromContent;
            }

            var dataSearch = FirstTextLine(node.GetAttributeValue("data-search", null));
            if (!string.IsNullOrWhiteSpace(dataSearch))
            {
                return dataSearch;
            }

            return FirstTextLine(node.SelectSingleNode(".//img")?.GetAttributeValue("alt", null));
        }

        private static List<SteamOwnedGame> TryParseOwnedGamesXml(string html)
        {
            var gamesListXml = TryExtractXmlPayload(html, "gamesList");
            if (!string.IsNullOrWhiteSpace(gamesListXml))
            {
                try
                {
                    var doc = XDocument.Parse(gamesListXml);
                    return doc.Descendants("game")
                        .Select(ToOwnedGame)
                        .Where(game => game != null)
                        .ToList();
                }
                catch
                {
                    return null;
                }
            }

            var mostPlayedXml = TryExtractXmlPayload(html, "mostPlayedGames");
            if (!string.IsNullOrWhiteSpace(mostPlayedXml))
            {
                try
                {
                    var doc = XDocument.Parse(mostPlayedXml);
                    return doc.Descendants("mostPlayedGame")
                        .Select(ToMostPlayedOwnedGame)
                        .Where(game => game != null)
                        .ToList();
                }
                catch
                {
                    return null;
                }
            }

            return null;
        }

        private static string TryExtractXmlPayload(string html, string rootElementName)
        {
            if (string.IsNullOrWhiteSpace(html) || string.IsNullOrWhiteSpace(rootElementName))
            {
                return null;
            }

            var direct = TryExtractXmlPayloadFromText(html, rootElementName);
            if (!string.IsNullOrWhiteSpace(direct))
            {
                return direct;
            }

            var decoded = WebUtility.HtmlDecode(html);
            var decodedDirect = TryExtractXmlPayloadFromText(decoded, rootElementName);
            if (!string.IsNullOrWhiteSpace(decodedDirect))
            {
                return decodedDirect;
            }

            try
            {
                var doc = new HtmlDocument();
                doc.LoadHtml(html);
                var xmlViewer = doc.GetElementbyId("webkit-xml-viewer-source-xml") ??
                                doc.DocumentNode.SelectSingleNode("//*[contains(@class,'webkit-xml-viewer-source-xml')]");
                var text = WebUtility.HtmlDecode(xmlViewer?.InnerText ?? string.Empty);
                return TryExtractXmlPayloadFromText(text, rootElementName);
            }
            catch
            {
                return null;
            }
        }

        private static string TryExtractXmlPayloadFromText(string text, string rootElementName)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            var startMatch = Regex.Match(
                text,
                $@"<{Regex.Escape(rootElementName)}(?:\s[^>]*)?>",
                RegexOptions.IgnoreCase);
            if (!startMatch.Success)
            {
                return null;
            }

            var endMatch = Regex.Match(
                text.Substring(startMatch.Index),
                $@"</{Regex.Escape(rootElementName)}>",
                RegexOptions.IgnoreCase);
            if (!endMatch.Success)
            {
                return null;
            }

            var endExclusive = startMatch.Index + endMatch.Index + endMatch.Length;
            return text.Substring(startMatch.Index, endExclusive - startMatch.Index);
        }

        private static SteamOwnedGame ToMostPlayedOwnedGame(XElement game)
        {
            var link = game?.Element("gameLink")?.Value;
            var appId = ExtractAppIdFromGameLink(link);
            if (appId <= 0)
            {
                return null;
            }

            return new SteamOwnedGame
            {
                AppId = appId,
                Name = NormalizeText(game.Element("gameName")?.Value),
                PlaytimeForever = HoursToMinutes(game.Element("hoursOnRecord")?.Value),
                Playtime2Weeks = NullableHoursToMinutes(game.Element("hoursPlayed")?.Value)
            };
        }

        private static int ExtractAppIdFromGameLink(string link)
        {
            if (string.IsNullOrWhiteSpace(link))
            {
                return 0;
            }

            var match = AppPathPattern.Match(link);
            return match.Success
                ? ParseInt(match.Groups["id"].Value)
                : 0;
        }

        private static SteamOwnedGame ToOwnedGame(XElement game)
        {
            var appId = ParseInt(game?.Element("appID")?.Value);
            if (appId <= 0)
            {
                return null;
            }

            return new SteamOwnedGame
            {
                AppId = appId,
                Name = NormalizeText(game.Element("name")?.Value),
                PlaytimeForever = HoursToMinutes(game.Element("hoursOnRecord")?.Value),
                Playtime2Weeks = NullableHoursToMinutes(game.Element("hoursLast2Weeks")?.Value)
            };
        }

        private static List<SteamOwnedGame> ParseOwnedGamesFromRenderContext(string html)
        {
            var result = new Dictionary<int, SteamOwnedGame>();
            var renderContextArgument = TryExtractJsonParseArgument(html, "window.SSR.renderContext");
            if (string.IsNullOrWhiteSpace(renderContextArgument))
            {
                return new List<SteamOwnedGame>();
            }

            try
            {
                var renderContextJson = JsonConvert.DeserializeObject<string>(renderContextArgument);
                var renderContext = JObject.Parse(renderContextJson);
                var queryDataJson = renderContext["queryData"]?.Value<string>();
                if (string.IsNullOrWhiteSpace(queryDataJson))
                {
                    return new List<SteamOwnedGame>();
                }

                var queryData = JObject.Parse(queryDataJson);
                var queries = (queryData["queries"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>()).ToList();
                foreach (var query in queries)
                {
                    var data = query["state"]?["data"];
                    if (IsOwnedGamesQuery(query["queryKey"]))
                    {
                        AddOwnedGamesFromToken(result, data);
                    }
                }

                foreach (var query in queries)
                {
                    AddAchievementProgressFromToken(result, query["state"]?["data"]);
                }
            }
            catch
            {
                return new List<SteamOwnedGame>();
            }

            return result.Values.ToList();
        }

        private static List<SteamOwnedGame> ParseOwnedGamesFromLoaderData(string html)
        {
            var result = new Dictionary<int, SteamOwnedGame>();
            var loaderDataJson = TryExtractJsonArrayAssignment(html, "window.SSR.loaderData");
            if (string.IsNullOrWhiteSpace(loaderDataJson))
            {
                return new List<SteamOwnedGame>();
            }

            try
            {
                foreach (var item in JArray.Parse(loaderDataJson))
                {
                    var itemJson = item.Type == JTokenType.String ? item.Value<string>() : item.ToString();
                    if (string.IsNullOrWhiteSpace(itemJson))
                    {
                        continue;
                    }

                    var itemObject = JObject.Parse(itemJson);
                    var listData = itemObject["listData"] as JObject;
                    if (listData == null)
                    {
                        continue;
                    }

                    AddOwnedGamesFromToken(result, listData);
                    AddAchievementProgressFromToken(result, listData);
                }
            }
            catch
            {
                return new List<SteamOwnedGame>();
            }

            return result.Values.ToList();
        }

        private static void AddOwnedGamesFromToken(Dictionary<int, SteamOwnedGame> result, JToken token)
        {
            if (result == null || token == null)
            {
                return;
            }

            if (token is JArray array && LooksLikeOwnedGamesArray(array))
            {
                AddGamesFromArray(result, array);
                return;
            }

            foreach (var child in token.Children())
            {
                AddOwnedGamesFromToken(result, child);
            }
        }

        private static void AddAchievementProgressFromToken(Dictionary<int, SteamOwnedGame> result, JToken token)
        {
            if (result == null || token == null)
            {
                return;
            }

            if (token is JArray array && LooksLikeAchievementProgressArray(array))
            {
                foreach (var item in array.OfType<JObject>())
                {
                    UpsertAchievementProgress(result, item);
                }

                return;
            }

            // A standalone progress record (one React Query entry per game on the modern page).
            if (token is JObject obj && IsAchievementProgressObject(obj))
            {
                UpsertAchievementProgress(result, obj);
                return;
            }

            foreach (var child in token.Children())
            {
                AddAchievementProgressFromToken(result, child);
            }
        }

        private static bool IsAchievementProgressObject(JObject item)
        {
            return item != null &&
                   item["appid"] != null &&
                   item["total"] != null &&
                   item["unlocked"] != null;
        }

        private static void AddGamesFromArray(Dictionary<int, SteamOwnedGame> result, JArray games)
        {
            if (result == null || games == null)
            {
                return;
            }

            foreach (var gameToken in games)
            {
                var game = ToOwnedGame(gameToken as JObject);
                if (game != null)
                {
                    UpsertLargestPlaytime(result, game);
                }
            }
        }

        private static bool LooksLikeOwnedGamesArray(JArray array)
        {
            return array != null &&
                   array.OfType<JObject>().Any(item =>
                       item["appid"] != null &&
                       (item["playtime_forever"] != null ||
                        item["playtime_2weeks"] != null ||
                        item["has_community_visible_stats"] != null));
        }

        private static bool LooksLikeAchievementProgressArray(JArray array)
        {
            return array != null &&
                   array.OfType<JObject>().Any(item =>
                       item["appid"] != null &&
                       item["total"] != null &&
                       item["unlocked"] != null);
        }

        private static bool IsOwnedGamesQuery(JToken queryKey)
        {
            var array = queryKey as JArray;
            if (array != null && array.Count > 0)
            {
                return string.Equals(array[0]?.Value<string>(), "OwnedGames", StringComparison.OrdinalIgnoreCase);
            }

            return string.Equals(queryKey?.Value<string>(), "OwnedGames", StringComparison.OrdinalIgnoreCase);
        }

        private static SteamOwnedGame ToOwnedGame(JObject game)
        {
            var appId = ReadInt(game?["appid"]) ?? ReadInt(game?["appID"]) ?? 0;
            if (appId <= 0)
            {
                return null;
            }

            var playtimeForever = ReadInt(game["playtime_forever"]) ??
                                  ReadInt(game["playtimeForever"]) ??
                                  0;
            var playtime2Weeks = ReadInt(game["playtime_2weeks"]) ??
                                 ReadInt(game["playtime2Weeks"]);

            return new SteamOwnedGame
            {
                AppId = appId,
                Name = NormalizeText(FirstNonEmpty(
                    game["name"]?.Value<string>(),
                    game["display_name"]?.Value<string>(),
                    game["title"]?.Value<string>())),
                PlaytimeForever = Math.Max(0, playtimeForever),
                Playtime2Weeks = playtime2Weeks.HasValue ? Math.Max(0, playtime2Weeks.Value) : (int?)null,
                LastPlayedUnixSeconds = ReadLong(game["rtime_last_played"]) ?? ReadLong(game["last_played"]),
                AchievementsEarned = ReadInt(game["achievements_unlocked"]) ?? ReadInt(game["unlocked"]),
                AchievementsTotal = ReadInt(game["achievements_total"]) ?? ReadInt(game["total_achievements"]) ?? ReadInt(game["total"])
            };
        }

        private static string TryExtractJsonParseArgument(string html, string marker)
        {
            if (string.IsNullOrWhiteSpace(html) || string.IsNullOrWhiteSpace(marker))
            {
                return null;
            }

            var markerIndex = html.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex < 0)
            {
                return null;
            }

            var parseIndex = html.IndexOf("JSON.parse", markerIndex, StringComparison.OrdinalIgnoreCase);
            if (parseIndex < 0)
            {
                return null;
            }

            var openParenIndex = html.IndexOf('(', parseIndex);
            if (openParenIndex < 0)
            {
                return null;
            }

            var stringStartIndex = html.IndexOf('"', openParenIndex);
            return TryExtractJsonStringLiteral(html, stringStartIndex);
        }

        private static string TryExtractJsonArrayAssignment(string html, string marker)
        {
            if (string.IsNullOrWhiteSpace(html) || string.IsNullOrWhiteSpace(marker))
            {
                return null;
            }

            var markerIndex = html.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex < 0)
            {
                return null;
            }

            var equalsIndex = html.IndexOf('=', markerIndex);
            if (equalsIndex < 0)
            {
                return null;
            }

            var arrayStartIndex = html.IndexOf('[', equalsIndex);
            return TryExtractBalancedJson(html, arrayStartIndex, '[', ']');
        }

        private static string TryExtractJsonStringLiteral(string text, int startIndex)
        {
            if (string.IsNullOrEmpty(text) ||
                startIndex < 0 ||
                startIndex >= text.Length ||
                text[startIndex] != '"')
            {
                return null;
            }

            var escaped = false;
            for (var i = startIndex + 1; i < text.Length; i++)
            {
                var ch = text[i];
                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (ch == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (ch == '"')
                {
                    return text.Substring(startIndex, i - startIndex + 1);
                }
            }

            return null;
        }

        private static string TryExtractBalancedJson(string text, int startIndex, char openChar, char closeChar)
        {
            if (string.IsNullOrEmpty(text) ||
                startIndex < 0 ||
                startIndex >= text.Length ||
                text[startIndex] != openChar)
            {
                return null;
            }

            var depth = 0;
            var inString = false;
            var escaped = false;
            for (var i = startIndex; i < text.Length; i++)
            {
                var ch = text[i];
                if (inString)
                {
                    if (escaped)
                    {
                        escaped = false;
                    }
                    else if (ch == '\\')
                    {
                        escaped = true;
                    }
                    else if (ch == '"')
                    {
                        inString = false;
                    }

                    continue;
                }

                if (ch == '"')
                {
                    inString = true;
                }
                else if (ch == openChar)
                {
                    depth++;
                }
                else if (ch == closeChar)
                {
                    depth--;
                    if (depth == 0)
                    {
                        return text.Substring(startIndex, i - startIndex + 1);
                    }
                }
            }

            return null;
        }

        private static int? ReadInt(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null || token.Type == JTokenType.Undefined)
            {
                return null;
            }

            if (token.Type == JTokenType.Integer)
            {
                return token.Value<int>();
            }

            if (token.Type == JTokenType.Float)
            {
                return (int)Math.Round(token.Value<decimal>(), MidpointRounding.AwayFromZero);
            }

            return int.TryParse(token.Value<string>(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : (int?)null;
        }

        private static long? ReadLong(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null || token.Type == JTokenType.Undefined)
            {
                return null;
            }

            if (token.Type == JTokenType.Integer)
            {
                return token.Value<long>();
            }

            return long.TryParse(token.Value<string>(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : (long?)null;
        }

        private static List<SteamOwnedGame> ParseOwnedGamesJson(string html)
        {
            var result = new List<SteamOwnedGame>();
            if (string.IsNullOrWhiteSpace(html))
            {
                return result;
            }

            var match = GamesJsonPattern.Match(html);
            if (!match.Success)
            {
                return result;
            }

            try
            {
                var games = JsonConvert.DeserializeObject<List<SteamCommunityGameJson>>(match.Groups["json"].Value) ??
                            new List<SteamCommunityGameJson>();
                return games
                    .Where(game => game != null && game.AppId > 0)
                    .Select(game => new SteamOwnedGame
                    {
                        AppId = game.AppId,
                        Name = NormalizeText(game.Name),
                        PlaytimeForever = Math.Max(0, game.PlaytimeForever ?? 0),
                        Playtime2Weeks = game.Playtime2Weeks.HasValue ? Math.Max(0, game.Playtime2Weeks.Value) : (int?)null,
                        LastPlayedUnixSeconds = game.LastPlayedUnixSeconds ?? game.LastPlayed
                    })
                    .ToList();
            }
            catch
            {
                return result;
            }
        }

        private static List<SteamOwnedGame> ParseOwnedGamesHtml(string html)
        {
            var result = new Dictionary<int, SteamOwnedGame>();
            if (string.IsNullOrWhiteSpace(html) ||
                (html.IndexOf("data-app", StringComparison.OrdinalIgnoreCase) < 0 &&
                 !AppPathPattern.IsMatch(html)))
            {
                return new List<SteamOwnedGame>();
            }

            try
            {
                var doc = new HtmlDocument();
                doc.LoadHtml(html);
                var appNodes = doc.DocumentNode.SelectNodes(
                    "//*[@href or @src or @data-ds-appid or @data-appid or @data-app-id]");
                if (appNodes == null)
                {
                    return new List<SteamOwnedGame>();
                }

                foreach (var node in appNodes)
                {
                    var appId = ExtractAppIdFromNode(node);
                    if (appId <= 0)
                    {
                        continue;
                    }

                    var row = FindLikelyGameRow(node);
                    var rowText = NormalizeWhitespace(WebUtility.HtmlDecode(row?.InnerText ?? node.ParentNode?.InnerText ?? string.Empty));
                    var playtime = ExtractPlaytimeMinutes(rowText);
                    var (achievementsEarned, achievementsTotal) = ExtractAchievementCounts(row);
                    UpsertLargestPlaytime(result, new SteamOwnedGame
                    {
                        AppId = appId,
                        Name = ExtractGameNameFromNode(node, row),
                        PlaytimeForever = playtime ?? 0,
                        AchievementsEarned = achievementsEarned,
                        AchievementsTotal = achievementsTotal
                    });
                }
            }
            catch
            {
                return new List<SteamOwnedGame>();
            }

            return result.Values.ToList();
        }

        private static int ExtractAppIdFromNode(HtmlNode node)
        {
            if (node == null)
            {
                return 0;
            }

            foreach (var attributeName in new[] { "data-ds-appid", "data-appid", "data-app-id" })
            {
                var appId = ParseInt(node.GetAttributeValue(attributeName, null));
                if (appId > 0)
                {
                    return appId;
                }
            }

            foreach (var attributeName in new[] { "href", "src" })
            {
                var appId = ExtractAppIdFromGameLink(node.GetAttributeValue(attributeName, null));
                if (appId > 0)
                {
                    return appId;
                }
            }

            return 0;
        }

        private static HtmlNode FindLikelyGameRow(HtmlNode link)
        {
            var node = link;
            for (var depth = 0; node != null && depth < 8; depth++, node = node.ParentNode)
            {
                var className = node.GetAttributeValue("class", string.Empty);
                var id = node.GetAttributeValue("id", string.Empty);
                var text = node.InnerText ?? string.Empty;
                if (className.IndexOf("gameListRow", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    className.IndexOf("gameslistitems_", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    id.StartsWith("game_", StringComparison.OrdinalIgnoreCase) ||
                    text.IndexOf("TOTAL PLAYED", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    text.IndexOf("ACHIEVEMENTS", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return node;
                }
            }

            node = link;
            for (var depth = 0; node != null && depth < 8; depth++, node = node.ParentNode)
            {
                var className = node.GetAttributeValue("class", string.Empty);
                var id = node.GetAttributeValue("id", string.Empty);
                if (className.IndexOf("gameListRow", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    className.IndexOf("gameslistitems_", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    id.StartsWith("game_", StringComparison.OrdinalIgnoreCase))
                {
                    return node;
                }
            }

            return link.ParentNode;
        }

        // Time-unit words for the localized "hours played" cell on scraped community pages. Covers
        // the common Steam client languages; a bare "h" catches abbreviated forms like "12h".
        private const string HourUnitPattern = @"hours?|hrs?|heures?|stunden|std\.?|horas?|ore|uur|timer|hodin\w*|h\b";

        private static int? ExtractPlaytimeMinutes(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            var totalMatch = Regex.Match(
                text,
                @"TOTAL\s+PLAYED\s*(?<hours>[\d.,]+)\s*(?:" + HourUnitPattern + @")",
                RegexOptions.IgnoreCase);
            if (totalMatch.Success)
            {
                return NullableHoursToMinutes(totalMatch.Groups["hours"].Value);
            }

            var genericMatch = Regex.Match(
                text,
                @"(?<hours>[\d.,]+)\s*(?:" + HourUnitPattern + @")",
                RegexOptions.IgnoreCase);
            return genericMatch.Success
                ? NullableHoursToMinutes(genericMatch.Groups["hours"].Value)
                : null;
        }

        // Reads the "earned/total" achievement count from a games-page row. The React page renders
        // it as an ACHIEVEMENTS link followed by a sibling "11/17" span; scope the search to the
        // achievements anchor's container to avoid picking up unrelated numbers, falling back to the
        // whole row. Returns (null, null) when the row exposes no achievement progress.
        private static (int? Earned, int? Total) ExtractAchievementCounts(HtmlNode row)
        {
            if (row == null)
            {
                return (null, null);
            }

            var anchor = row.SelectSingleNode(
                ".//a[contains(@href, 'tab=achievements')]") ??
                row.SelectSingleNode(".//a[contains(@href, '/stats/')]");
            var scope = anchor?.ParentNode ?? row;
            var text = WebUtility.HtmlDecode(scope.InnerText ?? string.Empty);

            var match = Regex.Match(text, @"(\d+)\s*/\s*(\d+)");
            if (match.Success &&
                int.TryParse(match.Groups[1].Value, out var earned) &&
                int.TryParse(match.Groups[2].Value, out var total))
            {
                return (Math.Max(0, earned), Math.Max(0, total));
            }

            return (null, null);
        }

        private static string NormalizeWhitespace(string value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? string.Empty
                : Regex.Replace(value, @"\s+", " ").Trim();
        }

        private static void UpsertLargestPlaytime(Dictionary<int, SteamOwnedGame> target, SteamOwnedGame game)
        {
            if (target == null || game == null || game.AppId <= 0)
            {
                return;
            }

            if (!target.TryGetValue(game.AppId, out var existing))
            {
                target[game.AppId] = game;
                return;
            }

            var primary = game.PlaytimeForever > existing.PlaytimeForever ? game : existing;
            var secondary = ReferenceEquals(primary, game) ? existing : game;
            target[game.AppId] = new SteamOwnedGame
            {
                AppId = primary.AppId,
                Name = FirstNonEmpty(primary.Name, secondary.Name),
                PlaytimeForever = Math.Max(0, primary.PlaytimeForever),
                Playtime2Weeks = primary.Playtime2Weeks ?? secondary.Playtime2Weeks,
                LastPlayedUnixSeconds = primary.LastPlayedUnixSeconds ?? secondary.LastPlayedUnixSeconds,
                // The achievement hint is sourced from the rendered HTML row, which is often a
                // different source than the playtime-bearing list entry; coalesce so it is not
                // dropped when rows for the same app are merged.
                AchievementsEarned = primary.AchievementsEarned ?? secondary.AchievementsEarned,
                AchievementsTotal = primary.AchievementsTotal ?? secondary.AchievementsTotal
            };
        }

        private static void UpsertAchievementProgress(Dictionary<int, SteamOwnedGame> target, JObject progress)
        {
            var appId = ReadInt(progress?["appid"]) ?? 0;
            if (target == null || appId <= 0)
            {
                return;
            }

            var earned = ReadInt(progress["unlocked"]);
            var total = ReadInt(progress["total"]);
            if (!earned.HasValue || !total.HasValue)
            {
                return;
            }

            if (!target.TryGetValue(appId, out var existing))
            {
                return;
            }

            target[appId] = new SteamOwnedGame
            {
                AppId = existing.AppId,
                Name = existing.Name,
                PlaytimeForever = Math.Max(0, existing.PlaytimeForever),
                Playtime2Weeks = existing.Playtime2Weeks,
                LastPlayedUnixSeconds = existing.LastPlayedUnixSeconds,
                AchievementsEarned = existing.AchievementsEarned ?? Math.Max(0, earned.Value),
                AchievementsTotal = existing.AchievementsTotal ?? Math.Max(0, total.Value)
            };
        }

        private static string ExtractGameNameFromNode(HtmlNode node, HtmlNode row)
        {
            var direct = FirstNonEmpty(
                node?.GetAttributeValue("title", null),
                node?.GetAttributeValue("alt", null),
                node?.SelectSingleNode(".//img")?.GetAttributeValue("alt", null));
            if (!string.IsNullOrWhiteSpace(direct))
            {
                return NormalizeWhitespace(WebUtility.HtmlDecode(direct));
            }

            var linkText = FirstTextLine(node?.InnerText);
            if (!string.IsNullOrWhiteSpace(linkText) &&
                linkText.IndexOf("TOTAL PLAYED", StringComparison.OrdinalIgnoreCase) < 0 &&
                linkText.IndexOf("ACHIEVEMENTS", StringComparison.OrdinalIgnoreCase) < 0)
            {
                return linkText;
            }

            var text = NormalizeWhitespace(WebUtility.HtmlDecode(row?.InnerText ?? string.Empty));
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            var totalIndex = text.IndexOf("TOTAL PLAYED", StringComparison.OrdinalIgnoreCase);
            if (totalIndex > 0)
            {
                return text.Substring(0, totalIndex).Trim();
            }

            var achievementsIndex = text.IndexOf("ACHIEVEMENTS", StringComparison.OrdinalIgnoreCase);
            return achievementsIndex > 0
                ? text.Substring(0, achievementsIndex).Trim()
                : null;
        }

        private static string FirstTextLine(string value)
        {
            var decoded = WebUtility.HtmlDecode(value ?? string.Empty);
            if (string.IsNullOrWhiteSpace(decoded))
            {
                return null;
            }

            return decoded
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(NormalizeText)
                .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line));
        }

        private static string NormalizeText(string value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? null
                : WebUtility.HtmlDecode(value).Trim();
        }

        private static string FirstNonEmpty(params string[] values)
        {
            return values?.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        }

        private static int ParseInt(string value)
        {
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : 0;
        }

        private static int HoursToMinutes(string value)
        {
            return NullableHoursToMinutes(value) ?? 0;
        }

        private static int? NullableHoursToMinutes(string value)
        {
            var normalized = NormalizeNumericString(value);
            if (!decimal.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var hours))
            {
                return null;
            }

            return Math.Max(0, (int)Math.Round(hours * 60m, MidpointRounding.AwayFromZero));
        }

        /// <summary>
        /// Normalizes a locale-formatted number to an invariant "1234.5" form. Handles comma
        /// decimals (French "12,5"), NBSP/space thousands separators ("1 234,5"), and mixed
        /// dot/comma grouping ("1.234,5" / "1,234.5"). The right-most separator is treated as the
        /// decimal point when both a dot and comma are present; a lone comma is a decimal separator
        /// only when it looks like a fractional part (1-2 trailing digits), otherwise it is a
        /// thousands grouping.
        /// </summary>
        private static string NormalizeNumericString(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return value;
            }

            // Space, non-breaking space, and narrow no-break space are thousands separators.
            var cleaned = value.Trim()
                .Replace(" ", string.Empty)
                .Replace(" ", string.Empty)
                .Replace(" ", string.Empty);

            var hasDot = cleaned.IndexOf('.') >= 0;
            var hasComma = cleaned.IndexOf(',') >= 0;

            if (hasDot && hasComma)
            {
                if (cleaned.LastIndexOf('.') > cleaned.LastIndexOf(','))
                {
                    cleaned = cleaned.Replace(",", string.Empty);
                }
                else
                {
                    cleaned = cleaned.Replace(".", string.Empty).Replace(',', '.');
                }
            }
            else if (hasComma)
            {
                var firstComma = cleaned.IndexOf(',');
                var lastComma = cleaned.LastIndexOf(',');
                var trailing = cleaned.Length - lastComma - 1;
                if (firstComma == lastComma && trailing >= 1 && trailing <= 2)
                {
                    cleaned = cleaned.Replace(',', '.');
                }
                else
                {
                    cleaned = cleaned.Replace(",", string.Empty);
                }
            }

            return cleaned;
        }

        private sealed class SteamCommunityGameJson
        {
            [JsonProperty("appid")]
            public int AppId { get; set; }

            [JsonProperty("name")]
            public string Name { get; set; }

            [JsonProperty("playtime_forever")]
            public int? PlaytimeForever { get; set; }

            [JsonProperty("playtime_2weeks")]
            public int? Playtime2Weeks { get; set; }

            [JsonProperty("rtime_last_played")]
            public long? LastPlayedUnixSeconds { get; set; }

            [JsonProperty("last_played")]
            public long? LastPlayed { get; set; }
        }
    }
}
