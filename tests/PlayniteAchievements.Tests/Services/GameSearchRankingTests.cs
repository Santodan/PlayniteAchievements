using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services.GameSearch;
using System.Linq;

namespace PlayniteAchievements.Tests.Services
{
    [TestClass]
    public class GameSearchRankingTests
    {
        [TestMethod]
        public void Rank_PutsExactAheadOfPrefixAheadOfSubstring()
        {
            var ranked = GameSearchRanking.Rank("Hades", new[]
            {
                Result("3", "The Hades Collection"),
                Result("2", "Hades II"),
                Result("1", "Hades")
            });

            CollectionAssert.AreEqual(new[] { "1", "2", "3" }, ranked.Select(result => result.Id).ToArray());
        }

        [TestMethod]
        public void Rank_EqualScoresKeepTheSourceOrder()
        {
            var ranked = GameSearchRanking.Rank("Portal", new[]
            {
                Result("b", "Portal Stories"),
                Result("a", "Portal Knights"),
                Result("c", "Unrelated")
            });

            CollectionAssert.AreEqual(new[] { "b", "a", "c" }, ranked.Select(result => result.Id).ToArray());
        }

        [TestMethod]
        public void Rank_IgnoresSymbolsEditionsAndPunctuation()
        {
            var ranked = GameSearchRanking.Rank("Deus Ex: Mankind Divided", new[]
            {
                Result("other", "Deus Ex: The Fall"),
                Result("match", "Deus Ex™ - Mankind Divided - Definitive Edition")
            });

            Assert.AreEqual("match", ranked[0].Id);
        }

        [TestMethod]
        public void Normalize_DropsAccentsAndApostrophes()
        {
            Assert.AreEqual(
                GameSearchRanking.Normalize("Marvels Spider Man"),
                GameSearchRanking.Normalize("Marvel’s Spider-Man"));
            Assert.AreEqual("Pokemon", GameSearchRanking.Normalize("Pokémon"));
        }

        private static GameSearchResult Result(string id, string name) => new GameSearchResult(id, name);
    }
}
