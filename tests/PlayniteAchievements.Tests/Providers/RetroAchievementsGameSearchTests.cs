using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Providers.RetroAchievements;
using PlayniteAchievements.Providers.RetroAchievements.Models;
using System.Linq;

namespace PlayniteAchievements.Tests.Providers
{
    [TestClass]
    public class RetroAchievementsGameSearchTests
    {
        [TestMethod]
        public void Filter_MatchesAnyAlternateTitle()
        {
            var games = new[]
            {
                Game(1, "Pocket Monsters Red | Pokemon Red Version"),
                Game(2, "Tetris")
            };

            var found = RetroAchievementsGameSearch.Filter("Pokemon Red", games);

            CollectionAssert.AreEqual(new[] { 1 }, found.Select(game => game.ID).ToArray());
        }

        [TestMethod]
        public void Filter_IgnoresTagsRegionsAndAccents()
        {
            var games = new[]
            {
                Game(1, "~Homebrew~ Pokémon Crystal Clear (USA) [T+Eng]"),
                Game(2, "Crystal Castles")
            };

            var found = RetroAchievementsGameSearch.Filter("pokemon crystal clear", games);

            Assert.AreEqual(1, found[0].ID);
        }

        [TestMethod]
        public void Filter_PutsBaseSetsBeforeSubsetsOfTheSameScore()
        {
            var games = new[]
            {
                Game(2, "Super Mario World [Subset - Bonus]"),
                Game(1, "Super Mario World"),
            };

            var found = RetroAchievementsGameSearch.Filter("Super Mario World", games);

            CollectionAssert.AreEqual(new[] { 1, 2 }, found.Select(game => game.ID).ToArray());
        }

        [TestMethod]
        public void Filter_DropsWeakMatchesAndCapsTheList()
        {
            var games = Enumerable.Range(1, 150).Select(id => Game(id, "Mega Man " + id))
                .Concat(new[] { Game(500, "Unrelated Puzzle") })
                .ToList();

            var found = RetroAchievementsGameSearch.Filter("Mega Man", games);

            Assert.AreEqual(100, found.Count);
            Assert.IsFalse(found.Any(game => game.ID == 500));
        }

        [TestMethod]
        public void ParseGameId_ReadsIdsAndGameAddresses()
        {
            Assert.AreEqual(1446, RetroAchievementsGameSearch.ParseGameId("1446"));
            Assert.AreEqual(1446, RetroAchievementsGameSearch.ParseGameId("https://retroachievements.org/game/1446"));
            Assert.AreEqual(1446, RetroAchievementsGameSearch.ParseGameId("retroachievements.org/game/1446-super-mario-world"));
            Assert.IsNull(RetroAchievementsGameSearch.ParseGameId("Super Mario World"));
            Assert.IsNull(RetroAchievementsGameSearch.ParseGameId("0"));
        }

        private static RaGameListWithTitle Game(int id, string title) => new RaGameListWithTitle { ID = id, Title = title };
    }
}
