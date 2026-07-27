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
        public void AchievementDetail_ComputedScoresMatchCalculator()
        {
            var achievement = Achievement(RarityTier.Rare, unlocked: false, percent: 5.0);

            Assert.AreEqual(
                AchievementScoreCalculator.GetCollectionValue(achievement.Rarity),
                achievement.CollectionScore);
            Assert.AreEqual(
                AchievementScoreCalculator.GetPrestigeValue(achievement),
                achievement.PrestigeScore);
        }

        [TestMethod]
        public void CalculateModernScores_SumsUnlockedAchievementScoresOnly()
        {
            var commonUnlocked = Achievement(RarityTier.Common, unlocked: true, percent: 1.0);
            var ultraUnlocked = Achievement(RarityTier.UltraRare, unlocked: true, percent: 0.5);
            var rareLocked = Achievement(RarityTier.Rare, unlocked: false, percent: 2.0);

            var scores = AchievementScoreCalculator.CalculateModernScores(new[]
            {
                new GameAchievementData
                {
                    HasAchievements = true,
                    Achievements = new List<AchievementDetail>
                    {
                        commonUnlocked,
                        ultraUnlocked,
                        rareLocked
                    }
                }
            });

            Assert.AreEqual(
                commonUnlocked.CollectionScore + ultraUnlocked.CollectionScore,
                scores.CollectorScore);
            Assert.AreEqual(
                commonUnlocked.PrestigeScore + ultraUnlocked.PrestigeScore,
                scores.PrestigeScore);
        }

        [TestMethod]
        public void GetPrestigeValue_UsesClampedPercentAndTierFallback()
        {
            Assert.AreEqual(282, AchievementScoreCalculator.GetPrestigeValue(
                Achievement(RarityTier.Common, unlocked: true, percent: 1.0)));
            Assert.AreEqual(5, AchievementScoreCalculator.GetPrestigeValue(
                Achievement(RarityTier.Common, unlocked: true, percent: null)));
            Assert.AreEqual(300, AchievementScoreCalculator.GetPrestigeValue(
                Achievement(RarityTier.UltraRare, unlocked: true, percent: -5.0)));
            Assert.AreEqual(1, AchievementScoreCalculator.GetPrestigeValue(
                Achievement(RarityTier.UltraRare, unlocked: true, percent: 250.0)));
        }

        [TestMethod]
        public void GetPrestigeValue_UsesAggressiveRarityCurve()
        {
            Assert.AreEqual(1, AchievementScoreCalculator.GetPrestigeValue(100, RarityTier.Common));
            Assert.AreEqual(2, AchievementScoreCalculator.GetPrestigeValue(90, RarityTier.Common));
            Assert.AreEqual(5, AchievementScoreCalculator.GetPrestigeValue(75, RarityTier.Common));
            Assert.AreEqual(10, AchievementScoreCalculator.GetPrestigeValue(50, RarityTier.Common));
            Assert.AreEqual(22, AchievementScoreCalculator.GetPrestigeValue(35, RarityTier.Uncommon));
            Assert.AreEqual(32, AchievementScoreCalculator.GetPrestigeValue(30, RarityTier.Uncommon));
            Assert.AreEqual(50, AchievementScoreCalculator.GetPrestigeValue(25, RarityTier.Uncommon));
            Assert.AreEqual(90, AchievementScoreCalculator.GetPrestigeValue(20, RarityTier.Uncommon));
            Assert.AreEqual(145, AchievementScoreCalculator.GetPrestigeValue(12.5, RarityTier.Rare));
            Assert.AreEqual(162, AchievementScoreCalculator.GetPrestigeValue(10, RarityTier.Rare));
            Assert.AreEqual(210, AchievementScoreCalculator.GetPrestigeValue(5, RarityTier.Rare));
            Assert.AreEqual(255, AchievementScoreCalculator.GetPrestigeValue(2.5, RarityTier.UltraRare));
            Assert.AreEqual(300, AchievementScoreCalculator.GetPrestigeValue(0.1, RarityTier.UltraRare));
        }

        [TestMethod]
        public void CalculateModernScoresFromCounts_CommonAndUncommonFallbacksKeepCollectorAhead()
        {
            var scores = AchievementScoreCalculator.CalculateModernScoresFromCounts(
                commonUnlocked: 10,
                uncommonUnlocked: 10,
                rareUnlocked: 0,
                ultraRareUnlocked: 0);

            Assert.IsTrue(scores.CollectorScore > scores.PrestigeScore);
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
        public void CalculateLevel_UsesBronzeFiveAsDefaultAndPreservesFirstBoundary()
        {
            var zero = AchievementLevelCalculator.Calculate(0);
            var endOfFirstRange = AchievementLevelCalculator.Calculate(100);
            var startOfSecondRange = AchievementLevelCalculator.Calculate(101);

            Assert.AreEqual(0, zero.Level);
            Assert.AreEqual(0, zero.DisplayLevel);
            Assert.AreEqual(0, zero.LevelProgress);
            Assert.AreEqual("Bronze5", zero.Rank);
            Assert.AreEqual("Bronze4", zero.NextRank);
            Assert.AreEqual(2801, zero.NextRankScoreThreshold);
            Assert.AreEqual(2801, zero.PointsUntilNextRank);

            Assert.AreEqual(0, endOfFirstRange.Level);
            Assert.AreEqual(0, endOfFirstRange.DisplayLevel);
            Assert.AreEqual(99, endOfFirstRange.LevelProgress);
            Assert.AreEqual("Bronze5", endOfFirstRange.Rank);

            Assert.AreEqual(1, startOfSecondRange.Level);
            Assert.AreEqual(1, startOfSecondRange.DisplayLevel);
            Assert.AreEqual(0, startOfSecondRange.LevelProgress);
            Assert.AreEqual("Bronze5", startOfSecondRange.Rank);
            Assert.AreEqual(1, startOfSecondRange.CurrentLevelPoints);
            Assert.AreEqual(140, startOfSecondRange.CurrentLevelTotalPoints);
        }

        [TestMethod]
        public void CalculateLevel_ChangesTierEveryTenDisplayLevelsAndCapsAtTwoHundredFifty()
        {
            var bronzeFiveEnd = AchievementLevelCalculator.Calculate(2800);
            var bronzeFourStart = AchievementLevelCalculator.Calculate(2801);
            var masterOneStart = AchievementLevelCalculator.Calculate(970981);
            var levelCapStart = AchievementLevelCalculator.Calculate(1040481);
            var aboveCap = AchievementLevelCalculator.Calculate(int.MaxValue);

            Assert.AreEqual(9, bronzeFiveEnd.DisplayLevel);
            Assert.AreEqual("Bronze5", bronzeFiveEnd.Rank);
            Assert.AreEqual("Bronze4", bronzeFiveEnd.NextRank);
            Assert.AreEqual(2801, bronzeFiveEnd.NextRankScoreThreshold);
            Assert.AreEqual(1, bronzeFiveEnd.PointsUntilNextRank);

            Assert.AreEqual(10, bronzeFourStart.DisplayLevel);
            Assert.AreEqual("Bronze4", bronzeFourStart.Rank);

            Assert.AreEqual(240, masterOneStart.DisplayLevel);
            Assert.AreEqual("Master1", masterOneStart.Rank);

            Assert.AreEqual(250, levelCapStart.DisplayLevel);
            Assert.AreEqual("Master1", levelCapStart.Rank);
            Assert.AreEqual(1040481, levelCapStart.CurrentLevelStartScore);
            Assert.AreEqual(1047540, levelCapStart.CurrentLevelEndScore);
            Assert.IsTrue(levelCapStart.IsMaxLevel);
            Assert.AreEqual(100, levelCapStart.LevelProgress);
            Assert.AreEqual(0, levelCapStart.PointsUntilNextLevel);

            Assert.AreEqual(250, aboveCap.DisplayLevel);
            Assert.AreEqual("Master1", aboveCap.Rank);
            Assert.IsTrue(aboveCap.IsMaxLevel);
        }

        [TestMethod]
        public void RankPresentation_FormatsTierAndLevelText()
        {
            Assert.AreEqual("Bronze V", AchievementRankPresentation.FormatRank(AchievementRank.Bronze5));
            Assert.AreEqual("Bronze I", AchievementRankPresentation.FormatRank(AchievementRank.Bronze1));
            Assert.AreEqual("Gold III", AchievementRankPresentation.FormatRank(AchievementRank.Gold3));
            Assert.AreEqual("Gold I", AchievementRankPresentation.FormatRank(AchievementRank.Gold1));
            Assert.AreEqual("Platinum I", AchievementRankPresentation.FormatRank("Plat1"));
            Assert.AreEqual("Master I", AchievementRankPresentation.FormatRank(AchievementRank.Master1));
        }

        [TestMethod]
        public void RankPresentation_MapsTierToRarityBadgeIcon()
        {
            Assert.AreEqual("BadgeBronzeTriangle", AchievementRankPresentation.GetBadgeIconKey(AchievementRank.Bronze5));
            Assert.AreEqual("BadgeSilverSquare", AchievementRankPresentation.GetBadgeIconKey(AchievementRank.Silver5));
            Assert.AreEqual("BadgeGoldPentagon", AchievementRankPresentation.GetBadgeIconKey(AchievementRank.Gold5));
            Assert.AreEqual("BadgePlatinumHexagon", AchievementRankPresentation.GetBadgeIconKey(AchievementRank.Plat5));
            Assert.AreEqual("BadgeCompletedGame", AchievementRankPresentation.GetBadgeIconKey(AchievementRank.Master1));

            Assert.AreEqual("BadgeBronzeHexagon", AchievementRankPresentation.GetBadgeIconKey(AchievementRank.Bronze5, useUniformRarityBadges: true));
            Assert.AreEqual("BadgeSilverHexagon", AchievementRankPresentation.GetBadgeIconKey(AchievementRank.Silver5, useUniformRarityBadges: true));
            Assert.AreEqual("BadgeGoldHexagon", AchievementRankPresentation.GetBadgeIconKey(AchievementRank.Gold5, useUniformRarityBadges: true));
            Assert.AreEqual("BadgePlatinumHexagon", AchievementRankPresentation.GetBadgeIconKey(AchievementRank.Plat5, useUniformRarityBadges: true));
            Assert.AreEqual("BadgeCompletedGame", AchievementRankPresentation.GetBadgeIconKey(AchievementRank.Master5, useUniformRarityBadges: true));
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
