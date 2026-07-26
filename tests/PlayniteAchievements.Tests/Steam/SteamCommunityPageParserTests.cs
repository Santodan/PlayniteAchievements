using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using PlayniteAchievements.Providers.Steam;
using System.Linq;

namespace PlayniteAchievements.Steam.Tests
{
    [TestClass]
    public class SteamCommunityPageParserTests
    {
        [TestMethod]
        public void ParseFriends_MapsSteamIdNameAndAvatar()
        {
            const string html =
                "<div id=\"friends_list\">" +
                "<a class=\"selectable friend_block_v2 persona online\" data-steamid=\"76561198000000001\" data-search=\"Fallback Name\" href=\"https://steamcommunity.com/profiles/76561198000000001/\">" +
                "<div class=\"player_avatar\"><img src=\"https://avatars.example/avatar.jpg\" /></div>" +
                "<div class=\"friend_block_content\">Display Name<br><span class=\"friend_small_text\">Online</span></div>" +
                "</a></div>";

            var friends = SteamCommunityPageParser.ParseFriends(html);

            Assert.AreEqual(1, friends.Count);
            Assert.AreEqual("76561198000000001", friends[0].SteamId);
            Assert.AreEqual("Display Name", friends[0].DisplayName);
            Assert.AreEqual("https://avatars.example/avatar.jpg", friends[0].AvatarUrl);
            Assert.IsTrue(SteamCommunityPageParser.LooksLikeFriendsPayload(html));
        }

        [TestMethod]
        public void ParseOwnedGames_MapsXmlPayload()
        {
            const string xml =
                "<gamesList><games>" +
                "<game><appID>440</appID><name>Team Fortress 2</name><hoursOnRecord>1.5</hoursOnRecord><hoursLast2Weeks>0.5</hoursLast2Weeks></game>" +
                "</games></gamesList>";

            var games = SteamCommunityPageParser.ParseOwnedGames(xml);

            Assert.AreEqual(1, games.Count);
            Assert.AreEqual(440, games[0].AppId);
            Assert.AreEqual("Team Fortress 2", games[0].Name);
            Assert.AreEqual(90, games[0].PlaytimeForever);
            Assert.AreEqual(30, games[0].Playtime2Weeks);
            Assert.IsTrue(SteamCommunityPageParser.LooksLikeOwnedGamesPayload(xml));
        }

        [TestMethod]
        public void ParseOwnedGames_ParsesAchievementProgressFromGamesPageRow()
        {
            // Mirrors the community games page row: a store/app link plus an ACHIEVEMENTS link with a
            // sibling "11/17" progress count. The count feeds the friend refresh unlock hint.
            const string html =
                "<div class=\"gameListRow\">" +
                "<a href=\"https://store.steampowered.com/app/431960\">Slay the Spire</a>" +
                "<div>" +
                "<a href=\"https://steamcommunity.com/profiles/76561198000000001/stats/431960/?tab=achievements\">ACHIEVEMENTS</a>" +
                "<span>11/17</span>" +
                "</div>" +
                "</div>";

            var game = SteamCommunityPageParser.ParseOwnedGames(html).Single();

            Assert.AreEqual(431960, game.AppId);
            Assert.AreEqual(11, game.AchievementsEarned);
            Assert.AreEqual(17, game.AchievementsTotal);
            Assert.IsTrue(SteamCommunityPageParser.LooksLikeOwnedGamesPayload(html));
        }

        [TestMethod]
        public void ParseOwnedGames_MapsEscapedXmlViewerPayload()
        {
            const string html =
                "<html><body><div id=\"webkit-xml-viewer-source-xml\">" +
                "&lt;gamesList&gt;&lt;games&gt;" +
                "&lt;game&gt;&lt;appID&gt;440&lt;/appID&gt;&lt;hoursOnRecord&gt;2.0&lt;/hoursOnRecord&gt;&lt;/game&gt;" +
                "&lt;/games&gt;&lt;/gamesList&gt;" +
                "</div></body></html>";

            var game = SteamCommunityPageParser.ParseOwnedGames(html).Single();

            Assert.AreEqual(440, game.AppId);
            Assert.AreEqual(120, game.PlaytimeForever);
            Assert.IsTrue(SteamCommunityPageParser.LooksLikeOwnedGamesPayload(html));
        }

        [TestMethod]
        public void ParseOwnedGames_MapsMostPlayedProfileXmlPayload()
        {
            const string xml =
                "<profile><mostPlayedGames><mostPlayedGame>" +
                "<gameLink><![CDATA[https://steamcommunity.com/app/730]]></gameLink>" +
                "<hoursPlayed>3.0</hoursPlayed><hoursOnRecord>3,222</hoursOnRecord>" +
                "</mostPlayedGame></mostPlayedGames></profile>";

            var game = SteamCommunityPageParser.ParseOwnedGames(xml).Single();

            Assert.AreEqual(730, game.AppId);
            Assert.AreEqual(193320, game.PlaytimeForever);
            Assert.AreEqual(180, game.Playtime2Weeks);
            Assert.IsTrue(SteamCommunityPageParser.LooksLikeOwnedGamesPayload(xml));
        }

        [TestMethod]
        public void ParseOwnedGames_MapsRgGamesPayload()
        {
            const string html =
                "<script>var rgGames = [{\"appid\":570,\"playtime_forever\":120,\"playtime_2weeks\":15,\"rtime_last_played\":1710000000}];</script>";

            var game = SteamCommunityPageParser.ParseOwnedGames(html).Single();

            Assert.AreEqual(570, game.AppId);
            Assert.AreEqual(120, game.PlaytimeForever);
            Assert.AreEqual(15, game.Playtime2Weeks);
            Assert.AreEqual(1710000000L, game.LastPlayedUnixSeconds);
            Assert.IsTrue(SteamCommunityPageParser.LooksLikeOwnedGamesPayload(html));
        }

        [TestMethod]
        public void ParseOwnedGames_MapsSteamSsrOwnedGamesQuery()
        {
            const string queryData =
                "{\"mutations\":[],\"queries\":[{\"queryKey\":[\"OwnedGames\",\"76561198087595485\",\"english\"]," +
                "\"state\":{\"data\":[{\"appid\":730,\"name\":\"Counter-Strike 2\",\"playtime_forever\":156469}," +
                "{\"appid\":550,\"name\":\"Left 4 Dead 2\",\"playtime_forever\":281,\"playtime_2weeks\":12,\"rtime_last_played\":1710000000}]}}]}";
            var renderContext = "{\"queryData\":" + JsonConvert.SerializeObject(queryData) + "}";
            var html = "<script>window.SSR.renderContext=JSON.parse(" +
                       JsonConvert.SerializeObject(renderContext) +
                       ");</script>";

            var games = SteamCommunityPageParser.ParseOwnedGames(html)
                .OrderBy(game => game.AppId)
                .ToList();

            Assert.AreEqual(2, games.Count);
            Assert.AreEqual(550, games[0].AppId);
            Assert.AreEqual("Left 4 Dead 2", games[0].Name);
            Assert.AreEqual(281, games[0].PlaytimeForever);
            Assert.AreEqual(12, games[0].Playtime2Weeks);
            Assert.AreEqual(1710000000L, games[0].LastPlayedUnixSeconds);
            Assert.AreEqual(730, games[1].AppId);
            Assert.AreEqual("Counter-Strike 2", games[1].Name);
            Assert.AreEqual(156469, games[1].PlaytimeForever);
            Assert.IsTrue(SteamCommunityPageParser.LooksLikeOwnedGamesPayload(html));
        }

        [TestMethod]
        public void ParseOwnedGames_MergesSteamSsrRenderContextAndLoaderData()
        {
            const string queryData =
                "{\"mutations\":[],\"queries\":[{\"queryKey\":[\"OwnedGames\",\"76561198087595485\",\"english\"]," +
                "\"state\":{\"data\":[{\"appid\":730,\"name\":\"Counter-Strike 2\",\"playtime_forever\":156469}]}}]}";
            var renderContext = "{\"queryData\":" + JsonConvert.SerializeObject(queryData) + "}";
            const string listData =
                "{\"listData\":{\"rgGames\":[{\"appid\":550,\"name\":\"Left 4 Dead 2\",\"playtime_forever\":281}]}}";
            var loaderData = JsonConvert.SerializeObject(new[] { listData });
            var html = "<script>window.SSR={};window.SSR.renderContext=JSON.parse(" +
                       JsonConvert.SerializeObject(renderContext) +
                       ");window.SSR.loaderData = " + loaderData + ";</script>";

            var games = SteamCommunityPageParser.ParseOwnedGames(html)
                .OrderBy(game => game.AppId)
                .ToList();

            Assert.AreEqual(2, games.Count);
            Assert.AreEqual(550, games[0].AppId);
            Assert.AreEqual(730, games[1].AppId);
        }

        [TestMethod]
        public void ParseOwnedGames_MapsNestedSteamSsrOwnedGamesAndAchievementProgress()
        {
            const string queryData =
                "{\"mutations\":[],\"queries\":[" +
                "{\"queryKey\":[\"OwnedGames\",\"76561198087595485\",\"english\"]," +
                "\"state\":{\"data\":{\"response\":{\"game_count\":2,\"games\":[" +
                "{\"appid\":440,\"name\":\"Team Fortress 2\",\"playtime_forever\":90}," +
                "{\"appid\":570,\"name\":\"Dota 2\",\"playtime_forever\":180}]}}}}," +
                "{\"queryKey\":[\"AchievementsProgress\",\"76561198087595485\"]," +
                "\"state\":{\"data\":{\"achievement_progress\":[" +
                "{\"appid\":440,\"unlocked\":0,\"total\":520}," +
                "{\"appid\":570,\"unlocked\":14,\"total\":100}]}}}]}";
            var renderContext = "{\"queryData\":" + JsonConvert.SerializeObject(queryData) + "}";
            var html = "<script>window.SSR.renderContext=JSON.parse(" +
                       JsonConvert.SerializeObject(renderContext) +
                       ");</script>";

            var games = SteamCommunityPageParser.ParseOwnedGames(html)
                .OrderBy(game => game.AppId)
                .ToList();

            Assert.AreEqual(2, games.Count);
            Assert.AreEqual(440, games[0].AppId);
            Assert.AreEqual(0, games[0].AchievementsEarned);
            Assert.AreEqual(520, games[0].AchievementsTotal);
            Assert.AreEqual(570, games[1].AppId);
            Assert.AreEqual(14, games[1].AchievementsEarned);
            Assert.AreEqual(100, games[1].AchievementsTotal);
            Assert.IsTrue(SteamCommunityPageParser.LooksLikeOwnedGamesPayload(html));
        }

        [TestMethod]
        public void ParseOwnedGames_MapsSteamSsrLoaderDataRecentlyPlayedFallback()
        {
            const string listData =
                "{\"listData\":{\"rgRecentlyPlayedGames\":[{\"appid\":2862420,\"name\":\"Tyr Playtest\"," +
                "\"playtime_forever\":111,\"playtime_2weeks\":111}]}}";
            var loaderData = JsonConvert.SerializeObject(new[] { "{}", listData });
            var html = "<script>window.SSR={};window.SSR.loaderData = " + loaderData + ";" +
                       "window.SSR.renderContext=JSON.parse(\"{}\");</script>";

            var game = SteamCommunityPageParser.ParseOwnedGames(html).Single();

            Assert.AreEqual(2862420, game.AppId);
            Assert.AreEqual("Tyr Playtest", game.Name);
            Assert.AreEqual(111, game.PlaytimeForever);
            Assert.AreEqual(111, game.Playtime2Weeks);
            Assert.IsTrue(SteamCommunityPageParser.LooksLikeOwnedGamesPayload(html));
        }

        [TestMethod]
        public void ParseOwnedGames_MergesPartialSsrWithRenderedRows()
        {
            const string listData =
                "{\"listData\":{\"rgRecentlyPlayedGames\":[{\"appid\":2862420,\"name\":\"Tyr Playtest\"," +
                "\"playtime_forever\":111,\"playtime_2weeks\":111}]}}";
            var loaderData = JsonConvert.SerializeObject(new[] { "{}", listData });
            var html = "<script>window.SSR={};window.SSR.loaderData = " + loaderData + ";</script>" +
                       "<div class=\"Panel\">" +
                       "<a href=\"https://store.steampowered.com/app/730\">Counter-Strike 2</a>" +
                       "<span>TOTAL PLAYED</span><span>2,607.8 hours</span>" +
                       "<a href=\"https://steamcommunity.com/id/test/stats/730/?tab=achievements\">ACHIEVEMENTS</a>" +
                       "</div>" +
                       "<div class=\"Panel\">" +
                       "<a href=\"https://store.steampowered.com/app/550\">Left 4 Dead 2</a>" +
                       "<span>TOTAL PLAYED</span><span>5 hours</span>" +
                       "</div>";

            var games = SteamCommunityPageParser.ParseOwnedGames(html)
                .OrderBy(game => game.AppId)
                .ToList();

            Assert.AreEqual(3, games.Count);
            Assert.AreEqual(550, games[0].AppId);
            Assert.AreEqual(300, games[0].PlaytimeForever);
            Assert.AreEqual(730, games[1].AppId);
            Assert.AreEqual(156468, games[1].PlaytimeForever);
            Assert.AreEqual(2862420, games[2].AppId);
            Assert.AreEqual(111, games[2].PlaytimeForever);
            Assert.AreEqual(111, games[2].Playtime2Weeks);
            Assert.IsTrue(SteamCommunityPageParser.LooksLikeOwnedGamesPayload(html));
        }

        [TestMethod]
        public void ParseOwnedGames_MapsModernHtmlRows()
        {
            const string html =
                "<div class=\"gameslistitems_GameListItem_abc\">" +
                "<a href=\"https://steamcommunity.com/app/72850\">The Elder Scrolls V: Skyrim</a>" +
                "<div>TOTAL PLAYED</div><div>1,549 hours</div>" +
                "<div>ACHIEVEMENTS</div><div>32/75</div>" +
                "</div>" +
                "<div class=\"gameslistitems_GameListItem_def\">" +
                "<a href=\"https://steamcommunity.com/app/292030/\">The Witcher 3: Wild Hunt</a>" +
                "<div>TOTAL PLAYED</div><div>476 hours</div>" +
                "</div>";

            var games = SteamCommunityPageParser.ParseOwnedGames(html)
                .OrderBy(game => game.AppId)
                .ToList();

            Assert.AreEqual(2, games.Count);
            Assert.AreEqual(72850, games[0].AppId);
            Assert.AreEqual("The Elder Scrolls V: Skyrim", games[0].Name);
            Assert.AreEqual(92940, games[0].PlaytimeForever);
            Assert.AreEqual(32, games[0].AchievementsEarned);
            Assert.AreEqual(75, games[0].AchievementsTotal);
            Assert.AreEqual(292030, games[1].AppId);
            Assert.AreEqual("The Witcher 3: Wild Hunt", games[1].Name);
            Assert.AreEqual(28560, games[1].PlaytimeForever);
            Assert.IsFalse(games[1].AchievementsEarned.HasValue);
            Assert.IsFalse(games[1].AchievementsTotal.HasValue);
            Assert.IsTrue(SteamCommunityPageParser.LooksLikeOwnedGamesPayload(html));
        }

        [TestMethod]
        public void ParseOwnedGames_ParsesFrenchCommaDecimalHoursOnRecord()
        {
            const string xml =
                "<gamesList><games>" +
                "<game><appID>440</appID><name>Team Fortress 2</name><hoursOnRecord>12,5</hoursOnRecord></game>" +
                "</games></gamesList>";

            var game = SteamCommunityPageParser.ParseOwnedGames(xml).Single();

            Assert.AreEqual(440, game.AppId);
            Assert.AreEqual(750, game.PlaytimeForever);
        }

        [TestMethod]
        public void ParseOwnedGames_ParsesSpaceThousandsWithCommaDecimalHoursOnRecord()
        {
            const string xml =
                "<gamesList><games>" +
                "<game><appID>440</appID><name>Team Fortress 2</name><hoursOnRecord>1 234,5</hoursOnRecord></game>" +
                "</games></gamesList>";

            var game = SteamCommunityPageParser.ParseOwnedGames(xml).Single();

            Assert.AreEqual(74070, game.PlaytimeForever);
        }

        [TestMethod]
        public void ParseOwnedGames_ParsesFrenchHtmlHoursUnit()
        {
            const string html =
                "<div class=\"gameslistitems_GameListItem_fr\">" +
                "<a href=\"https://steamcommunity.com/app/440\">Team Fortress 2</a>" +
                "<div>TOTAL PLAYED</div><div>12,5 heures</div>" +
                "</div>";

            var game = SteamCommunityPageParser.ParseOwnedGames(html).Single();

            Assert.AreEqual(440, game.AppId);
            Assert.AreEqual(750, game.PlaytimeForever);
        }

        [TestMethod]
        public void ParseOwnedGames_MapsModernHtmlRowsWithImageAppUrls()
        {
            const string html =
                "<div class=\"mtoll770TDI- Panel\" role=\"button\">" +
                "<img src=\"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/72850/header.jpg\" alt=\"Skyrim\" />" +
                "<span>The Elder Scrolls V: Skyrim</span>" +
                "<span>TOTAL PLAYED</span><span>1,549 hours</span>" +
                "</div>";

            var game = SteamCommunityPageParser.ParseOwnedGames(html).Single();

            Assert.AreEqual(72850, game.AppId);
            Assert.AreEqual("Skyrim", game.Name);
            Assert.AreEqual(92940, game.PlaytimeForever);
            Assert.IsTrue(SteamCommunityPageParser.LooksLikeOwnedGamesPayload(html));
        }

        [TestMethod]
        public void ParseOwnedGames_AppliesSsrPerGameAchievementProgressToLoaderDataGames()
        {
            // The modern games page delivers the owned list via loaderData and the per-game
            // achievement progress as individual React Query entries (one {appid,unlocked,total,...}
            // object each) in renderContext. The two sources must reconcile by appid so every game
            // with achievements gets its X/Y hint; games with total:0 get none.
            const string listData =
                "{\"listData\":{\"rgGames\":[" +
                "{\"appid\":440,\"name\":\"Team Fortress 2\",\"playtime_forever\":90}," +
                "{\"appid\":570,\"name\":\"Dota 2\",\"playtime_forever\":180}," +
                "{\"appid\":730,\"name\":\"Counter-Strike 2\",\"playtime_forever\":50}]}}";
            var loaderData = JsonConvert.SerializeObject(new[] { listData });

            const string queryData =
                "{\"mutations\":[],\"queries\":[" +
                "{\"state\":{\"data\":{\"appid\":440,\"unlocked\":11,\"total\":17,\"percentage\":64,\"all_unlocked\":false}}," +
                "\"queryKey\":[\"AchievementProgress\",440]}," +
                "{\"state\":{\"data\":{\"appid\":570,\"unlocked\":0,\"total\":0,\"percentage\":0,\"all_unlocked\":false}}," +
                "\"queryKey\":[\"AchievementProgress\",570]}," +
                "{\"state\":{\"data\":{\"appid\":730,\"unlocked\":75,\"total\":75,\"percentage\":100,\"all_unlocked\":true}}," +
                "\"queryKey\":[\"AchievementProgress\",730]}]}";
            var renderContext = "{\"queryData\":" + JsonConvert.SerializeObject(queryData) + "}";

            var html = "<script>window.SSR={};window.SSR.loaderData = " + loaderData + ";" +
                       "window.SSR.renderContext=JSON.parse(" + JsonConvert.SerializeObject(renderContext) + ");</script>";

            var games = SteamCommunityPageParser.ParseOwnedGames(html)
                .OrderBy(game => game.AppId)
                .ToList();

            Assert.AreEqual(3, games.Count);
            Assert.AreEqual(440, games[0].AppId);
            Assert.AreEqual(11, games[0].AchievementsEarned);
            Assert.AreEqual(17, games[0].AchievementsTotal);
            // total:0 game keeps a null hint so it is treated as achievement-less downstream.
            Assert.AreEqual(570, games[1].AppId);
            Assert.IsFalse(games[1].AchievementsTotal.HasValue && games[1].AchievementsTotal.Value > 0);
            Assert.AreEqual(730, games[2].AppId);
            Assert.AreEqual(75, games[2].AchievementsEarned);
            Assert.AreEqual(75, games[2].AchievementsTotal);
        }

        [TestMethod]
        public void ParseOwnedGames_AttributesAchievementHintByStatsAppIdWhenRowTextIsLocalized()
        {
            // Minified React row with a non-English playtime/achievements label and the achievements
            // anchor in a sibling container from the appid-bearing image. Row-text discovery cannot
            // scope the count here, so only the appid-keyed /stats/{appid} overlay can attribute it.
            const string html =
                "<div class=\"row- Panel\" role=\"button\">" +
                "<div class=\"cover-\"><img src=\"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/431960/header.jpg\" alt=\"Slay the Spire\" /></div>" +
                "<div class=\"stats-\">" +
                "<a href=\"https://steamcommunity.com/id/test/stats/431960/?tab=achievements\">SUCCÈS</a>" +
                "<span>21/72</span>" +
                "</div>" +
                "</div>";

            var game = SteamCommunityPageParser.ParseOwnedGames(html).Single();

            Assert.AreEqual(431960, game.AppId);
            Assert.AreEqual(21, game.AchievementsEarned);
            Assert.AreEqual(72, game.AchievementsTotal);
            Assert.IsTrue(SteamCommunityPageParser.LooksLikeOwnedGamesPayload(html));
        }
    }
}
