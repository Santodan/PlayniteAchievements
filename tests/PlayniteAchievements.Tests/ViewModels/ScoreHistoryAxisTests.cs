using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Achievements.Scoring;
using PlayniteAchievements.ViewModels.Showcase.Widgets;

namespace PlayniteAchievements.Tests.ViewModels
{
    [TestClass]
    public class ScoreHistoryAxisTests
    {
        // Silver V spans levels 50-59, so level 53 sits inside a tier with Silver IV starting at 60.
        private const int TierStartLevel = 50;
        private const int MidTierLevel = 53;
        private const int NextTierStartLevel = 60;

        private static int LevelStart(int level)
        {
            return AchievementLevelCalculator.GetScoreForLevel(level);
        }

        [TestMethod]
        public void GainInsideOneTier_FitsAxisToWindowWithHeadroomAndDrawsNoLines()
        {
            var windowMin = LevelStart(MidTierLevel) + 200;
            var windowMax = windowMin + 500;

            var frame = ScoreHistoryAxis.Frame(windowMax, windowMin, windowMax);

            Assert.AreEqual(windowMin, frame.Min);
            Assert.AreEqual(windowMax + 500 * ScoreHistoryAxis.Headroom, frame.Max, 0.001);
            Assert.IsNull(frame.CurrentTierLine);
            Assert.IsNull(frame.NextTierLine);
        }

        [TestMethod]
        public void LevelCrossedInsideTier_DrawsNoLines()
        {
            var levelStart = LevelStart(MidTierLevel);
            var windowMin = levelStart - 300;
            var windowMax = levelStart + 400;

            var frame = ScoreHistoryAxis.Frame(windowMax, windowMin, windowMax);

            Assert.IsNull(frame.CurrentTierLine);
            Assert.IsNull(frame.NextTierLine);
        }

        [TestMethod]
        public void TierEnteredInsideWindow_DrawsCurrentTierStart()
        {
            var tierStart = LevelStart(TierStartLevel);
            var windowMin = tierStart - 300;
            var windowMax = LevelStart(MidTierLevel) + 400;

            var frame = ScoreHistoryAxis.Frame(windowMax, windowMin, windowMax);

            Assert.AreEqual(windowMin, frame.Min);
            Assert.AreEqual(tierStart, frame.CurrentTierLine);
            Assert.IsNull(frame.NextTierLine);
        }

        [TestMethod]
        public void NextTierWithinHeadroom_SnapsCeilingToItAndDrawsIt()
        {
            var nextTierStart = LevelStart(NextTierStartLevel);
            var windowMax = nextTierStart - 10;
            var windowMin = windowMax - 1000;

            var frame = ScoreHistoryAxis.Frame(windowMax, windowMin, windowMax);

            Assert.AreEqual(windowMin, frame.Min);
            Assert.AreEqual(nextTierStart, frame.Max);
            Assert.AreEqual(nextTierStart, frame.NextTierLine);
            Assert.IsNull(frame.CurrentTierLine);
        }

        [TestMethod]
        public void ZeroGain_FramesTheCurrentTier()
        {
            var score = LevelStart(MidTierLevel) + 500;

            var frame = ScoreHistoryAxis.Frame(score, score, score);

            Assert.AreEqual(LevelStart(TierStartLevel), frame.Min);
            Assert.AreEqual(LevelStart(NextTierStartLevel), frame.Max);
            Assert.AreEqual(LevelStart(NextTierStartLevel), frame.NextTierLine);
            Assert.IsNull(frame.CurrentTierLine);
        }

        [TestMethod]
        public void ManyTiersCrossed_DrawsOnlyTheCurrentTierStart()
        {
            var windowMin = LevelStart(MidTierLevel - 30);
            var windowMax = LevelStart(MidTierLevel) + 10;

            var frame = ScoreHistoryAxis.Frame(windowMax, windowMin, windowMax);

            Assert.AreEqual(LevelStart(TierStartLevel), frame.CurrentTierLine);
            // Ten percent of a thirty-level gain reaches past the next tier's start only when the
            // remaining tier is small; here seven levels remain, so no snap.
            Assert.IsNull(frame.NextTierLine);
        }

        [TestMethod]
        public void CurrentTierStartAtWindowMin_DrawsNoCurrentTierLine()
        {
            var windowMin = LevelStart(TierStartLevel);
            var windowMax = windowMin + 100;

            var frame = ScoreHistoryAxis.Frame(windowMax, windowMin, windowMax);

            Assert.AreEqual(windowMin, frame.Min);
            Assert.IsNull(frame.CurrentTierLine);
        }

        [TestMethod]
        public void MasteryRollover_NextTierIsTheNewPassStart()
        {
            var score = LevelStart(245) + 5;

            var frame = ScoreHistoryAxis.Frame(score, score, score);

            Assert.AreEqual(LevelStart(240), frame.Min);
            Assert.AreEqual(LevelStart(250), frame.Max);
            Assert.AreEqual(LevelStart(250), frame.NextTierLine);
        }
    }
}
