using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.Tests.ViewModels
{
    /// <summary>
    /// The progress column's finish badge: when it shows, when it carries a count, and when it
    /// stands in for the platinum trophy.
    /// </summary>
    [TestClass]
    public class GameSummaryBadgeRuleTests
    {
        [TestMethod]
        public void OneCapstone_BadgeWaitsForIt_AndCarriesNoCount()
        {
            var item = new GameSummaryItem { CapstoneTotal = 1, CapstoneUnlocked = 0 };
            Assert.IsFalse(item.ShowCompletionBadge);

            item.CapstoneUnlocked = 1;
            item.IsCompleted = true;
            Assert.IsTrue(item.ShowCompletionBadge);
            Assert.IsFalse(item.ShowCompletionCount);
            Assert.AreEqual(1, item.Completions);
        }

        [TestMethod]
        public void SeveralCapstones_BadgeCountsRatherThanWaits()
        {
            var item = new GameSummaryItem { CapstoneTotal = 3, CapstoneUnlocked = 2 };

            Assert.IsTrue(item.ShowCompletionBadge);
            Assert.IsTrue(item.ShowCompletionCount);
            Assert.AreEqual("2", item.CompletionCountText);
            Assert.AreEqual(3, item.PossibleCompletions);
        }

        [TestMethod]
        public void SeveralCapstonesNoneEarned_ShowsNothing()
        {
            var item = new GameSummaryItem { CapstoneTotal = 3, CapstoneUnlocked = 0 };

            Assert.IsFalse(item.ShowCompletionBadge);
            Assert.IsFalse(item.ShowCompletionCount);
        }

        [TestMethod]
        public void NoCapstones_FallsBackToTheFinishedState()
        {
            var item = new GameSummaryItem { TotalAchievements = 10, IsCompleted = true };

            Assert.IsTrue(item.ShowCompletionBadge);
            Assert.AreEqual(1, item.Completions);
            Assert.AreEqual(1, item.PossibleCompletions);
        }

        [TestMethod]
        public void PlatinumIsTheFinish_OnlyWhenItIsTheCapstone()
        {
            var item = new GameSummaryItem { TrophyPlatinumTotal = 1 };
            Assert.IsFalse(item.IsPlatinumFinish);

            item.CapstonesMatchPlatinums = true;
            Assert.IsTrue(item.IsPlatinumFinish);
        }

        [TestMethod]
        public void NoPlatinumToShow_KeepsTheOrdinaryBadge()
        {
            var item = new GameSummaryItem { CapstonesMatchPlatinums = true, TrophyPlatinumTotal = 0 };

            Assert.IsFalse(item.IsPlatinumFinish);
        }

        [TestMethod]
        public void PlatinumTotalArrivingLate_RaisesTheBadgeSwap()
        {
            var item = new GameSummaryItem { CapstonesMatchPlatinums = true };
            var raised = false;
            item.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(GameSummaryItem.IsPlatinumFinish))
                {
                    raised = true;
                }
            };

            item.TrophyPlatinumTotal = 1;

            Assert.IsTrue(raised);
        }

        [TestMethod]
        public void MythicFinish_NeedsEveryPointOfAFamily()
        {
            var item = new GameSummaryItem { PointsFamily = "Xbox", PlatformScorePointsTotal = 1000, PlatformScorePoints = 990 };
            Assert.IsFalse(item.IsMythicFinish);

            item.PlatformScorePoints = 1000;
            Assert.IsTrue(item.IsMythicFinish);

            Assert.IsFalse(new GameSummaryItem { PlatformScorePointsTotal = 1000, PlatformScorePoints = 1000 }.IsMythicFinish);
            Assert.IsFalse(new GameSummaryItem { PointsFamily = "Xbox" }.IsMythicFinish);
        }

        [TestMethod]
        public void LastPointsArriving_RaisesTheBadgeSwap()
        {
            var item = new GameSummaryItem { PointsFamily = "Xbox", PlatformScorePointsTotal = 100, PlatformScorePoints = 90 };
            var raised = false;
            item.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(GameSummaryItem.IsMythicFinish))
                {
                    raised = true;
                }
            };

            item.PlatformScorePoints = 100;

            Assert.IsTrue(raised);
        }

        [TestMethod]
        public void Badge_FollowsTheLeadingDetail()
        {
            // A PSN-style row with trophy grades, a capstone platinum and every point earned.
            var item = new GameSummaryItem
            {
                CapstonesMatchPlatinums = true,
                TrophyPlatinumTotal = 1,
                TrophyGoldTotal = 3,
                PointsFamily = "PlayStation",
                PlatformScorePointsTotal = 300,
                PlatformScorePoints = 300
            };
            Assert.IsTrue(item.HasProgressDetail(ProgressDetailStyle.Trophy));

            Assert.AreEqual(CompletionBadgeKind.Platinum,
                item.CompletionBadgeFor(new[] { ProgressDetailStyle.Trophy, ProgressDetailStyle.Points }));
            Assert.AreEqual(CompletionBadgeKind.Mythic,
                item.CompletionBadgeFor(new[] { ProgressDetailStyle.Points, ProgressDetailStyle.Trophy }));
            Assert.AreEqual(CompletionBadgeKind.Completed,
                item.CompletionBadgeFor(new ProgressDetailStyle[0]));
            Assert.AreEqual(CompletionBadgeKind.Completed, item.CompletionBadgeFor(null));
        }

        [TestMethod]
        public void Badge_OnlyTrophyIsTheCapstonePlatinum_TrophiesStillLead()
        {
            // The trophy row is empty because its one platinum moved to the badge.
            var item = new GameSummaryItem { CapstonesMatchPlatinums = true, TrophyPlatinumTotal = 1 };
            Assert.IsFalse(item.HasProgressDetail(ProgressDetailStyle.Trophy));

            Assert.AreEqual(CompletionBadgeKind.Platinum,
                item.CompletionBadgeFor(new[] { ProgressDetailStyle.Trophy, ProgressDetailStyle.Rarity }));
        }

        [TestMethod]
        public void Badge_SkipsDetailsTheRowHasNoDataFor()
        {
            // An Xbox-style row: no trophy grades, so points lead even when trophies come first.
            var item = new GameSummaryItem { PointsFamily = "Xbox", PlatformScorePointsTotal = 1000, PlatformScorePoints = 1000 };

            Assert.AreEqual(CompletionBadgeKind.Mythic,
                item.CompletionBadgeFor(new[] { ProgressDetailStyle.Trophy, ProgressDetailStyle.Points }));
        }

        [TestMethod]
        public void Badge_LeadingDetailWithoutAFinish_KeepsTheCompletedBadge()
        {
            // Trophies lead, but the capstone is not the platinum: points further down do not step in.
            var item = new GameSummaryItem
            {
                TrophyPlatinumTotal = 1,
                PointsFamily = "PlayStation",
                PlatformScorePointsTotal = 300,
                PlatformScorePoints = 300
            };

            Assert.AreEqual(CompletionBadgeKind.Completed,
                item.CompletionBadgeFor(new[] { ProgressDetailStyle.Trophy, ProgressDetailStyle.Points }));
        }
    }
}
