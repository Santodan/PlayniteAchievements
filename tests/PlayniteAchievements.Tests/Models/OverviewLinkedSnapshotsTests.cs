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
        public void SpanLinkedView_CountsProvidersFromSpanUnlocksNotWholeGames()
        {
            // The overview's linked view for a span: the games with unlocks in it, then those
            // games' unlocks cut to the span.
            var source = CreateSource();
            var inRange = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);
            var outOfRange = new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc);
            source.Achievements = new List<PlayniteAchievements.ViewModels.AchievementDisplayItem>
            {
                new PlayniteAchievements.ViewModels.AchievementDisplayItem { PlayniteGameId = GameA, Unlocked = true, UnlockTimeUtc = inRange },
                new PlayniteAchievements.ViewModels.AchievementDisplayItem { PlayniteGameId = GameA, Unlocked = true, UnlockTimeUtc = outOfRange },
                new PlayniteAchievements.ViewModels.AchievementDisplayItem { PlayniteGameId = GameA, Unlocked = true, UnlockTimeUtc = outOfRange },
                new PlayniteAchievements.ViewModels.AchievementDisplayItem { PlayniteGameId = GameB, Unlocked = true, UnlockTimeUtc = outOfRange }
            };
            var day = UnlockDayCounts.DayOf(inRange);
            var span = new UnlockDaySpan(day, day);
            var keptIds = new HashSet<Guid>(source.Achievements
                .Where(item => span.Contains(UnlockDayCounts.DayOf(item.UnlockTimeUtc.Value)))
                .Select(item => item.PlayniteGameId.Value));
            var kept = source.GameSummaries.Where(game => keptIds.Contains(game.PlayniteGameId.Value)).ToList();

            var whole = OverviewLinkedSnapshots.Build(source, kept, keptIsAll: false);
            var linked = OverviewLinkedSnapshots.ClipToSpan(whole, span);

            // The whole-game view counts GameA's four lifetime unlocks; the span view only one.
            Assert.AreEqual(4, whole.UnlockedByProvider["Steam"]);
            Assert.AreEqual(1, linked.UnlockedByProvider["Steam"]);
            Assert.IsFalse(linked.UnlockedByProvider.ContainsKey("Epic"));
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
        public void ClipToAchievements_CountsUndatedUnlocksWithoutDayCounts()
        {
            var source = CreateSource();
            var when = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);
            source.Achievements = new List<PlayniteAchievements.ViewModels.AchievementDisplayItem>
            {
                new PlayniteAchievements.ViewModels.AchievementDisplayItem
                {
                    PlayniteGameId = GameA, Unlocked = true, UnlockTimeUtc = when, Tags = "Unobtainable"
                },
                new PlayniteAchievements.ViewModels.AchievementDisplayItem
                {
                    PlayniteGameId = GameB, Unlocked = true, Tags = "Unobtainable"
                },
                new PlayniteAchievements.ViewModels.AchievementDisplayItem
                {
                    PlayniteGameId = GameB, Unlocked = false, Tags = "Unobtainable"
                }
            };

            var clipped = OverviewLinkedSnapshots.ClipToAchievements(source, item => item.Tags == "Unobtainable");

            Assert.AreEqual(2, clipped.TotalUnlocked);
            Assert.AreEqual(1, clipped.GlobalUnlockCountsByDate.Values.Sum());
            Assert.IsFalse(clipped.UnlockCountsByDateByGame.ContainsKey(GameB));
            Assert.AreEqual(1, clipped.UnlockedByProvider["Epic"]);
        }

        [TestMethod]
        public void ClipCompletionsToSpan_CountsOnlyCompletionsFinishedInRangeWithNoRemainder()
        {
            var (source, span, _, inRangeGame, _) = CreateCompletionSource();

            var clipped = OverviewLinkedSnapshots.ClipCompletionsToSpan(source, span);

            // One of the capstone game's two capstones, plus the game that reached 100% in range.
            Assert.AreEqual(2, clipped.Completions);
            Assert.AreEqual(2, clipped.PossibleCompletions);
            Assert.AreEqual(1, clipped.CompletedGames);
            Assert.AreEqual(span, clipped.CompletionSpan);
            Assert.AreEqual(3, clipped.TotalGames);
            Assert.IsNull(source.CompletionSpan);
            CollectionAssert.AreEquivalent(
                new[] { inRangeGame },
                OverviewLinkedSnapshots.GamesCompletedDuring(source, span).ToList());
        }

        [TestMethod]
        public void NarrowToGames_KeepsACompletionClipCut()
        {
            var (source, span, capstoneGame, _, _) = CreateCompletionSource();
            var clipped = OverviewLinkedSnapshots.ClipCompletionsToSpan(source, span);
            var kept = clipped.GameSummaries.Where(game => game.PlayniteGameId == capstoneGame).ToList();

            var narrowed = OverviewLinkedSnapshots.NarrowToGames(clipped, kept);

            Assert.AreEqual(span, narrowed.CompletionSpan);
            Assert.AreEqual(1, narrowed.Completions);
            Assert.AreEqual(1, narrowed.PossibleCompletions);
            Assert.AreEqual(0, narrowed.CompletedGames);
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

        // A game finished by two capstones, one unlocked in range and the last after it; a game
        // without capstones that reached 100% in range; and one that reached it before.
        private static (OverviewDataSnapshot Source, UnlockDaySpan Span, Guid CapstoneGame, Guid InRangeGame, Guid OutOfRangeGame)
            CreateCompletionSource()
        {
            var capstoneGame = Guid.NewGuid();
            var inRangeGame = Guid.NewGuid();
            var outOfRangeGame = Guid.NewGuid();
            var inRange = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);
            var after = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
            var before = new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc);
            var games = new List<GameSummaryItem>
            {
                new GameSummaryItem
                {
                    PlayniteGameId = capstoneGame, ProviderKey = "Steam", IsCompleted = true,
                    CapstoneTotal = 2, CapstoneUnlocked = 2, TotalAchievements = 5, UnlockedAchievements = 4
                },
                new GameSummaryItem
                {
                    PlayniteGameId = inRangeGame, ProviderKey = "Epic", IsCompleted = true,
                    TotalAchievements = 2, UnlockedAchievements = 2
                },
                new GameSummaryItem
                {
                    PlayniteGameId = outOfRangeGame, ProviderKey = "Epic", IsCompleted = true,
                    TotalAchievements = 1, UnlockedAchievements = 1
                }
            };
            var snapshot = OverviewDataSnapshot.FromGameSummaries(games);
            snapshot.Achievements = new List<PlayniteAchievements.ViewModels.AchievementDisplayItem>
            {
                new PlayniteAchievements.ViewModels.AchievementDisplayItem
                {
                    PlayniteGameId = capstoneGame, ApiName = "c1", Unlocked = true, IsCapstone = true, UnlockTimeUtc = inRange
                },
                new PlayniteAchievements.ViewModels.AchievementDisplayItem
                {
                    PlayniteGameId = capstoneGame, ApiName = "c2", Unlocked = true, IsCapstone = true, UnlockTimeUtc = after
                },
                new PlayniteAchievements.ViewModels.AchievementDisplayItem
                {
                    PlayniteGameId = inRangeGame, ApiName = "a", Unlocked = true, UnlockTimeUtc = before
                },
                new PlayniteAchievements.ViewModels.AchievementDisplayItem
                {
                    PlayniteGameId = inRangeGame, ApiName = "b", Unlocked = true, UnlockTimeUtc = inRange
                },
                new PlayniteAchievements.ViewModels.AchievementDisplayItem
                {
                    PlayniteGameId = outOfRangeGame, ApiName = "a", Unlocked = true, UnlockTimeUtc = before
                }
            };
            var day = UnlockDayCounts.DayOf(inRange);
            return (snapshot, new UnlockDaySpan(day.AddDays(-1), day.AddDays(1)), capstoneGame, inRangeGame, outOfRangeGame);
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
