using Microsoft.VisualStudio.TestTools.UnitTesting;
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
        public void PlatinumStandsInForTheBadge_OnlyWhenItIsTheCapstone()
        {
            var item = new GameSummaryItem { TrophyPlatinumTotal = 1 };
            Assert.IsFalse(item.ShowPlatinumInCompletionSpot);

            item.CapstonesMatchPlatinums = true;
            Assert.IsTrue(item.ShowPlatinumInCompletionSpot);
        }

        [TestMethod]
        public void NoPlatinumToShow_KeepsTheOrdinaryBadge()
        {
            var item = new GameSummaryItem { CapstonesMatchPlatinums = true, TrophyPlatinumTotal = 0 };

            Assert.IsFalse(item.ShowPlatinumInCompletionSpot);
        }

        [TestMethod]
        public void PlatinumTotalArrivingLate_RaisesTheBadgeSwap()
        {
            var item = new GameSummaryItem { CapstonesMatchPlatinums = true };
            var raised = false;
            item.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(GameSummaryItem.ShowPlatinumInCompletionSpot))
                {
                    raised = true;
                }
            };

            item.TrophyPlatinumTotal = 1;

            Assert.IsTrue(raised);
        }
    }
}
