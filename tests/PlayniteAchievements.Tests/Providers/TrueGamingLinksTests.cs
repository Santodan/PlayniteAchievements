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
        [DataRow("Fallout-4", "Xbox", "www.trueachievements.com", "Fallout-4")]
        [DataRow("/game/Fallout-4/achievements", "Steam", "truesteamachievements.com", "Fallout-4")]
        [DataRow("https://www.truetrophies.com/game/Elden-Ring/trophies", "Steam", "www.truetrophies.com", "Elden-Ring")]
        [DataRow("truesteamachievements.com/game/Hades/achievements?x=1", "PSN", "truesteamachievements.com", "Hades")]
        [DataRow("ta:ELDEN-RING-NIGHTREIGN", "Steam", "www.trueachievements.com", "ELDEN-RING-NIGHTREIGN")]
        [DataRow("TT:Elden-Ring-PS5", "Xbox", "www.truetrophies.com", "Elden-Ring-PS5")]
        public void Links_AcceptSlugsAndPastedAddresses(string link, string provider, string host, string slug)
        {
            Assert.IsTrue(TrueGamingLinks.TryParse(link, provider, out var site, out var parsed));
            Assert.AreEqual(host, site.Host);
            Assert.AreEqual(slug, parsed);
        }

        [TestMethod]
        public void Lookup_TriesEverySite_AndStoresOtherSitesAsAddresses()
        {
            CollectionAssert.AreEqual(
                new[] { "trueachievements", "truetrophies", "truesteamachievements" },
                TrueGamingSite.LookupOrder.Select(site => site.Name).ToList());

            var link = TrueGamingSite.TrueAchievements.LinkId("ELDEN-RING-NIGHTREIGN", "Steam");
            Assert.AreEqual("ta:ELDEN-RING-NIGHTREIGN", link);
            Assert.IsTrue(TrueGamingLinks.TryParse(link, "Steam", out var site, out var slug));
            Assert.AreSame(TrueGamingSite.TrueAchievements, site);
            Assert.AreEqual("ELDEN-RING-NIGHTREIGN", slug);
            Assert.AreEqual("Hades", TrueGamingSite.TrueSteamAchievements.LinkId("Hades", "Steam"));
        }

        [TestMethod]
        public void Links_NameOnePagePerSite_InLookupOrder()
        {
            var pages = TrueGamingLinks.ParsePages("tsa:Hades, tt:Hades-PS5,  Hades , ta:Other, two words", "Xbox");

            CollectionAssert.AreEqual(
                new[] { "trueachievements:Hades", "truetrophies:Hades-PS5", "truesteamachievements:Hades" },
                pages.Select(page => page.Site.Name + ":" + page.Slug).ToList(),
                "the bare slug is the platform's own site; a second page on a site is dropped");
            Assert.AreEqual("Hades, tt:Hades-PS5, tsa:Hades", TrueGamingLinks.Format(pages, "Xbox"));
            Assert.AreEqual("ta:Hades, tt:Hades-PS5, Hades", TrueGamingLinks.Format(pages, "Steam"));
        }

        [TestMethod]
        public void Links_RejectBlankAndMalformedIds()
        {
            Assert.IsFalse(TrueGamingLinks.TryParse("  ", "Steam", out _, out _));
            Assert.IsFalse(TrueGamingLinks.TryParse("two words", "Steam", out _, out _));
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
    }
}
