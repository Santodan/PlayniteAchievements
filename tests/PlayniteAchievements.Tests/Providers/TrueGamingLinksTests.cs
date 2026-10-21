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
        public void Links_AcceptSlugsAndPastedAddresses(string link, string provider, string host, string slug)
        {
            Assert.IsTrue(TrueGamingLinks.TryParse(link, provider, out var site, out var parsed));
            Assert.AreEqual(host, site.Host);
            Assert.AreEqual(slug, parsed);
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
