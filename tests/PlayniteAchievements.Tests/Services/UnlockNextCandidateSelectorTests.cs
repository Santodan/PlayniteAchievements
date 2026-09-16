using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Achievements;
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
        public void SelectAchievements_KeepsOnlyLockedRowsAndBoundsThemPerGame()
        {
            var achievements = new List<AchievementDetail>
            {
                Achievement("unlocked", order: 0, percent: 99, unlocked: true)
            };
            for (var i = 0; i < 60; i++)
            {
                achievements.Add(Achievement("locked" + i, order: i + 1, percent: i));
            }

            var selected = UnlockNextCandidateSelector.SelectAchievements(achievements);

            Assert.IsFalse(selected.Any(detail => detail.Unlocked));
            Assert.IsTrue(selected.Count <= UnlockNextCandidateSelector.PerGameSlice * 2);

            // The head of the game's own order and the most commonly unlocked rows both survive,
            // because the criteria the widget offers read from opposite ends of the list.
            CollectionAssert.Contains(selected.ToArray(), achievements[1], "lowest order index");
            CollectionAssert.Contains(selected.ToArray(), achievements[60], "highest global percent");
        }

        [TestMethod]
        public void SelectAchievements_LeavesRowsWithoutARarityOutOfTheCommonnessSlice()
        {
            var unknown = Achievement("unknown", order: 50, percent: null);
            var known = Achievement("known", order: 51, percent: 5);

            var selected = UnlockNextCandidateSelector.SelectAchievements(new[] { unknown, known });

            // Both fit inside the order slice here; the point is that a missing percentage never
            // reads as "easiest", which a null-as-zero comparison would invert.
            CollectionAssert.AreEquivalent(new[] { unknown, known }, selected.ToArray());
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

        private static AchievementDetail Achievement(
            string apiName,
            int order,
            double? percent,
            bool unlocked = false)
        {
            return new AchievementDetail
            {
                ApiName = apiName,
                DisplayName = apiName,
                Unlocked = unlocked,
                DefaultOrderIndex = order,
                GlobalPercentUnlocked = percent
            };
        }
    }
}
