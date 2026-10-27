using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Providers.Metadata.TrueGaming;
using PlayniteAchievements.Services.Achievements;
using System.Linq;

namespace PlayniteAchievements.Tests.Providers
{
    [TestClass]
    [TestCategory("Providers")]
    public class TrueGamingLinksTests
    {
        [DataTestMethod]
        [DataRow("Clair Obscur: Expedition 33", "Clair-Obscur-Expedition-33")]
        [DataRow("Fallout 4", "Fallout-4")]
        [DataRow("Marvel’s Spider-Man", "Marvels-Spider-Man")]
        [DataRow("Pokémon™ Legends", "Pokemon-Legends")]
        public void Slug_KeepsCase_AndHyphenatesPunctuation(string name, string slug)
        {
            Assert.AreEqual(slug, TrueGamingLinks.ToSlug(name));
        }

        [TestMethod]
        public void PlayStation_TriesTheNewestConsoleSuffixFirst()
        {
            var slugs = TrueGamingLinks.CandidateSlugs(new[] { "Elden Ring", "ELDEN RING" }, TrueGamingSite.For("PSN").Suffixes).ToList();

            CollectionAssert.AreEqual(
                new[] { "Elden-Ring", "Elden-Ring-PS5", "Elden-Ring-PS4", "Elden-Ring-PS3", "Elden-Ring-PS-Vita" },
                slugs);
        }

        [DataTestMethod]
        [DataRow("ta", "BioShock-Xbox-One", "Xbox One")]
        [DataRow("ta", "BioShock-Xbox-360", "Xbox 360")]
        [DataRow("ta", "BioShock", null)]
        [DataRow("tt", "BioShock-PS4", "PS4")]
        [DataRow("tt", "Persona-4-Golden-PS-Vita", "PS Vita")]
        [DataRow("tsa", "BioShock-Remastered", null)]
        public void PlatformOf_IsTheEditionEnding(string site, string pageSlug, string platform)
        {
            var found = TrueGamingSite.LookupOrder.Single(candidate => candidate.Prefix == site);
            Assert.AreEqual(platform, found.PlatformOf(pageSlug));
        }

        [DataTestMethod]
        [DataRow("bioshock-ps4", "PS4")]
        [DataRow("halo-5-xbox-one", "Xbox One")]
        [DataRow("fable-ii-xbox-360", "Xbox 360")]
        [DataRow("persona-4-golden-psvita", "PS Vita")]
        [DataRow("god-of-war-psn", null)]
        [DataRow("starfield-xbox", null)]
        [DataRow(null, null)]
        public void ExophasePlatformOfSlug_NamesOnlyOneConsoleEndings(string slug, string platform)
        {
            Assert.AreEqual(platform, PlayniteAchievements.Providers.Metadata.Exophase.ExophaseMetadataBranch.PlatformOfSlug(slug));
        }

        [DataTestMethod]
        [DataRow("Fallout-4", "Fallout-4")]
        [DataRow(" /game/Fallout-4/achievements ", "Fallout-4")]
        [DataRow("https://www.truetrophies.com/game/Elden-Ring-PS5/trophies", "Elden-Ring")]
        [DataRow("truesteamachievements.com/game/Hades/achievements?x=1", "Hades")]
        [DataRow("ta:ELDEN-RING-NIGHTREIGN, tt:ELDEN-RING-NIGHTREIGN, ELDEN-RING-NIGHTREIGN", "ELDEN-RING-NIGHTREIGN")]
        [DataRow("TT:Hades-PS4", "Hades")]
        [DataRow("Halo-Infinite-Xbox-One", "Halo-Infinite")]
        public void Links_AreOneNameForEverySite(string link, string slug)
        {
            Assert.AreEqual(slug, TrueGamingLinks.Normalize(link));
        }

        [TestMethod]
        public void Links_RejectBlankAndMalformedIds()
        {
            Assert.IsNull(TrueGamingLinks.Normalize("  "));
            Assert.IsNull(TrueGamingLinks.Normalize("two words"));
            Assert.IsNull(TrueGamingLinks.Normalize(null));
        }

        [TestMethod]
        public void Lookup_TriesEverySite_AndEachSitesEditionsOfTheName()
        {
            CollectionAssert.AreEqual(
                new[] { "trueachievements", "truetrophies", "truesteamachievements" },
                TrueGamingSite.LookupOrder.Select(site => site.Name).ToList());
            CollectionAssert.AreEqual(
                new[] { "Elden-Ring", "Elden-Ring-PS5", "Elden-Ring-PS4", "Elden-Ring-PS3", "Elden-Ring-PS-Vita" },
                TrueGamingLinks.SlugsFor(TrueGamingSite.TrueTrophies, "Elden-Ring").ToList());
            CollectionAssert.AreEqual(new[] { "Hades" }, TrueGamingLinks.SlugsFor(TrueGamingSite.TrueSteamAchievements, "Hades").ToList());
        }

        [TestMethod]
        public void SlugsToTry_AddsEditionsOnlyOnTheOwnSite_OrForAUserLink()
        {
            var own = TrueGamingSite.For("Steam");
            CollectionAssert.AreEqual(new[] { "Spyro" }, TrueGamingLinks.SlugsToTry(TrueGamingSite.TrueAchievements, own, "Spyro", userSet: false).ToList());
            CollectionAssert.AreEqual(
                new[] { "Spyro", "Spyro-Xbox-One", "Spyro-Xbox-360" },
                TrueGamingLinks.SlugsToTry(TrueGamingSite.TrueAchievements, TrueGamingSite.For("Xbox"), "Spyro", userSet: false).ToList());
            CollectionAssert.AreEqual(
                new[] { "Spyro", "Spyro-Xbox-One", "Spyro-Xbox-360" },
                TrueGamingLinks.SlugsToTry(TrueGamingSite.TrueAchievements, own, "Spyro", userSet: true).ToList());
        }
        [TestMethod]
        public void Flags_MapSynonymsToBuiltInTags_AndKeepTheRestApart()
        {
            var tags = AchievementTagHelper.Parse(TrueGamingFlags.ToTags(new[]
            {
                "Single Player", "Main Storyline", "Missable", "Cooperative", "Versus", "Buggy +", "Buggy -",
                "Online/Offline", "Partly Discontinued/Unobtainable"
            }));

            CollectionAssert.IsSubsetOf(
                new[] { "Singleplayer", "Progression", "Missable", "Cooperative", "Versus", "BuggyPlus", "BuggyMinus", "OnlineOffline", "PartlyDiscontinued", "Unobtainable" },
                tags.ToList());
        }

        [TestMethod]
        public void SiteSearch_ListsEachGamesSlugAndTitle()
        {
            // TrueAchievements' answer for "Fallout 4", as the browser shows it, shortened.
            const string text =
                "<SiteSearchController.SiteSearchResponse><ResultsXML><SearchResults><SearchString>Fallout 4</SearchString><TotalGames>2</TotalGames>" +
                "<Games><Game><GameID>5757</GameID><Title>Fallout 4</Title><Url>/game/Fallout-4/achievements</Url><MaxAchievements>84</MaxAchievements></Game>" +
                "<Game><GameID>9305</GameID><Title>Fallout 4 (Windows) &amp; More</Title><Url>/game/Fallout-4-Windows/achievements</Url></Game></Games>" +
                "</SearchResults></ResultsXML></SiteSearchController.SiteSearchResponse>";

            var games = TrueGamingLinks.ParseSearch(text);

            CollectionAssert.AreEqual(new[] { "Fallout-4", "Fallout-4-Windows" }, games.Select(game => game.Slug).ToList());
            Assert.AreEqual("Fallout 4 (Windows) & More", games[1].Title);
        }

        [TestMethod]
        public void SiteSearch_ReadsPlatforms_FromTheApiAndFromTheSearchPage()
        {
            // TrueTrophies' API answer for "Elden Ring" (2026-10-27), shortened.
            const string api =
                "<Games><Game><Title>ELDEN RING</Title><Url>/game/Elden-Ring/trophies</Url><PlatformsXML><Platforms><Platform><Name>PlayStation 5</Name></Platform></Platforms></PlatformsXML></Game>" +
                "<Game><Title>ELDEN RING NIGHTREIGN (PS4)</Title><Url>/game/ELDEN-RING-NIGHTREIGN-v2/trophies</Url><PlatformsXML><Platforms><Platform><Name>PlayStation 4</Name></Platform></Platforms></PlatformsXML></Game></Games>";
            // TrueSteamAchievements' search page for "Hades" (2026-10-27), one row.
            const string page =
                "<a class=\"sst-game-row\" href=\"/game/Hades/achievements\"><img class=\"sst-game-art\" src=\"/imagestore/2901193.jpg\" alt=\"\">" +
                "<span class=\"sst-game-info\"><span class=\"sst-game-name\"><span class=\"hl\">Hades</span></span><span class=\"sst-game-meta\"><span class=\"sst-stat\">49</span></span>" +
                "<span class=\"sst-game-platforms\"><span class=\"sst-platform-tag\" style=\"background:#6b7280\">Windows</span><span class=\"sst-platform-tag\">Mac OS X</span></span></span></a>";

            var tt = TrueGamingLinks.ParseSiteSearch(api);
            Assert.AreEqual(("ELDEN-RING-NIGHTREIGN-v2", "ELDEN RING NIGHTREIGN (PS4)", "PlayStation 4"), tt[1]);
            Assert.AreEqual("PlayStation 5", tt[0].Platforms);

            var tsa = TrueGamingLinks.ParseSiteSearch(page);
            Assert.AreEqual(("Hades", "Hades", "Windows, Mac OS X"), tsa.Single());
        }

        [TestMethod]
        public void SearchUrls_AskEachSiteTheWayItAnswersGames()
        {
            Assert.AreEqual(TrueGamingLinks.SearchUrl("Elden Ring"), TrueGamingLinks.SearchUrl(TrueGamingSite.TrueAchievements, "Elden Ring"));
            Assert.AreEqual(
                "https://www.truetrophies.com/true-api/sitesearch/search?q=Elden%20Ring&category=Games&site=tt",
                TrueGamingLinks.SearchUrl(TrueGamingSite.TrueTrophies, "Elden Ring"));
            Assert.AreEqual(
                "https://truesteamachievements.com/search-results?search=Hades",
                TrueGamingLinks.SearchUrl(TrueGamingSite.TrueSteamAchievements, "Hades"));
        }

        [TestMethod]
        public void PickedPage_KeepsItsSiteAndEdition()
        {
            Assert.AreEqual((TrueGamingSite.TrueTrophies, "Elden-Ring-PS4"), TrueGamingLinks.Picked("tt:Elden-Ring-PS4"));
            Assert.IsNull(TrueGamingLinks.Picked("Elden-Ring").Site);
            Assert.IsNull(TrueGamingLinks.Picked("ta:Hades, tt:Hades-PS5").Site, "a list stored by an earlier version");
            Assert.AreEqual("Elden-Ring", TrueGamingLinks.Normalize("tt:Elden-Ring-PS4"), "other sites read the page name");

            Assert.AreEqual("tt:Elden-Ring-PS4", TrueGamingLinks.PickedIdFromAddress("https://www.truetrophies.com/game/Elden-Ring-PS4/trophies"));
            Assert.AreEqual("tsa:Hades", TrueGamingLinks.PickedIdFromAddress("truesteamachievements.com/game/Hades/achievements"));
            Assert.IsNull(TrueGamingLinks.PickedIdFromAddress("Hades"));
        }

        [TestMethod]
        public void SiteSearch_WithNoGames_ListsNone()
        {
            Assert.AreEqual(0, TrueGamingLinks.ParseSearch("<SearchResults><TotalGames>0</TotalGames></SearchResults>").Count);
            Assert.AreEqual(0, TrueGamingLinks.ParseSearch(null).Count);
        }
    }
}
