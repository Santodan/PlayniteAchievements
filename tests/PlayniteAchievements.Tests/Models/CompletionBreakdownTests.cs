using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Overview;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.Tests.Models
{
    /// <summary>
    /// The Completed games pie's split: each finish under the badge its row shows, and the open
    /// finishes by whether the game has an unlock.
    /// </summary>
    [TestClass]
    public class CompletionBreakdownTests
    {
        private static readonly ProgressDetailStyle[] TrophyFirst =
            { ProgressDetailStyle.Rarity, ProgressDetailStyle.Trophy, ProgressDetailStyle.Points };

        private static readonly ProgressDetailStyle[] PointsFirst =
            { ProgressDetailStyle.Rarity, ProgressDetailStyle.Points, ProgressDetailStyle.Trophy };

        // Platinum and every point earned: the detail priority decides its slice.
        private static GameSummaryItem PlatinumAndMythic() => new GameSummaryItem
        {
            PlayniteGameId = Guid.NewGuid(),
            TotalAchievements = 10,
            UnlockedAchievements = 10,
            IsCompleted = true,
            CapstoneTotal = 1,
            CapstoneUnlocked = 1,
            CapstonesMatchPlatinums = true,
            TrophyPlatinumTotal = 1,
            TrophyGoldTotal = 3,
            PointsFamily = "PlayStation",
            PlatformScorePointsTotal = 300,
            PlatformScorePoints = 300
        };

        private static GameSummaryItem Plain(int unlocked, bool completed) => new GameSummaryItem
        {
            PlayniteGameId = Guid.NewGuid(),
            TotalAchievements = 10,
            UnlockedAchievements = unlocked,
            IsCompleted = completed
        };

        private static OverviewDataSnapshot Snapshot(params GameSummaryItem[] games) =>
            OverviewDataSnapshot.FromGameSummaries(games);

        [TestMethod]
        public void Finishes_FollowTheDetailPriority()
        {
            var snapshot = Snapshot(PlatinumAndMythic(), Plain(10, true), Plain(4, false), Plain(0, false));

            var trophy = CompletionBreakdown.From(snapshot, TrophyFirst);
            Assert.AreEqual(1, trophy.Finishes(CompletionBadgeKind.Platinum));
            Assert.AreEqual(0, trophy.Finishes(CompletionBadgeKind.Mythic));
            Assert.AreEqual(1, trophy.Finishes(CompletionBadgeKind.Completed));
            CollectionAssert.AreEqual(
                new[] { CompletionBadgeKind.Platinum, CompletionBadgeKind.Mythic, CompletionBadgeKind.Completed },
                trophy.FinishOrder.ToList());

            var points = CompletionBreakdown.From(snapshot, PointsFirst);
            Assert.AreEqual(1, points.Finishes(CompletionBadgeKind.Mythic));
            Assert.AreEqual(0, points.Finishes(CompletionBadgeKind.Platinum));
            CollectionAssert.AreEqual(
                new[] { CompletionBadgeKind.Mythic, CompletionBadgeKind.Platinum, CompletionBadgeKind.Completed },
                points.FinishOrder.ToList());
        }

        [TestMethod]
        public void DisabledDetails_GetNoSlice()
        {
            var breakdown = CompletionBreakdown.From(
                Snapshot(PlatinumAndMythic()),
                new[] { ProgressDetailStyle.Rarity });

            CollectionAssert.AreEqual(new[] { CompletionBadgeKind.Completed }, breakdown.FinishOrder.ToList());
            Assert.AreEqual(1, breakdown.Finishes(CompletionBadgeKind.Completed));
        }

        [TestMethod]
        public void OpenFinishes_SplitByWhetherTheGameHasAnUnlock()
        {
            var multi = new GameSummaryItem
            {
                PlayniteGameId = Guid.NewGuid(),
                TotalAchievements = 20,
                UnlockedAchievements = 12,
                CapstoneTotal = 3,
                CapstoneUnlocked = 1
            };
            var breakdown = CompletionBreakdown.From(
                Snapshot(multi, Plain(4, false), Plain(0, false), Plain(0, false)),
                TrophyFirst);

            Assert.AreEqual(1, breakdown.Finishes(CompletionBadgeKind.Completed));
            Assert.AreEqual(3, breakdown.InProgress, "Two open capstones and one started game.");
            Assert.AreEqual(2, breakdown.NoProgress);
            Assert.AreEqual(5, breakdown.Incomplete);
        }

        [TestMethod]
        public void SpanCut_CountsOnlyTheSpansFinishes_WithNothingOpen()
        {
            var platinum = PlatinumAndMythic();
            var plain = Plain(10, true);
            var snapshot = Snapshot(platinum, plain);
            snapshot.Completions = 1;
            snapshot.PossibleCompletions = 1;
            snapshot.CompletionsByGame = new Dictionary<Guid, int> { [platinum.PlayniteGameId.Value] = 1 };

            var breakdown = CompletionBreakdown.From(snapshot, TrophyFirst);

            Assert.AreEqual(1, breakdown.Finishes(CompletionBadgeKind.Platinum));
            Assert.AreEqual(0, breakdown.Finishes(CompletionBadgeKind.Completed));
            Assert.AreEqual(0, breakdown.Incomplete);
        }

        [TestMethod]
        public void TotalsNotSummedFromTheGames_StayUnsplit()
        {
            var snapshot = Snapshot(PlatinumAndMythic(), Plain(0, false));
            snapshot.Completions = 5;
            snapshot.PossibleCompletions = 9;

            var breakdown = CompletionBreakdown.From(snapshot, TrophyFirst);

            Assert.AreEqual(5, breakdown.Finishes(CompletionBadgeKind.Completed));
            Assert.AreEqual(0, breakdown.Finishes(CompletionBadgeKind.Platinum));
            Assert.AreEqual(4, breakdown.Incomplete);
            Assert.AreEqual(0, breakdown.NoProgress);
        }

        [TestMethod]
        public void NoCompletionsToChart_IsEmpty()
        {
            var breakdown = CompletionBreakdown.From(new OverviewDataSnapshot(), TrophyFirst);

            Assert.AreEqual(0, breakdown.Incomplete);
            Assert.AreEqual(0, breakdown.Finishes(CompletionBadgeKind.Completed));
        }
    }
}
