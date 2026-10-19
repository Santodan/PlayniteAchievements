using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Providers.PSN;
using System;
using System.IO;
using System.Linq;

namespace PlayniteAchievements.Tests.Providers
{
    [TestClass]
    public class PsPricesPageParserTests
    {
        [TestMethod]
        public void TrophyPage_YieldsNpwrTitleAndTrophies()
        {
            var page = PsPricesPageParser.ParseTrophyPage(Fixture("game-trophies-god-of-war-ragnarok.html"));

            Assert.IsNotNull(page);
            Assert.AreEqual("NPWR22392_00", page.NpCommunicationId);
            Assert.AreEqual("God of War Ragnarök", page.Title);
            Assert.AreEqual(5, page.Trophies.Count);

            var hope = page.Trophies.Single(t => t.DisplayName == "God of Hope");
            Assert.AreEqual("Master Valhalla and master thyself.", hope.Description);
            Assert.AreEqual(
                "https://psnobj.prod.dl.playstation.net/psnobj/NPWR22392_00/90753ee0-3711-441b-9012-72e0802209ed.png",
                hope.UnlockedIconPath);
            Assert.AreEqual("silver", hope.TrophyType);

            var platinum = page.Trophies.Single(t => t.IsCapstone);
            Assert.AreEqual("The Bear and the Wolf", platinum.DisplayName);
            Assert.AreEqual("platinum", platinum.TrophyType);
            Assert.AreEqual("gold", page.Trophies.Single(t => t.DisplayName == "Ready for Commitment").TrophyType);
            Assert.AreEqual("bronze", page.Trophies.Single(t => t.DisplayName == "Invitation Accepted").TrophyType);

            // Entities decode, so titles compare against the platform's own text.
            Assert.IsTrue(page.Trophies.Any(t => t.DisplayName == "How it's Going"));
        }

        [TestMethod]
        public void TrophyPage_WithoutNpwrInTitle_IsRejected()
        {
            Assert.IsNull(PsPricesPageParser.ParseTrophyPage("<html><head><title>God of War — Trophies</title></head></html>"));
        }

        [TestMethod]
        public void SearchPage_ListsGameCardsWithTitlesAndPlatforms()
        {
            var results = PsPricesPageParser.ParseSearchResults(Fixture("search-god-of-war-ra.html"));

            CollectionAssert.AreEqual(
                new[]
                {
                    "/region-us/game/5352717/god-of-war-ragnarok",
                    "/region-us/game/5352718/god-of-war-ragnarok",
                    "/region-us/game/5352719/god-of-war-ragnarok-digital-deluxe-edition",
                    "/region-us/game/1824213/mario-rabbids-kingdom-battle-gold-edition",
                    "/region-us/game/7531695/grim-god-of-war"
                },
                results.Select(r => r.Path).ToList());

            Assert.AreEqual("God Of War Ragnarök", results[0].Title);
            CollectionAssert.AreEqual(new[] { "PlayStation 4", "PlayStation 5" }, results[0].Platforms.ToList());
            Assert.IsTrue(results[0].IsPlayStation);
            Assert.IsFalse(results[3].IsPlayStation);
            Assert.IsFalse(results[4].IsPlayStation);
        }

        [TestMethod]
        public void GamePage_LinkToTrophyPageIsFound()
        {
            const string html = "<html><body><a href=\"/region-us/game/5352717/god-of-war-ragnarok\">x</a>" +
                                "<a href=\"https://psprices.com/game-trophies/17141-god-of-war-ragnarok\">Trophies</a></body></html>";

            Assert.AreEqual("/game-trophies/17141-god-of-war-ragnarok", PsPricesPageParser.FindTrophyPagePath(html));
        }

        [TestMethod]
        public void ChallengePage_IsRecognized()
        {
            Assert.IsTrue(PsPricesPageParser.IsChallengePage("<html><head><title>Just a moment...</title></head></html>"));
            Assert.IsFalse(PsPricesPageParser.IsChallengePage(Fixture("search-god-of-war-ra.html")));
        }

        private static string Fixture(string name)
        {
            return File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "PsPrices", name));
        }
    }
}
