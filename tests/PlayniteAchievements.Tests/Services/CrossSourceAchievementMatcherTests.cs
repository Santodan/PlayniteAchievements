using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Services.Metadata;
using System.Linq;

namespace PlayniteAchievements.Tests.Services
{
    [TestClass]
    public class CrossSourceAchievementMatcherTests
    {
        [TestMethod]
        public void UniqueTitle_MatchesIgnoringCaseAndPunctuation()
        {
            var target = Achievement("epic_1", "Dragon-Slayer!");
            var source = Achievement("steam_1", "dragon slayer");

            var matches = CrossSourceAchievementMatcher.Match(new[] { target }, new[] { source });

            Assert.AreEqual(1, matches.Count);
            Assert.AreSame(target, matches[0].Target);
            Assert.AreSame(source, matches[0].Source);
        }

        [TestMethod]
        public void DuplicateTitles_AreSeparatedByDescription()
        {
            var first = Achievement("a", "Collector", "Find all coins");
            var second = Achievement("b", "Collector", "Find all gems");
            var gems = Achievement("x", "Collector", "find all gems.");
            var coins = Achievement("y", "Collector", "Find all coins");

            var matches = CrossSourceAchievementMatcher.Match(new[] { first, second }, new[] { gems, coins });

            Assert.AreSame(coins, matches.Single(m => m.Target == first).Source);
            Assert.AreSame(gems, matches.Single(m => m.Target == second).Source);
        }

        [TestMethod]
        public void TiedScores_LeaveTheAchievementUnmatched()
        {
            var first = Achievement("a", "Collector");
            var second = Achievement("b", "Collector");
            var source1 = Achievement("x", "Collector");
            var source2 = Achievement("y", "Collector");

            var matches = CrossSourceAchievementMatcher.Match(new[] { first, second }, new[] { source1, source2 });

            Assert.AreEqual(0, matches.Count);
        }

        [TestMethod]
        public void SourceRow_IsUsedOnlyOnce()
        {
            var first = Achievement("a", "Collector", "Find all coins");
            var second = Achievement("b", "Collector", "Find all coins");
            var source = Achievement("x", "Collector", "Find all coins");

            var matches = CrossSourceAchievementMatcher.Match(new[] { first, second }, new[] { source });

            Assert.AreEqual(1, matches.Count);
            Assert.AreSame(first, matches[0].Target);
        }

        [TestMethod]
        public void SourceFilter_ExcludesRows()
        {
            var target = Achievement("a", "Collector");
            var source = Achievement("x", "Collector");

            var matches = CrossSourceAchievementMatcher.Match(new[] { target }, new[] { source }, row => false);

            Assert.AreEqual(0, matches.Count);
        }

        [TestMethod]
        public void ApiNamePass_WinsOverTitle_WhenEnabled()
        {
            var target = Achievement("ACH_WIN", "Victory");
            var renamed = Achievement("ach_win", "Triumph");
            var sameTitle = Achievement("OTHER", "Victory");

            var matches = CrossSourceAchievementMatcher.Match(
                new[] { target },
                new[] { sameTitle, renamed },
                matchApiNames: true);

            Assert.AreEqual(1, matches.Count);
            Assert.AreSame(renamed, matches[0].Source);
        }

        [TestMethod]
        public void ApiNamePass_IsOffByDefault()
        {
            var target = Achievement("ACH_WIN", "Victory");
            var renamed = Achievement("ACH_WIN", "Triumph");

            var matches = CrossSourceAchievementMatcher.Match(new[] { target }, new[] { renamed });

            Assert.AreEqual(0, matches.Count);
        }

        [TestMethod]
        public void ApiNameMatchedTargets_DoNotCountTowardTitleDuplicates()
        {
            // Once "a" is joined by ApiName, "b" is the only remaining "Collector" and pairs 1:1.
            var first = Achievement("a", "Collector");
            var second = Achievement("b", "Collector");
            var byApi = Achievement("a", "Something else");
            var byTitle = Achievement("z", "Collector");

            var matches = CrossSourceAchievementMatcher.Match(
                new[] { first, second },
                new[] { byApi, byTitle },
                matchApiNames: true);

            Assert.AreEqual(2, matches.Count);
            Assert.AreSame(byTitle, matches.Single(m => m.Target == second).Source);
        }

        private static AchievementDetail Achievement(string apiName, string title, string description = null)
        {
            return new AchievementDetail
            {
                ApiName = apiName,
                DisplayName = title,
                Description = description
            };
        }
    }
}
