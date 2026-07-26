using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Providers.Exophase;

namespace PlayniteAchievements.Tests.Providers
{
    [TestClass]
    public class ExophaseGameNameMatcherTests
    {
        [TestMethod]
        public void NormalizeGameName_StripsDeluxeEditionSuffix()
        {
            Assert.AreEqual("Titanfall 2", ExophaseGameNameMatcher.NormalizeGameName("Titanfall 2 Deluxe Edition"));
            Assert.AreEqual("Titanfall 2", ExophaseGameNameMatcher.NormalizeGameName("Titanfall 2 - Deluxe Edition"));
            Assert.AreEqual("Titanfall 2", ExophaseGameNameMatcher.NormalizeGameName("Titanfall 2 (Deluxe Edition)"));
        }

        [TestMethod]
        public void NormalizeGameName_StripsDirectorsCutSuffix()
        {
            Assert.AreEqual("Ghost of Tsushima", ExophaseGameNameMatcher.NormalizeGameName("Ghost of Tsushima DIRECTOR'S CUT"));
            Assert.AreEqual("Ghost of Tsushima", ExophaseGameNameMatcher.NormalizeGameName("Ghost of Tsushima - Director's Cut"));
            Assert.AreEqual("Ghost of Tsushima", ExophaseGameNameMatcher.NormalizeGameName("Ghost of Tsushima (Director's Cut)"));
            Assert.AreEqual("Ghost of Tsushima", ExophaseGameNameMatcher.NormalizeGameName("Ghost of Tsushima Directors Cut"));
        }

        [TestMethod]
        public void NormalizeGameName_LeavesUnlistedEditionWordsIntact()
        {
            // "Legendary Edition" is not a stripped suffix, so both the friend title and the
            // library game keep it and still normalize identically.
            Assert.AreEqual(
                "Mass Effect Legendary Edition",
                ExophaseGameNameMatcher.NormalizeGameName("Mass Effect Legendary Edition"));
        }

        [TestMethod]
        public void ComputeMatchScore_TitanfallDeluxeMatchesFriendTitanfall2Exactly()
        {
            var library = ExophaseGameNameMatcher.NormalizeGameName("Titanfall 2 Deluxe Edition");
            var friend = ExophaseGameNameMatcher.NormalizeGameName("Titanfall 2");

            Assert.AreEqual(
                ExophaseGameNameMatcher.ExactMatchScore,
                ExophaseGameNameMatcher.ComputeMatchScore(friend, library));
        }

        [TestMethod]
        public void ComputeMatchScore_MassEffectLegendaryEditionMatchesExactly()
        {
            var library = ExophaseGameNameMatcher.NormalizeGameName("Mass Effect Legendary Edition");
            var friend = ExophaseGameNameMatcher.NormalizeGameName("Mass Effect Legendary Edition");

            Assert.AreEqual(
                ExophaseGameNameMatcher.ExactMatchScore,
                ExophaseGameNameMatcher.ComputeMatchScore(friend, library));
        }

        [TestMethod]
        public void ComputeMatchScore_SequelIsNotAnExactMatch()
        {
            // A friend's "Titanfall" must not auto-map onto a library "Titanfall 2":
            // it scores as a prefix (80), never an exact (100) match.
            var score = ExophaseGameNameMatcher.ComputeMatchScore(
                ExophaseGameNameMatcher.NormalizeGameName("Titanfall"),
                ExophaseGameNameMatcher.NormalizeGameName("Titanfall 2"));

            Assert.AreNotEqual(ExophaseGameNameMatcher.ExactMatchScore, score);
            Assert.IsTrue(score < ExophaseGameNameMatcher.ExactMatchScore);
        }

        [TestMethod]
        public void ComputeMatchScore_IsCaseInsensitive()
        {
            Assert.AreEqual(
                ExophaseGameNameMatcher.ExactMatchScore,
                ExophaseGameNameMatcher.ComputeMatchScore("titanfall 2", "Titanfall 2"));
        }

        [TestMethod]
        public void NormalizeGameNameForSlug_StripsEditionAndHyphenates()
        {
            Assert.AreEqual("titanfall-2", ExophaseGameNameMatcher.NormalizeGameNameForSlug("Titanfall 2 Deluxe Edition"));
        }
    }
}
