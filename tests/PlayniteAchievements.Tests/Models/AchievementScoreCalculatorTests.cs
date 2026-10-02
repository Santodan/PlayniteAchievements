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
        public void CalculateLevel_ChangesTierEveryTenDisplayLevels()
        {
            var bronzeFiveEnd = AchievementLevelCalculator.Calculate(2800);
            var bronzeFourStart = AchievementLevelCalculator.Calculate(2801);
            var masterOneStart = AchievementLevelCalculator.Calculate(970981);

            Assert.AreEqual(9, bronzeFiveEnd.DisplayLevel);
            Assert.AreEqual("Bronze5", bronzeFiveEnd.Rank);
            Assert.AreEqual("Bronze4", bronzeFiveEnd.NextRank);
            Assert.AreEqual(2801, bronzeFiveEnd.NextRankScoreThreshold);
            Assert.AreEqual(1, bronzeFiveEnd.PointsUntilNextRank);

            Assert.AreEqual(10, bronzeFourStart.DisplayLevel);
            Assert.AreEqual("Bronze4", bronzeFourStart.Rank);

            Assert.AreEqual(240, masterOneStart.DisplayLevel);
            Assert.AreEqual("Master1", masterOneStart.Rank);
            Assert.AreEqual(0, masterOneStart.Mastery);
        }

        [TestMethod]
        public void CalculateLevel_ReportsTheRankLevelSpanForTheSegmentedBar()
        {
            var rankStart = AchievementLevelCalculator.Calculate(2801);
            var midRank = AchievementLevelCalculator.Calculate(5371);
            var rankEnd = AchievementLevelCalculator.Calculate(9600);
            var passEnd = AchievementLevelCalculator.Calculate(1040480);

            // Bronze IV covers levels 10-19, so a bar drawn from the span has ten cells.
            Assert.AreEqual(10, rankStart.RankStartLevel);
            Assert.AreEqual(19, rankStart.RankEndLevel);
            Assert.AreEqual(10, rankStart.LevelsInRank);
            Assert.AreEqual(0, rankStart.LevelsCompletedInRank);
            Assert.AreEqual(10, rankStart.LevelsUntilNextRank);

            Assert.AreEqual(14, midRank.DisplayLevel);
            Assert.AreEqual(4, midRank.LevelsCompletedInRank);
            Assert.AreEqual(6, midRank.LevelsUntilNextRank);

            Assert.AreEqual(19, rankEnd.DisplayLevel);
            Assert.AreEqual(9, rankEnd.LevelsCompletedInRank);
            Assert.AreEqual(1, rankEnd.LevelsUntilNextRank);

            // The last point of a pass is the final level of Master I, one level from mastery.
            Assert.AreEqual(240, passEnd.RankStartLevel);
            Assert.AreEqual(249, passEnd.RankEndLevel);
            Assert.AreEqual(9, passEnd.LevelsCompletedInRank);
            Assert.AreEqual(1, passEnd.LevelsUntilNextRank);
        }

        [TestMethod]
        public void CalculateLevel_MasteryRestartsTheLadderAndKeepsCountingLevels()
        {
            const int cycle = 1040480;
            var passEnd = AchievementLevelCalculator.Calculate(cycle);
            var masteryOne = AchievementLevelCalculator.Calculate(cycle + 1);
            var masteryOneBronzeFour = AchievementLevelCalculator.Calculate(cycle + 2801);
            var masteryTwo = AchievementLevelCalculator.Calculate((2 * cycle) + 1);
            var top = AchievementLevelCalculator.Calculate(int.MaxValue);

            Assert.AreEqual(0, passEnd.Mastery);
            Assert.AreEqual(249, passEnd.DisplayLevel);
            Assert.AreEqual("Master1", passEnd.Rank);
            Assert.AreEqual("Bronze5", passEnd.NextRank);
            Assert.AreEqual(cycle + 1, passEnd.NextRankScoreThreshold);
            Assert.AreEqual(1, passEnd.PointsUntilNextRank);
            Assert.AreEqual(1, passEnd.PointsUntilNextLevel);
            Assert.IsFalse(passEnd.IsMaxLevel);

            Assert.AreEqual(1, masteryOne.Mastery);
            Assert.AreEqual(250, masteryOne.Level);
            Assert.AreEqual(250, masteryOne.DisplayLevel);
            Assert.AreEqual(0, masteryOne.PassLevel);
            Assert.AreEqual("Bronze5", masteryOne.Rank);
            Assert.AreEqual("Bronze4", masteryOne.NextRank);
            Assert.AreEqual(cycle + 1, masteryOne.CurrentLevelStartScore);
            Assert.AreEqual(cycle + 100, masteryOne.CurrentLevelEndScore);
            Assert.AreEqual(cycle + 2801, masteryOne.NextRankScoreThreshold);
            Assert.AreEqual(0, masteryOne.LevelsCompletedInRank);
            Assert.AreEqual(250, masteryOne.RankStartLevel);
            Assert.IsFalse(masteryOne.IsMaxLevel);

            Assert.AreEqual(1, masteryOneBronzeFour.Mastery);
            Assert.AreEqual(260, masteryOneBronzeFour.DisplayLevel);
            Assert.AreEqual("Bronze4", masteryOneBronzeFour.Rank);

            Assert.AreEqual(2, masteryTwo.Mastery);
            Assert.AreEqual(500, masteryTwo.DisplayLevel);
            Assert.AreEqual("Bronze5", masteryTwo.Rank);

            Assert.AreEqual((int.MaxValue - 1) / cycle, top.Mastery);
            Assert.IsTrue(top.DisplayLevel > 0);
            Assert.IsTrue(top.CurrentLevelEndScore >= top.CurrentLevelStartScore);
        }

        [TestMethod]
        public void GetScoreForLevel_RoundTripsThroughCalculateAcrossMasteries()
        {
            foreach (var level in new[] { 0, 1, 9, 10, 98, 99, 249, 250, 251, 499, 500, 1234 })
            {
                var score = AchievementLevelCalculator.GetScoreForLevel(level);
                Assert.AreEqual(level, AchievementLevelCalculator.Calculate(score).Level, "start of " + level);
                Assert.AreEqual(Math.Max(0, level - 1), AchievementLevelCalculator.Calculate(score - 1).Level, "before " + level);
            }
        }

        [TestMethod]
        public void CalculateLegacy_DoesNotUseMastery()
        {
            var legacy = AchievementLevelCalculator.CalculateLegacy(5000000);

            Assert.AreEqual(0, legacy.Mastery);
            Assert.AreEqual(legacy.Level, legacy.PassLevel);
            Assert.AreEqual("Master1", legacy.Rank);
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

        /// <summary>
        /// The score card keys are the badge keys under a "Score" prefix. Pinned so the two cannot
        /// drift apart again now that both resolve through one shape table.
        /// </summary>
        [TestMethod]
        public void RankPresentation_ScoreCardBadgeIconPrefixesTheRarityBadgeIcon()
        {
            foreach (var rank in new[]
            {
                AchievementRank.Bronze5,
                AchievementRank.Silver5,
                AchievementRank.Gold5,
                AchievementRank.Plat5,
                AchievementRank.Master1
            })
            {
                foreach (var uniform in new[] { false, true })
                {
                    Assert.AreEqual(
                        "Score" + AchievementRankPresentation.GetBadgeIconKey(rank, uniform),
                        AchievementRankPresentation.GetScoreCardBadgeIconKey(rank, uniform),
                        $"{rank}, uniform={uniform}");
                }
            }
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
