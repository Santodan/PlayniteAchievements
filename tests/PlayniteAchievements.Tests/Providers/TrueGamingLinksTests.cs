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
        public void SiteSearch_WithNoGames_ListsNone()
        {
            Assert.AreEqual(0, TrueGamingLinks.ParseSearch("<SearchResults><TotalGames>0</TotalGames></SearchResults>").Count);
            Assert.AreEqual(0, TrueGamingLinks.ParseSearch(null).Count);
        }
    }
}
