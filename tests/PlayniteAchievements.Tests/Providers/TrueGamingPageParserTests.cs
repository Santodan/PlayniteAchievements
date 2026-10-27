using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Providers.Metadata.TrueGaming;
using System;
using System.IO;
using System.Linq;

namespace PlayniteAchievements.Tests.Providers
{
    /// <summary>Pages saved from the sites through an offscreen browser view (2026-10-21).</summary>
    [TestClass]
    [TestCategory("Providers")]
    public class TrueGamingPageParserTests
    {
        [TestMethod]
        public void TrueAchievements_FlagsDecodeFromTheMaskAndFilter()
        {
            var page = Parse("ta-clair-obscur.html");

            Assert.AreEqual(55, page.Achievements.Count);
            Assert.IsTrue(page.FlagsDecoded);
            var lumiere = page.Achievements.Single(row => row.Name == "Lumière");
            Assert.AreEqual("a477200", lumiere.Id);
            Assert.AreEqual("Embark on the Expedition.", lumiere.Description);
            Assert.IsNull(lumiere.Dlc);
            CollectionAssert.AreEquivalent(new[] { "Offline Mode", "Single Player", "Main Storyline" }, lumiere.Flags);

            var connoisseur = page.Achievements.Single(row => row.Name == "Connoisseur");
            CollectionAssert.AreEquivalent(
                new[] { "Offline Mode", "Single Player", "Collectable", "Missable", "Buggy +" },
                connoisseur.Flags);
            Assert.AreEqual(4, page.Achievements.Count(row => row.Flags.Contains("Missable")));
            Assert.AreEqual(1, page.Achievements.Count(row => row.Flags.Contains("Story Completed")));
        }

        [TestMethod]
        public void GameArt_IsThePagesShareImage()
        {
            Assert.AreEqual("https://img.trueachievements.com/game/14402?v=1819", Parse("ta-clair-obscur.html").GameArt);
            Assert.AreEqual("https://img.truetrophies.com/game/14950?v=1732", Parse("tt-elden-ring.html").GameArt);
            Assert.IsNull(TrueGamingPageParser.Parse("<html><body></body></html>").GameArt);
        }

        [TestMethod]
        public void DlcHeaders_GroupTheRowsAfterThem_WithTheirArt()
        {
            var page = Parse("ta-fallout-4.html");

            Assert.AreEqual(84, page.Achievements.Count);
            Assert.IsTrue(page.FlagsDecoded);
            Assert.AreEqual("Automatron", page.Achievements.Single(row => row.Name == "Mechanical Menace").Dlc);
            Assert.AreEqual("Wasteland Workshop", page.Achievements.Single(row => row.Name == "Docile").Dlc);
            Assert.IsNull(page.Achievements.Single(row => row.Name == "War Never Changes").Dlc);
            Assert.AreEqual("https://img.trueachievements.com/dlc/5688?v=5", page.DlcArt["Automatron"]);
            Assert.AreEqual(6, page.Achievements.Where(row => row.Dlc != null).Select(row => row.Dlc).Distinct().Count());
        }

        [DataTestMethod]
        [DataRow("tt-elden-ring.html", 42)]
        [DataRow("tsa-hades.html", 49)]
        [DataRow("tsa-cyberpunk-2077.html", 57)]
        public void OtherSites_ShareTheLayout(string file, int count)
        {
            var page = Parse(file);

            Assert.AreEqual(count, page.Achievements.Count);
            Assert.IsTrue(page.FlagsDecoded);
            Assert.IsTrue(page.Achievements.All(row => !string.IsNullOrWhiteSpace(row.Id) && row.Flags.Count > 0));
        }

        [TestMethod]
        public void TrueAchievements_RowsCarryGamerscore_AndTrueTrophiesRanks()
        {
            var ta = Parse("ta-clair-obscur.html");
            Assert.AreEqual(10, ta.Achievements.Single(row => row.Name == "Lumière").Points);
            Assert.AreEqual(1000, ta.Achievements.Sum(row => row.Points ?? 0));
            Assert.IsTrue(ta.Achievements.All(row => row.TrophyType == null));

            var tt = Parse("tt-elden-ring.html");
            Assert.AreEqual("platinum", tt.Achievements.Single(row => row.Name == "Elden Ring").TrophyType);
            Assert.AreEqual("gold", tt.Achievements.Single(row => row.Name == "Elden Lord").TrophyType);
            CollectionAssert.AreEquivalent(
                new[] { "bronze", "gold", "platinum", "silver" },
                tt.Achievements.Select(row => row.TrophyType).Distinct().ToList());
            Assert.AreEqual(24, tt.Achievements.Count(row => row.TrophyType == "bronze"));

            Assert.IsTrue(Parse("tsa-hades.html").Achievements.All(row => row.Points == null && row.TrophyType == null));
        }

        [TestMethod]
        public void Flags_PairBitsWithIdsInOrder_AndRejectDisagreeingCounts()
        {
            // Listed out of id order, as the sites list them.
            var filter = new[] { (6, "Single Player", 1), (4, "Offline Mode", 2) };

            var decoded = TrueGamingPageParser.DecodeFlags(filter, new[] { 0x50000000u, 0x40000000u });
            CollectionAssert.AreEqual(
                new[] { ("Offline Mode", 0x40000000u), ("Single Player", 0x10000000u) },
                decoded);

            Assert.IsNull(TrueGamingPageParser.DecodeFlags(filter, new[] { 0x50000000u, 0x50000000u }));
            Assert.IsNull(TrueGamingPageParser.DecodeFlags(filter, new[] { 0x40000000u, 0x40000000u }));
        }

        [TestMethod]
        public void Flags_IgnoreTheBitTheFilterNeverLists()
        {
            var page = Parse("ta-doom-eternal.html");

            Assert.IsTrue(page.FlagsDecoded);
            Assert.AreEqual(50, page.Achievements.Count(row => row.Flags.Count > 0));
            CollectionAssert.AreEquivalent(
                new[] { "Online Mode", "Versus" },
                page.Achievements.Single(row => row.Name == "Mix and Match").Flags);
            Assert.AreEqual(1, page.Achievements.Count(row => row.Flags.Contains("Story Completed")));
        }

        private static TrueGamingPage Parse(string file) =>
            TrueGamingPageParser.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "TrueGaming", file)));
    }
}
