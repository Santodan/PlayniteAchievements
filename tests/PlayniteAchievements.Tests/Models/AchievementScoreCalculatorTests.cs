using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Achievements.Scoring;
using System;
using System.Collections.Generic;

namespace PlayniteAchievements.Tests.Models
{
    [TestClass]
    public class AchievementScoreCalculatorTests
    {
        [TestMethod]
        public void CalculateModernScores_UsesCollectorTierValuesForUnlockedAchievementsOnly()
        {
            var scores = AchievementScoreCalculator.CalculateModernScores(new[]
            {
                new GameAchievementData
                {
                    HasAchievements = true,
                    Achievements = new List<AchievementDetail>
                    {
                        Achievement(RarityTier.Common, unlocked: true),
                        Achievement(RarityTier.Uncommon, unlocked: true),
                        Achievement(RarityTier.Rare, unlocked: true),
                        Achievement(RarityTier.UltraRare, unlocked: true),
                        Achievement(RarityTier.UltraRare, unlocked: false)
                    }
                }
            });

            Assert.AreEqual(315, scores.CollectorScore);
        }

        [TestMethod]
        public void GetPrestigeValue_UsesClampedPercentAndTierFallback()
        {
            Assert.AreEqual(200, AchievementScoreCalculator.GetPrestigeValue(
                Achievement(RarityTier.Common, unlocked: true, percent: 1.0)));
            Assert.AreEqual(37, AchievementScoreCalculator.GetPrestigeValue(
                Achievement(RarityTier.Common, unlocked: true, percent: null)));
            Assert.AreEqual(300, AchievementScoreCalculator.GetPrestigeValue(
                Achievement(RarityTier.UltraRare, unlocked: true, percent: -5.0)));
            Assert.AreEqual(30, AchievementScoreCalculator.GetPrestigeValue(
                Achievement(RarityTier.UltraRare, unlocked: true, percent: 250.0)));
        }

        [TestMethod]
        public void CalculateLegacyScore_PreservesExistingScoreWeights()
        {
            var score = AchievementScoreCalculator.CalculateLegacyScore(
                platinumTrophies: 1,
                goldTrophies: 2,
                silverTrophies: 3,
                bronzeTrophies: 4);

            Assert.AreEqual(630, score);
        }

        [TestMethod]
        public void CalculateLevel_PreservesExistingLevelBoundaries()
        {
            var zero = AchievementLevelCalculator.Calculate(0);
            var endOfFirstRange = AchievementLevelCalculator.Calculate(100);
            var startOfSecondRange = AchievementLevelCalculator.Calculate(101);

            Assert.AreEqual(0, zero.Level);
            Assert.AreEqual(0, zero.LevelProgress);
            Assert.AreEqual("Bronze1", zero.Rank);

            Assert.AreEqual(0, endOfFirstRange.Level);
            Assert.AreEqual(99, endOfFirstRange.LevelProgress);
            Assert.AreEqual("Bronze1", endOfFirstRange.Rank);

            Assert.AreEqual(1, startOfSecondRange.Level);
            Assert.AreEqual(0, startOfSecondRange.LevelProgress);
            Assert.AreEqual("Bronze1", startOfSecondRange.Rank);
        }

        [TestMethod]
        public void RankPresentation_FormatsTierAndLevelText()
        {
            Assert.AreEqual("Bronze 3", AchievementRankPresentation.FormatRank(AchievementRank.Bronze3));
            Assert.AreEqual("Silver 2", AchievementRankPresentation.FormatRank("Silver2"));
            Assert.AreEqual("Gold 1", AchievementRankPresentation.FormatRank(AchievementRank.Gold1));
            Assert.AreEqual("Platinum 3", AchievementRankPresentation.FormatRank("Plat3"));
            Assert.AreEqual("Platinum", AchievementRankPresentation.FormatRank(AchievementRank.Plat));
        }

        [TestMethod]
        public void RankPresentation_MapsTierToRarityBadgeIcon()
        {
            Assert.AreEqual("BadgeBronzeTriangle", AchievementRankPresentation.GetBadgeIconKey(AchievementRank.Bronze1));
            Assert.AreEqual("BadgeSilverSquare", AchievementRankPresentation.GetBadgeIconKey(AchievementRank.Silver1));
            Assert.AreEqual("BadgeGoldPentagon", AchievementRankPresentation.GetBadgeIconKey(AchievementRank.Gold1));
            Assert.AreEqual("BadgePlatinumHexagon", AchievementRankPresentation.GetBadgeIconKey(AchievementRank.Plat1));

            Assert.AreEqual("BadgeBronzeHexagon", AchievementRankPresentation.GetBadgeIconKey(AchievementRank.Bronze1, useUniformRarityBadges: true));
            Assert.AreEqual("BadgeSilverHexagon", AchievementRankPresentation.GetBadgeIconKey(AchievementRank.Silver1, useUniformRarityBadges: true));
            Assert.AreEqual("BadgeGoldHexagon", AchievementRankPresentation.GetBadgeIconKey(AchievementRank.Gold1, useUniformRarityBadges: true));
            Assert.AreEqual("BadgePlatinumHexagon", AchievementRankPresentation.GetBadgeIconKey(AchievementRank.Plat, useUniformRarityBadges: true));
        }

        private static AchievementDetail Achievement(
            RarityTier rarity,
            bool unlocked,
            double? percent = null)
        {
            return new AchievementDetail
            {
                DisplayName = Guid.NewGuid().ToString("N"),
                Rarity = rarity,
                GlobalPercentUnlocked = percent,
                Unlocked = unlocked
            };
        }
    }
}
