using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Achievements.Scoring;
using PlayniteAchievements.ViewModels;

namespace PlayniteAchievements.Tests.ViewModels
{
    [TestClass]
    public class ScoreCardViewModelTests
    {
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
            Assert.AreEqual("Gold III | Lv 42 | 12,345 pts", card.DetailText);
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
        public void TooltipText_UsesModernLevelSnapshot()
        {
            const int score = 315;
            var snapshot = AchievementLevelCalculator.CalculateModern(score);
            var card = new ScoreCardViewModel(ScoreCardType.Collection);

            card.ApplyFromScore(score, useUniformRarityBadges: false);

            StringAssert.Contains(
                card.CurrentLevelPointsText,
                $"{snapshot.CurrentLevelPoints:N0}/{snapshot.CurrentLevelTotalPoints:N0}");
            StringAssert.Contains(
                card.PointsUntilNextLevelText,
                snapshot.PointsUntilNextLevel.ToString("N0"));
            StringAssert.Contains(
                card.PointsUntilNextLevelText,
                $"Lv {snapshot.DisplayLevel + 1}");
            StringAssert.Contains(
                card.NextTierThresholdText,
                snapshot.PointsUntilNextRank.ToString("N0"));
            StringAssert.Contains(
                card.NextTierThresholdText,
                AchievementRankPresentation.FormatRank(snapshot.NextRank));
        }

        [TestMethod]
        public void TooltipText_ShowsMaxLevelAtCap()
        {
            var card = new ScoreCardViewModel(ScoreCardType.Prestige);

            card.ApplyFromScore(int.MaxValue, useUniformRarityBadges: false);

            Assert.AreEqual("Max level reached", card.PointsUntilNextLevelText);
            Assert.AreEqual("Max level reached", card.NextTierThresholdText);
        }
    }
}
