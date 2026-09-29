using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Services.Achievements;
using System.Collections.Generic;

namespace PlayniteAchievements.Tests.Services
{
    /// <summary>
    /// Covers what automatic capstone generation decides to do with a game, the rules a refresh
    /// and the retroactive pass both run.
    /// </summary>
    [TestClass]
    public class AutoCapstoneEligibilityTests
    {
        [TestMethod]
        public void HandledGame_IsSkippedWhateverItHolds()
        {
            // A capstone the user deleted must stay deleted, so the marker wins over everything.
            var decision = AutoCapstoneEligibility.Decide(true, false, new[] { Achievement("a") });

            Assert.AreEqual(AutoCapstoneGenerationAction.Skip, decision.Action);
        }

        [TestMethod]
        public void GameWithoutProviderAchievements_Waits()
        {
            Assert.AreEqual(
                AutoCapstoneGenerationAction.Wait,
                AutoCapstoneEligibility.Decide(false, false, null).Action);
            Assert.AreEqual(
                AutoCapstoneGenerationAction.Wait,
                AutoCapstoneEligibility.Decide(false, false, new List<AchievementDetail>()).Action);

            // Only authored achievements: nothing a provider scanned yet.
            var custom = Achievement("custom:note");
            custom.IsCustom = true;
            Assert.AreEqual(
                AutoCapstoneGenerationAction.Wait,
                AutoCapstoneEligibility.Decide(false, false, new[] { custom }).Action);
        }

        [TestMethod]
        public void GameWithACapstone_IsOnlyMarked()
        {
            var capstone = Achievement("plat", trophyType: "platinum");
            capstone.IsCapstone = true;

            var decision = AutoCapstoneEligibility.Decide(false, false, new[] { Achievement("a"), capstone });

            Assert.AreEqual(AutoCapstoneGenerationAction.MarkHandled, decision.Action);
        }

        [TestMethod]
        public void FilteredCapstone_StillCountsAsHavingOne()
        {
            var capstone = Achievement("win");
            capstone.IsCapstone = true;
            capstone.IsFiltered = true;

            var decision = AutoCapstoneEligibility.Decide(false, false, new[] { Achievement("a"), capstone });

            Assert.AreEqual(AutoCapstoneGenerationAction.MarkHandled, decision.Action);
        }

        [TestMethod]
        public void GameWithAnAutoCapstoneAlready_IsOnlyMarked()
        {
            // One the user added with the button, even if they later un-nominated it.
            var decision = AutoCapstoneEligibility.Decide(false, true, new[] { Achievement("a") });

            Assert.AreEqual(AutoCapstoneGenerationAction.MarkHandled, decision.Action);
        }

        [TestMethod]
        public void UnmarkedPlatinum_IsNominatedRatherThanAuthoredBeside()
        {
            var platinum = Achievement("plat", trophyType: "Platinum");

            var decision = AutoCapstoneEligibility.Decide(false, false, new[] { Achievement("a"), platinum });

            Assert.AreEqual(AutoCapstoneGenerationAction.NominatePlatinum, decision.Action);
            Assert.AreSame(platinum, decision.Platinum);
        }

        [TestMethod]
        public void SeveralPlatinums_NominatesTheBaseGameOne()
        {
            var dlc = Achievement("dlc_plat", trophyType: "platinum", categoryType: "DLC");
            var baseGame = Achievement("base_plat", trophyType: "platinum", categoryType: "Base");

            var decision = AutoCapstoneEligibility.Decide(false, false, new[] { dlc, baseGame });

            Assert.AreSame(baseGame, decision.Platinum);
        }

        [TestMethod]
        public void SeveralUntypedPlatinums_NominatesTheFirstInOrder()
        {
            var first = Achievement("first", trophyType: "platinum");
            var second = Achievement("second", trophyType: "platinum");

            var decision = AutoCapstoneEligibility.Decide(false, false, new[] { first, second });

            Assert.AreSame(first, decision.Platinum);
        }

        [TestMethod]
        public void GameWithNoCapstoneAndNoPlatinum_GetsOneAuthored()
        {
            var decision = AutoCapstoneEligibility.Decide(
                false,
                false,
                new[] { Achievement("a", trophyType: "gold"), Achievement("b") });

            Assert.AreEqual(AutoCapstoneGenerationAction.Author, decision.Action);
        }

        private static AchievementDetail Achievement(
            string apiName,
            string trophyType = null,
            string categoryType = null)
        {
            return new AchievementDetail
            {
                ApiName = apiName,
                DisplayName = apiName,
                TrophyType = trophyType,
                CategoryType = categoryType
            };
        }
    }
}
