using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Achievements.Scoring;
using PlayniteAchievements.ViewModels.Showcase.Widgets;

namespace PlayniteAchievements.Tests.ViewModels
{
    [TestClass]
    public class ScoreHistoryAxisTests
    {
        // Silver V spans levels 50-59 and Gold V starts at level 100, so level 53 sits well inside
        // a tier and level 100 is both a level start and a tier start.
        private const int MidTierLevel = 53;

        private static int LevelStart(int level)
        {
            return AchievementLevelCalculator.GetScoreForLevel(level);
        }

        [TestMethod]
        public void GainInsideOneLevel_FitsAxisToWindowWithHeadroomAndDrawsNoLines()
        {
            var windowMin = LevelStart(MidTierLevel) + 200;
            var windowMax = windowMin + 500;

            var frame = ScoreHistoryAxis.Frame(windowMax, windowMin, windowMax);

            Assert.AreEqual(windowMin, frame.Min);
            Assert.AreEqual(windowMax + 500 * ScoreHistoryAxis.Headroom, frame.Max, 0.001);
            Assert.IsNull(frame.CurrentLevelLine);
            Assert.IsNull(frame.NextLevelLine);
            Assert.AreEqual(0, frame.TierStarts.Count);
        }

        [TestMethod]
        public void LevelCrossedInsideWindow_DrawsCurrentLevelStartOnly()
        {
            var levelStart = LevelStart(MidTierLevel);
            var windowMin = levelStart - 300;
            var windowMax = levelStart + 400;

            var frame = ScoreHistoryAxis.Frame(windowMax, windowMin, windowMax);

            Assert.AreEqual(windowMin, frame.Min);
            Assert.AreEqual(windowMax + 700 * ScoreHistoryAxis.Headroom, frame.Max, 0.001);
            Assert.AreEqual(levelStart, frame.CurrentLevelLine);
            Assert.IsNull(frame.NextLevelLine);
            Assert.AreEqual(0, frame.TierStarts.Count);
        }

        [TestMethod]
        public void NextLevelWithinHeadroom_SnapsCeilingToItAndDrawsIt()
        {
            var nextStart = LevelStart(MidTierLevel + 1);
            var windowMax = nextStart - 10;
            var windowMin = windowMax - 1000;

            var frame = ScoreHistoryAxis.Frame(windowMax, windowMin, windowMax);

            Assert.AreEqual(windowMin, frame.Min);
            Assert.AreEqual(nextStart, frame.Max);
            Assert.AreEqual(nextStart, frame.NextLevelLine);
            Assert.IsNull(frame.CurrentLevelLine);
        }

        [TestMethod]
        public void ZeroGain_FramesTheCurrentLevel()
        {
            var score = LevelStart(MidTierLevel) + 500;

            var frame = ScoreHistoryAxis.Frame(score, score, score);

            Assert.AreEqual(LevelStart(MidTierLevel), frame.Min);
            Assert.AreEqual(LevelStart(MidTierLevel + 1), frame.Max);
            Assert.AreEqual(LevelStart(MidTierLevel + 1), frame.NextLevelLine);
            Assert.IsNull(frame.CurrentLevelLine);
            Assert.AreEqual(0, frame.TierStarts.Count);
        }

        [TestMethod]
        public void ManyLevelsCrossed_DrawsOnlyTheCurrentLevelAndTiersReached()
        {
            var windowMin = LevelStart(MidTierLevel - 30);
            var windowMax = LevelStart(MidTierLevel) + 10;

            var frame = ScoreHistoryAxis.Frame(windowMax, windowMin, windowMax);

            Assert.AreEqual(LevelStart(MidTierLevel), frame.CurrentLevelLine);
            // Ten percent of a thirty-level gain is wider than one level, so the ceiling snaps to
            // the next level and it is drawn; the older crossings are not.
            Assert.AreEqual(LevelStart(MidTierLevel + 1), frame.Max);
            Assert.AreEqual(LevelStart(MidTierLevel + 1), frame.NextLevelLine);
            Assert.AreEqual(1, frame.TierStarts.Count);
            Assert.AreEqual(AchievementRank.Silver5, frame.TierStarts[0].Rank);
            Assert.AreEqual(LevelStart(50), frame.TierStarts[0].Score);
        }

        [TestMethod]
        public void CurrentLevelStartAtWindowMin_DrawsNoCurrentLevelLine()
        {
            var windowMin = LevelStart(MidTierLevel);
            var windowMax = windowMin + 100;

            var frame = ScoreHistoryAxis.Frame(windowMax, windowMin, windowMax);

            Assert.AreEqual(windowMin, frame.Min);
            Assert.IsNull(frame.CurrentLevelLine);
        }

        [TestMethod]
        public void AllTimeWindow_DrawsOneLinePerTierReachedAndSkipsTheFirstRank()
        {
            var windowMax = LevelStart(105) + 5;

            var frame = ScoreHistoryAxis.Frame(windowMax, 0, windowMax);

            Assert.AreEqual(0, frame.Min);
            Assert.AreEqual(LevelStart(105), frame.CurrentLevelLine);
            CollectionAssert.AreEqual(
                new[] { AchievementRank.Silver5, AchievementRank.Gold5 },
                frame.TierStarts.Select(tier => tier.Rank).ToArray());
            CollectionAssert.AreEqual(
                new[] { (double)LevelStart(50), LevelStart(100) },
                frame.TierStarts.Select(tier => tier.Score).ToArray());
        }

        [TestMethod]
        public void MasteryRollover_DrawsTheNewPassAsATierStart()
        {
            var windowMin = LevelStart(240);
            var windowMax = LevelStart(252);

            var frame = ScoreHistoryAxis.Frame(windowMax, windowMin, windowMax);

            Assert.AreEqual(1, frame.TierStarts.Count);
            Assert.AreEqual(AchievementRank.Bronze5, frame.TierStarts[0].Rank);
            Assert.AreEqual(LevelStart(250), frame.TierStarts[0].Score);
            Assert.AreEqual(LevelStart(252), frame.CurrentLevelLine);
        }

        [TestMethod]
        public void CurrentLevelStartingATier_DrawsTheTierLineInsteadOfTheLevelLine()
        {
            var windowMin = LevelStart(95);
            var windowMax = LevelStart(100) + 5;

            var frame = ScoreHistoryAxis.Frame(windowMax, windowMin, windowMax);

            Assert.AreEqual(1, frame.TierStarts.Count);
            Assert.AreEqual(AchievementRank.Gold5, frame.TierStarts[0].Rank);
            Assert.AreEqual(LevelStart(100), frame.TierStarts[0].Score);
            Assert.IsNull(frame.CurrentLevelLine);
        }
    }
}
