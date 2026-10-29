using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services.Overview;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.Tests.Models
{
    [TestClass]
    public class OverviewLinkedSnapshotsTests
    {
        private static readonly Guid GameA = Guid.NewGuid();
        private static readonly Guid GameB = Guid.NewGuid();
        private static readonly DateTime March1 = new DateTime(2026, 3, 1);
        private static readonly DateTime March20 = new DateTime(2026, 3, 20);

        [TestMethod]
        public void Build_ReturnsTheSourceWhenEveryGameIsKept()
        {
            var source = CreateSource();

            Assert.AreSame(source, OverviewLinkedSnapshots.Build(source, source.GameSummaries, keptIsAll: true));
        }

        [TestMethod]
        public void Build_CutsTotalsCountsAndRowsToTheKeptGames()
        {
            var source = CreateSource();
            var kept = source.GameSummaries.Where(game => game.PlayniteGameId == GameA).ToList();

            var linked = OverviewLinkedSnapshots.Build(source, kept, keptIsAll: false);

            Assert.AreNotSame(source, linked);
            Assert.AreEqual(1, linked.TotalGames);
            Assert.AreEqual(10, linked.TotalAchievements);
            Assert.AreEqual(4, linked.TotalUnlocked);
            CollectionAssert.AreEquivalent(new[] { GameA }, linked.UnlockCountsByDateByGame.Keys.ToList());
            Assert.AreEqual(3, linked.GlobalUnlockCountsByDate[March1]);
            Assert.IsFalse(linked.GlobalUnlockCountsByDate.ContainsKey(March20));
            Assert.IsTrue(linked.Achievements.All(item => item.PlayniteGameId == GameA));
            Assert.AreEqual(1, linked.Achievements.Count);
        }

        [TestMethod]
        public void GamesUnlockedDuring_MatchesInclusiveDaysWithUnlocks()
        {
            var source = CreateSource();

            CollectionAssert.AreEquivalent(
                new[] { GameA },
                OverviewLinkedSnapshots.GamesUnlockedDuring(source, new UnlockDaySpan(March1, March1)).ToList());
            CollectionAssert.AreEquivalent(
                new[] { GameA, GameB },
                OverviewLinkedSnapshots.GamesUnlockedDuring(source, new UnlockDaySpan(March1, March20)).ToList());
            Assert.AreEqual(
                0,
                OverviewLinkedSnapshots.GamesUnlockedDuring(source, new UnlockDaySpan(March1.AddDays(1), March20.AddDays(-1))).Count);
        }

        [TestMethod]
        public void ClipToSpan_CountsOnlyUnlocksInRangeWithNoLockedRemainder()
        {
            var source = CreateSource();
            var inRange = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);
            var outOfRange = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
            source.Achievements = new List<PlayniteAchievements.ViewModels.AchievementDisplayItem>
            {
                new PlayniteAchievements.ViewModels.AchievementDisplayItem
                {
                    PlayniteGameId = GameA, Unlocked = true, UnlockTimeUtc = inRange,
                    Rarity = PlayniteAchievements.Models.Achievements.RarityTier.Rare, TrophyType = "gold"
                },
                new PlayniteAchievements.ViewModels.AchievementDisplayItem
                {
                    PlayniteGameId = GameA, Unlocked = true, UnlockTimeUtc = outOfRange,
                    Rarity = PlayniteAchievements.Models.Achievements.RarityTier.Common, TrophyType = "bronze"
                }
            };
            var day = UnlockDayCounts.DayOf(inRange);

            var clipped = OverviewLinkedSnapshots.ClipToSpan(source, new UnlockDaySpan(day.AddDays(-1), day.AddDays(1)));

            Assert.AreEqual(1, clipped.TotalUnlocked);
            Assert.AreEqual(1, clipped.TotalAchievements);
            Assert.AreEqual(0, clipped.TotalLocked);
            Assert.AreEqual(1, clipped.TotalRare);
            Assert.AreEqual(0, clipped.TotalCommon);
            Assert.AreEqual(1, clipped.TotalGold);
            Assert.AreEqual(0, clipped.TotalBronze);
            Assert.AreEqual(source.TotalGames, clipped.TotalGames);
            Assert.IsFalse(clipped.GlobalUnlockCountsByDate.ContainsKey(March20));
        }

        [TestMethod]
        public void ClipToSpan_CountsProvidersFromTheUnlocksInRange()
        {
            var source = CreateSource();
            var inRange = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);
            var outOfRange = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
            source.Achievements = new List<PlayniteAchievements.ViewModels.AchievementDisplayItem>
            {
                new PlayniteAchievements.ViewModels.AchievementDisplayItem { PlayniteGameId = GameA, Unlocked = true, UnlockTimeUtc = inRange },
                new PlayniteAchievements.ViewModels.AchievementDisplayItem { PlayniteGameId = GameA, Unlocked = true, UnlockTimeUtc = inRange },
                new PlayniteAchievements.ViewModels.AchievementDisplayItem { PlayniteGameId = GameA, Unlocked = true, UnlockTimeUtc = outOfRange },
                new PlayniteAchievements.ViewModels.AchievementDisplayItem { PlayniteGameId = GameB, Unlocked = true, UnlockTimeUtc = outOfRange }
            };
            var day = UnlockDayCounts.DayOf(inRange);

            var clipped = OverviewLinkedSnapshots.ClipToSpan(source, new UnlockDaySpan(day.AddDays(-1), day.AddDays(1)));

            CollectionAssert.AreEquivalent(new[] { "Steam" }, clipped.UnlockedByProvider.Keys.ToList());
            Assert.AreEqual(2, clipped.UnlockedByProvider["Steam"]);
            Assert.AreEqual(2, clipped.TotalByProvider["Steam"]);
            Assert.IsFalse(clipped.TotalByProvider.ContainsKey("Epic"));
        }

        [TestMethod]
        public void NarrowToGames_KeepsAClippedViewClipped()
        {
            var source = CreateSource();
            var when = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);
            source.Achievements = new List<PlayniteAchievements.ViewModels.AchievementDisplayItem>
            {
                new PlayniteAchievements.ViewModels.AchievementDisplayItem
                {
                    PlayniteGameId = GameA, Unlocked = true, UnlockTimeUtc = when,
                    Rarity = PlayniteAchievements.Models.Achievements.RarityTier.Rare
                },
                new PlayniteAchievements.ViewModels.AchievementDisplayItem
                {
                    PlayniteGameId = GameB, Unlocked = true, UnlockTimeUtc = when,
                    Rarity = PlayniteAchievements.Models.Achievements.RarityTier.Common
                }
            };
            var clipped = OverviewLinkedSnapshots.ClipToAchievements(source, _ => true);
            var keptA = clipped.GameSummaries.Where(game => game.PlayniteGameId == GameA).ToList();

            var narrowed = OverviewLinkedSnapshots.NarrowToGames(clipped, keptA);

            Assert.IsTrue(narrowed.IsAchievementClip);
            Assert.AreEqual(1, narrowed.TotalUnlocked);
            Assert.AreEqual(0, narrowed.TotalLocked);
            Assert.AreEqual(1, narrowed.TotalRare);
            Assert.AreEqual(0, narrowed.TotalCommon);
            Assert.AreEqual(1, narrowed.TotalGames);
        }

        [TestMethod]
        public void NarrowToGames_KeepsAWholeGameViewWhole()
        {
            var source = CreateSource();
            var keptA = source.GameSummaries.Where(game => game.PlayniteGameId == GameA).ToList();

            var narrowed = OverviewLinkedSnapshots.NarrowToGames(source, keptA);

            Assert.IsFalse(narrowed.IsAchievementClip);
            Assert.AreEqual(10, narrowed.TotalAchievements);
            Assert.AreEqual(4, narrowed.TotalUnlocked);
        }

        [TestMethod]
        public void UnlockDaySpan_OrdersItsEndsAndIgnoresTimeOfDay()
        {
            var span = new UnlockDaySpan(March20.AddHours(15), March1.AddHours(9));

            Assert.AreEqual(March1, span.Start);
            Assert.AreEqual(March20, span.End);
            Assert.AreEqual(new UnlockDaySpan(March1, March20), span);
            Assert.IsTrue(span.Contains(March20.AddHours(23)));
        }

        [TestMethod]
        public void NarrowsTo_FollowsTheSelectionRule()
        {
            var plain = new GameSummaryItem { PlayniteGameId = GameA };
            var rarity = new GameSummaryItem { PlayniteGameId = GameA, TotalCommonPossible = 3 };
            var trophy = new GameSummaryItem { PlayniteGameId = GameA, TrophyGoldTotal = 2 };

            Assert.IsFalse(OverviewLinkedSnapshots.NarrowsTo(null, OverviewLinkedSelection.Always));
            Assert.IsFalse(OverviewLinkedSnapshots.NarrowsTo(plain, OverviewLinkedSelection.Ignore));
            Assert.IsTrue(OverviewLinkedSnapshots.NarrowsTo(plain, OverviewLinkedSelection.Always));
            Assert.IsFalse(OverviewLinkedSnapshots.NarrowsTo(plain, OverviewLinkedSelection.WhenRarityData));
            Assert.IsTrue(OverviewLinkedSnapshots.NarrowsTo(rarity, OverviewLinkedSelection.WhenRarityData));
            Assert.IsFalse(OverviewLinkedSnapshots.NarrowsTo(rarity, OverviewLinkedSelection.WhenTrophyData));
            Assert.IsTrue(OverviewLinkedSnapshots.NarrowsTo(trophy, OverviewLinkedSelection.WhenTrophyData));
        }

        private static OverviewDataSnapshot CreateSource()
        {
            var games = new List<GameSummaryItem>
            {
                new GameSummaryItem { PlayniteGameId = GameA, ProviderKey = "Steam", TotalAchievements = 10, UnlockedAchievements = 4 },
                new GameSummaryItem { PlayniteGameId = GameB, ProviderKey = "Epic", TotalAchievements = 6, UnlockedAchievements = 6 }
            };
            var snapshot = OverviewDataSnapshot.FromGameSummaries(games);
            snapshot.UnlockCountsByDateByGame = new Dictionary<Guid, Dictionary<DateTime, int>>
            {
                [GameA] = new Dictionary<DateTime, int> { [March1] = 3 },
                [GameB] = new Dictionary<DateTime, int> { [March20] = 2 }
            };
            snapshot.GlobalUnlockCountsByDate = new Dictionary<DateTime, int> { [March1] = 3, [March20] = 2 };
            snapshot.Achievements = new List<PlayniteAchievements.ViewModels.AchievementDisplayItem>
            {
                new PlayniteAchievements.ViewModels.AchievementDisplayItem { PlayniteGameId = GameA },
                new PlayniteAchievements.ViewModels.AchievementDisplayItem { PlayniteGameId = GameB }
            };
            return snapshot;
        }
    }
}
