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
