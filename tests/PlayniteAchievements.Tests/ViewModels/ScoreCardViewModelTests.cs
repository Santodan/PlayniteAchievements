using System.Linq;
using System.Windows.Media;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Achievements.Scoring;
using PlayniteAchievements.ViewModels;

namespace PlayniteAchievements.Tests.ViewModels
{
    [TestClass]
    public class ScoreCardViewModelTests
    {
        // Level 14, roughly half way through it: the fifth level of Bronze IV, so the segmented
        // bar has four solid cells, one part-filled and five empty.
        private const int MidRankScore = 5371;

        [TestMethod]
        public void Labels_UseCollectionAndPrestigeScoreText()
        {
            var collection = new ScoreCardViewModel(ScoreCardType.Collection);
            var prestige = new ScoreCardViewModel(ScoreCardType.Prestige);

            Assert.AreEqual("Collection Score", collection.Label);
            Assert.AreEqual("Prestige Score", prestige.Label);
        }

        [TestMethod]
        public void Apply_FormatsPointsTierAndLevel()
        {
            var card = new ScoreCardViewModel(ScoreCardType.Collection);

            card.Apply(12345, 42, 67, "Gold3", useUniformRarityBadges: false);

            Assert.AreEqual("12,345", card.ScoreText);
            Assert.AreEqual("12,345 pts", card.PointsText);
            Assert.AreEqual("Lv 42", card.LevelText);
            Assert.AreEqual("Gold III", card.TierText);
            Assert.AreEqual(67, card.LevelProgress);
        }

        [TestMethod]
        public void BadgeIconKey_TracksUniformRarityBadgeSetting()
        {
            var card = new ScoreCardViewModel(ScoreCardType.Prestige);

            card.Apply(12345, 42, 67, "Gold5", useUniformRarityBadges: false);
            Assert.AreEqual("ScoreBadgeGoldPentagon", card.BadgeIconKey);

            card.RefreshBadgeStyle(useUniformRarityBadges: true);
            Assert.AreEqual("ScoreBadgeGoldHexagon", card.BadgeIconKey);
        }

        [TestMethod]
        public void CaptionText_PairsTheLevelWithWhatTheBarIsFilling()
        {
            const int score = 315;
            var snapshot = AchievementLevelCalculator.CalculateModern(score);
            var card = new ScoreCardViewModel(ScoreCardType.Collection);

            card.ApplyFromScore(score, useUniformRarityBadges: false);

            StringAssert.Contains(card.CaptionText, card.LevelText);
            StringAssert.Contains(card.CaptionText, card.PointsUntilNextLevelText);
            StringAssert.Contains(
                card.PointsUntilNextLevelText,
                snapshot.PointsUntilNextLevel.ToString("N0"));
            StringAssert.Contains(
                card.PointsUntilNextLevelText,
                $"Lv {snapshot.DisplayLevel + 1}");
        }

        [TestMethod]
        public void TooltipText_StatesTheLadderRatherThanRepeatingTheCard()
        {
            var snapshot = AchievementLevelCalculator.CalculateModern(MidRankScore);
            var card = new ScoreCardViewModel(ScoreCardType.Collection);

            card.ApplyFromScore(MidRankScore, useUniformRarityBadges: false);

            Assert.AreEqual("Level", card.TooltipLevelLabel);
            Assert.AreEqual($"{snapshot.DisplayLevel}/250", card.TooltipLevelValueText);
            Assert.AreEqual("Level in tier", card.TooltipRankPositionLabel);
            Assert.AreEqual(
                $"{snapshot.LevelsCompletedInRank + 1}/{snapshot.LevelsInRank}",
                card.TooltipRankPositionValueText);
            Assert.AreEqual(
                AchievementRankPresentation.FormatRank(snapshot.NextRank),
                card.TooltipNextRankLabel);
            StringAssert.Contains(
                card.TooltipNextRankValueText,
                snapshot.PointsUntilNextRank.ToString("N0"));
        }

        [TestMethod]
        public void TooltipText_ShowsMaxLevelAtCap()
        {
            var card = new ScoreCardViewModel(ScoreCardType.Prestige);

            card.ApplyFromScore(int.MaxValue, useUniformRarityBadges: false);

            Assert.AreEqual("Max level reached", card.PointsUntilNextLevelText);
            Assert.AreEqual("Max level reached", card.TooltipNextRankLabel);
            Assert.AreEqual(string.Empty, card.TooltipNextRankValueText);
        }

        [TestMethod]
        public void Segments_DrawOneCellPerLevelOfTheCurrentRank()
        {
            var snapshot = AchievementLevelCalculator.CalculateModern(MidRankScore);
            var card = new ScoreCardViewModel(ScoreCardType.Collection);

            card.ApplyFromScore(MidRankScore, useUniformRarityBadges: false);

            // Pin the shipped curve: ten levels per rank, and this score sits five levels in.
            Assert.AreEqual(10, snapshot.LevelsInRank);
            Assert.AreEqual(4, snapshot.LevelsCompletedInRank);
            Assert.AreEqual(snapshot.LevelsInRank, card.Segments.Count);

            for (var i = 0; i < snapshot.LevelsCompletedInRank; i++)
            {
                Assert.AreSame(card.AccentBrush, card.Segments[i].Fill, $"segment {i}");
            }

            Assert.IsInstanceOfType(
                card.Segments[snapshot.LevelsCompletedInRank].Fill,
                typeof(LinearGradientBrush));

            for (var i = snapshot.LevelsCompletedInRank + 1; i < card.Segments.Count; i++)
            {
                Assert.AreSame(card.AccentTrackBrush, card.Segments[i].Fill, $"segment {i}");
            }
        }

        [TestMethod]
        public void Segments_AreSolidAtMaxLevel()
        {
            var card = new ScoreCardViewModel(ScoreCardType.Prestige);

            card.ApplyFromScore(int.MaxValue, useUniformRarityBadges: false);

            Assert.AreEqual(10, card.Segments.Count);
            Assert.IsTrue(card.Segments.All(segment => ReferenceEquals(segment.Fill, card.AccentBrush)));
        }

        [TestMethod]
        public void Segments_AreEmptyOnAFreshRank()
        {
            // 2801 is the first point of Bronze IV: a rank just entered, nothing filled yet.
            var card = new ScoreCardViewModel(ScoreCardType.Collection);

            card.ApplyFromScore(2801, useUniformRarityBadges: false);

            Assert.AreEqual(10, card.Segments.Count);
            Assert.IsTrue(card.Segments.All(segment => ReferenceEquals(segment.Fill, card.AccentTrackBrush)));
        }
    }
}
