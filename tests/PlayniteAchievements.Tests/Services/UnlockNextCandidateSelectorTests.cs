using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services.Overview;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.Tests.Services
{
    /// <summary>
    /// The Unlock Next pool exists because locked achievements are kept out of the overview
    /// snapshot for memory reasons, so its caps are the feature's load-bearing part.
    /// </summary>
    [TestClass]
    public class UnlockNextCandidateSelectorTests
    {
        [TestMethod]
        public void SelectGames_SkipsFinishedAndAchievementlessGames()
        {
            var unfinished = Game(total: 10, unlocked: 3, daysAgo: 1);
            var games = UnlockNextCandidateSelector.SelectGames(new[]
            {
                unfinished,
                Game(total: 10, unlocked: 10, daysAgo: 1),
                Game(total: 0, unlocked: 0, daysAgo: 1),
                null
            });

            Assert.AreSame(unfinished, games.Single());
        }

        [TestMethod]
        public void SelectGames_CapsTheLibraryAndCarriesBothRankings()
        {
            // The most recently played games are the least complete, so a selector that honored
            // only one ranking would drop the other's leaders entirely.
            var library = new List<GameSummaryItem>();
            for (var i = 0; i < 200; i++)
            {
                library.Add(Game(total: 200, unlocked: i, daysAgo: i + 1));
            }

            var selected = UnlockNextCandidateSelector.SelectGames(library);

            Assert.AreEqual(UnlockNextCandidateSelector.GameCap, selected.Count);
            Assert.AreEqual(selected.Count, selected.Distinct().Count());
            CollectionAssert.Contains(selected.ToArray(), library[0], "most recently played");
            CollectionAssert.Contains(selected.ToArray(), library[199], "closest to completion");
        }

        [TestMethod]
        public void CompletionFraction_UsesTheRawRatioRatherThanARoundedPercent()
        {
            // Both of these round to 33%, which is what GameSummaryItem.Progression would report,
            // so ranking on the rounded percent would tie them.
            var third = UnlockNextCandidateSelector.CompletionFraction(Game(3, 1, 1));
            var thirtyThree = UnlockNextCandidateSelector.CompletionFraction(Game(100, 33, 1));

            Assert.AreNotEqual(third, thirtyThree);
            Assert.IsTrue(third > thirtyThree);
            Assert.AreEqual(0, UnlockNextCandidateSelector.CompletionFraction(Game(0, 0, 1)));
            Assert.AreEqual(0, UnlockNextCandidateSelector.CompletionFraction(null));
        }

        private static GameSummaryItem Game(int total, int unlocked, int daysAgo)
        {
            return new GameSummaryItem
            {
                PlayniteGameId = Guid.NewGuid(),
                TotalAchievements = total,
                UnlockedAchievements = unlocked,
                LastPlayed = DateTime.Now.AddDays(-daysAgo)
            };
        }
    }
}
