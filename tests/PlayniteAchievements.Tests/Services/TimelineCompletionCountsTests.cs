using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services.Overview;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.Tests.Services
{
    [TestClass]
    public class TimelineCompletionCountsTests
    {
        [TestMethod]
        public void CompletionCountsByGame_PlacesEachCompletedGameOnTheDayItWasFinished()
        {
            var byCapstone = Guid.NewGuid();
            var byLastUnlock = Guid.NewGuid();
            var unfinished = Guid.NewGuid();
            var capstoneUtc = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);
            var laterUnlockUtc = new DateTime(2026, 5, 9, 12, 0, 0, DateTimeKind.Utc);
            var lastUnlockUtc = new DateTime(2026, 4, 2, 12, 0, 0, DateTimeKind.Utc);
            var snapshot = new OverviewDataSnapshot
            {
                GameSummaries = new List<GameSummaryItem>
                {
                    new GameSummaryItem
                    {
                        PlayniteGameId = byCapstone,
                        IsCompleted = true,
                        CapstoneTotal = 1,
                        CapstoneUnlocked = 1,
                        LastUnlockUtc = laterUnlockUtc
                    },
                    new GameSummaryItem { PlayniteGameId = byLastUnlock, IsCompleted = true, LastUnlockUtc = lastUnlockUtc },
                    new GameSummaryItem { PlayniteGameId = unfinished, IsCompleted = false, LastUnlockUtc = lastUnlockUtc }
                },
                Achievements = new List<AchievementDisplayItem>
                {
                    new AchievementDisplayItem
                    {
                        PlayniteGameId = byCapstone,
                        ApiName = "capstone",
                        Unlocked = true,
                        IsCapstone = true,
                        UnlockTimeUtc = capstoneUtc
                    }
                }
            };

            var counts = TimelinePlatformSeries.CompletionCountsByGame(snapshot);

            Assert.AreEqual(2, counts.Count);
            Assert.AreEqual(UnlockDayCounts.DayOf(capstoneUtc), counts[byCapstone].Single().Key);
            Assert.AreEqual(UnlockDayCounts.DayOf(lastUnlockUtc), counts[byLastUnlock].Single().Key);
            Assert.AreEqual(2, TimelinePlatformSeries.Sum(counts).Values.Sum());
        }

        [TestMethod]
        public void CompletionCountsByGame_CountsAPlatinumAndTakesTheEarliestFinish()
        {
            var platinumOnly = Guid.NewGuid();
            var platinumThenFull = Guid.NewGuid();
            var unfinished = Guid.NewGuid();
            var platinumUtc = new DateTime(2026, 2, 1, 12, 0, 0, DateTimeKind.Utc);
            var laterUtc = new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);
            var snapshot = new OverviewDataSnapshot
            {
                GameSummaries = new List<GameSummaryItem>
                {
                    // A platinum the user no longer counts as a capstone, with DLC still open.
                    new GameSummaryItem { PlayniteGameId = platinumOnly, TotalAchievements = 50, UnlockedAchievements = 40, LastUnlockUtc = laterUtc },
                    // Platinum first, 100% later: the platinum day is the finish.
                    new GameSummaryItem { PlayniteGameId = platinumThenFull, TotalAchievements = 10, UnlockedAchievements = 10, IsCompleted = true, LastUnlockUtc = laterUtc },
                    new GameSummaryItem { PlayniteGameId = unfinished, TotalAchievements = 10, UnlockedAchievements = 3, LastUnlockUtc = laterUtc }
                },
                Achievements = new List<AchievementDisplayItem>
                {
                    new AchievementDisplayItem { PlayniteGameId = platinumOnly, Unlocked = true, TrophyType = "Platinum", UnlockTimeUtc = platinumUtc },
                    new AchievementDisplayItem { PlayniteGameId = platinumThenFull, Unlocked = true, TrophyType = "platinum", UnlockTimeUtc = platinumUtc },
                    new AchievementDisplayItem { PlayniteGameId = unfinished, Unlocked = true, TrophyType = "gold", UnlockTimeUtc = platinumUtc }
                }
            };

            var counts = TimelinePlatformSeries.CompletionCountsByGame(snapshot);

            Assert.AreEqual(2, counts.Count);
            Assert.AreEqual(UnlockDayCounts.DayOf(platinumUtc), counts[platinumOnly].Single().Key);
            Assert.AreEqual(UnlockDayCounts.DayOf(platinumUtc), counts[platinumThenFull].Single().Key);
            Assert.IsFalse(counts.ContainsKey(unfinished));
        }
    }
}
