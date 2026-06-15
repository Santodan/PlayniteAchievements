using System;
using System.Collections.Generic;

namespace PlayniteAchievements.Models.Achievements.Scoring
{
    public static class AchievementScoreCalculator
    {
        public const int CommonCollectorValue = 15;
        public const int UncommonCollectorValue = 30;
        public const int RareCollectorValue = 90;
        public const int UltraRareCollectorValue = 180;

        private const double MinimumPrestigeRarityPercent = 0.1;
        private const double MaximumPrestigeRarityPercent = 100;
        private const int MinimumPrestigeValue = 1;
        private const int MaximumPrestigeValue = 300;
        private static readonly double[] PrestigeCurvePositions =
        {
            0,
            25,
            50,
            60,
            65,
            70,
            75,
            78,
            80,
            82,
            85,
            87.5,
            90,
            92,
            94,
            95,
            96,
            97.5,
            99,
            99.5,
            99.9
        };
        private static readonly double[] PrestigeCurveScores =
        {
            MinimumPrestigeValue,
            5,
            10,
            16,
            22,
            32,
            50,
            70,
            90,
            110,
            130,
            145,
            162,
            178,
            195,
            210,
            225,
            255,
            282,
            292,
            MaximumPrestigeValue
        };
        private static readonly double[] PrestigeCurveTangents = CreatePrestigeCurveTangents();

        public static AchievementScoreSnapshot CalculateLibraryScores(
            IEnumerable<GameAchievementData> allData,
            int platinumTrophies,
            int goldTrophies,
            int silverTrophies,
            int bronzeTrophies)
        {
            var snapshot = CalculateModernScores(allData);
            snapshot.LegacyScore = CalculateLegacyScore(platinumTrophies, goldTrophies, silverTrophies, bronzeTrophies);
            snapshot.LegacyLevel = AchievementLevelCalculator.CalculateLegacy(snapshot.LegacyScore);
            return snapshot;
        }

        public static AchievementScoreSnapshot CalculateModernScores(IEnumerable<GameAchievementData> allData)
        {
            var collectorScore = 0;
            var prestigeScore = 0;
            if (allData == null)
            {
                return CreateModernScoreSnapshot(collectorScore, prestigeScore);
            }

            foreach (var data in allData)
            {
                var achievements = data?.Achievements;
                if (data?.HasAchievements != true || achievements == null)
                {
                    continue;
                }

                for (var i = 0; i < achievements.Count; i++)
                {
                    var achievement = achievements[i];
                    if (achievement?.Unlocked != true)
                    {
                        continue;
                    }

                    collectorScore = AddClamped(collectorScore, achievement.CollectionScore);
                    prestigeScore = AddClamped(prestigeScore, achievement.PrestigeScore);
                }
            }

            return CreateModernScoreSnapshot(collectorScore, prestigeScore);
        }

        public static AchievementScoreSnapshot CalculateModernScoresFromCounts(
            int commonUnlocked,
            int uncommonUnlocked,
            int rareUnlocked,
            int ultraRareUnlocked)
        {
            var collectorScore = CalculateCollectorScoreFromCounts(
                commonUnlocked,
                uncommonUnlocked,
                rareUnlocked,
                ultraRareUnlocked);

            var prestigeScore = 0;
            prestigeScore = AddClamped(prestigeScore, MultiplyClamped(commonUnlocked, GetPrestigeValue(null, RarityTier.Common)));
            prestigeScore = AddClamped(prestigeScore, MultiplyClamped(uncommonUnlocked, GetPrestigeValue(null, RarityTier.Uncommon)));
            prestigeScore = AddClamped(prestigeScore, MultiplyClamped(rareUnlocked, GetPrestigeValue(null, RarityTier.Rare)));
            prestigeScore = AddClamped(prestigeScore, MultiplyClamped(ultraRareUnlocked, GetPrestigeValue(null, RarityTier.UltraRare)));

            return CreateModernScoreSnapshot(collectorScore, prestigeScore);
        }

        public static AchievementScoreSnapshot CreateModernScoreSnapshot(int collectorScore, int prestigeScore)
        {
            var snapshot = new AchievementScoreSnapshot
            {
                CollectorScore = Math.Max(0, collectorScore),
                PrestigeScore = Math.Max(0, prestigeScore)
            };

            snapshot.CollectorLevel = AchievementLevelCalculator.CalculateModern(snapshot.CollectorScore);
            snapshot.PrestigeLevel = AchievementLevelCalculator.CalculateModern(snapshot.PrestigeScore);
            return snapshot;
        }

        public static int CalculateCollectorScoreFromCounts(
            int commonUnlocked,
            int uncommonUnlocked,
            int rareUnlocked,
            int ultraRareUnlocked)
        {
            var score = 0;
            score = AddClamped(score, MultiplyClamped(commonUnlocked, CommonCollectorValue));
            score = AddClamped(score, MultiplyClamped(uncommonUnlocked, UncommonCollectorValue));
            score = AddClamped(score, MultiplyClamped(rareUnlocked, RareCollectorValue));
            score = AddClamped(score, MultiplyClamped(ultraRareUnlocked, UltraRareCollectorValue));
            return score;
        }

        public static int CalculateLegacyScore(
            int platinumTrophies,
            int goldTrophies,
            int silverTrophies,
            int bronzeTrophies)
        {
            var score = 0;
            score = AddClamped(score, MultiplyClamped(platinumTrophies, 300));
            score = AddClamped(score, MultiplyClamped(goldTrophies, 90));
            score = AddClamped(score, MultiplyClamped(silverTrophies, 30));
            score = AddClamped(score, MultiplyClamped(bronzeTrophies, 15));
            return score;
        }

        public static int GetCollectorValue(RarityTier tier)
        {
            return GetCollectionValue(tier);
        }

        public static int GetCollectionValue(AchievementDetail achievement)
        {
            if (achievement == null)
            {
                return 0;
            }

            return GetCollectionValue(achievement.Rarity);
        }

        public static int GetCollectionValue(RarityTier tier)
        {
            switch (tier)
            {
                case RarityTier.UltraRare:
                    return UltraRareCollectorValue;
                case RarityTier.Rare:
                    return RareCollectorValue;
                case RarityTier.Uncommon:
                    return UncommonCollectorValue;
                default:
                    return CommonCollectorValue;
            }
        }

        public static int GetPrestigeValue(AchievementDetail achievement)
        {
            if (achievement == null)
            {
                return 0;
            }

            return GetPrestigeValue(achievement.GlobalPercentUnlocked, achievement.Rarity);
        }

        public static int GetPrestigeValue(double? globalPercentUnlocked, RarityTier fallbackTier)
        {
            var rarityPercent = GetPrestigeRarityPercent(globalPercentUnlocked, fallbackTier);
            var rawValue = EvaluatePrestigeCurve(rarityPercent);
            var roundedValue = (int)Math.Round(rawValue, MidpointRounding.AwayFromZero);
            return Math.Max(MinimumPrestigeValue, Math.Min(MaximumPrestigeValue, roundedValue));
        }

        public static double GetPrestigeRarityPercent(AchievementDetail achievement)
        {
            if (achievement == null)
            {
                return MaximumPrestigeRarityPercent;
            }

            return GetPrestigeRarityPercent(achievement.GlobalPercentUnlocked, achievement.Rarity);
        }

        public static double GetPrestigeRarityPercent(double? globalPercentUnlocked, RarityTier fallbackTier)
        {
            var percent = globalPercentUnlocked ?? GetFallbackPercent(fallbackTier);
            return Math.Max(
                MinimumPrestigeRarityPercent,
                Math.Min(MaximumPrestigeRarityPercent, percent));
        }

        public static double GetFallbackPercent(RarityTier tier)
        {
            switch (tier)
            {
                case RarityTier.UltraRare:
                    return 2.5;
                case RarityTier.Rare:
                    return 12.5;
                case RarityTier.Uncommon:
                    return 35;
                default:
                    return 75;
            }
        }

        private static double EvaluatePrestigeCurve(double rarityPercent)
        {
            var percent = Math.Max(
                MinimumPrestigeRarityPercent,
                Math.Min(MaximumPrestigeRarityPercent, rarityPercent));

            var position = MaximumPrestigeRarityPercent - percent;
            if (position <= PrestigeCurvePositions[0])
            {
                return PrestigeCurveScores[0];
            }

            var lastIndex = PrestigeCurvePositions.Length - 1;
            if (position >= PrestigeCurvePositions[lastIndex])
            {
                return PrestigeCurveScores[lastIndex];
            }

            var segmentIndex = 0;
            while (segmentIndex < lastIndex - 1 && position > PrestigeCurvePositions[segmentIndex + 1])
            {
                segmentIndex++;
            }

            var startPosition = PrestigeCurvePositions[segmentIndex];
            var endPosition = PrestigeCurvePositions[segmentIndex + 1];
            var span = endPosition - startPosition;
            var t = (position - startPosition) / span;
            var t2 = t * t;
            var t3 = t2 * t;

            var startScore = PrestigeCurveScores[segmentIndex];
            var endScore = PrestigeCurveScores[segmentIndex + 1];
            var startTangent = PrestigeCurveTangents[segmentIndex];
            var endTangent = PrestigeCurveTangents[segmentIndex + 1];

            return ((2 * t3) - (3 * t2) + 1) * startScore
                + (t3 - (2 * t2) + t) * span * startTangent
                + ((-2 * t3) + (3 * t2)) * endScore
                + (t3 - t2) * span * endTangent;
        }

        private static double[] CreatePrestigeCurveTangents()
        {
            var pointCount = PrestigeCurvePositions.Length;
            var intervals = new double[pointCount - 1];
            var slopes = new double[pointCount - 1];
            for (var i = 0; i < pointCount - 1; i++)
            {
                intervals[i] = PrestigeCurvePositions[i + 1] - PrestigeCurvePositions[i];
                slopes[i] = (PrestigeCurveScores[i + 1] - PrestigeCurveScores[i]) / intervals[i];
            }

            var tangents = new double[pointCount];
            tangents[0] = CalculateEndpointTangent(intervals[0], intervals[1], slopes[0], slopes[1]);
            for (var i = 1; i < pointCount - 1; i++)
            {
                if (slopes[i - 1] * slopes[i] <= 0)
                {
                    tangents[i] = 0;
                    continue;
                }

                var previousInterval = intervals[i - 1];
                var currentInterval = intervals[i];
                var previousSlope = slopes[i - 1];
                var currentSlope = slopes[i];
                var firstWeight = (2 * currentInterval) + previousInterval;
                var secondWeight = currentInterval + (2 * previousInterval);
                tangents[i] = (firstWeight + secondWeight) /
                    ((firstWeight / previousSlope) + (secondWeight / currentSlope));
            }

            var lastIndex = pointCount - 1;
            tangents[lastIndex] = CalculateEndpointTangent(
                intervals[lastIndex - 1],
                intervals[lastIndex - 2],
                slopes[lastIndex - 1],
                slopes[lastIndex - 2]);
            return tangents;
        }

        private static double CalculateEndpointTangent(
            double currentInterval,
            double adjacentInterval,
            double currentSlope,
            double adjacentSlope)
        {
            var tangent = (((2 * currentInterval) + adjacentInterval) * currentSlope -
                (currentInterval * adjacentSlope)) / (currentInterval + adjacentInterval);

            if (Math.Sign(tangent) != Math.Sign(currentSlope))
            {
                return 0;
            }

            if (Math.Sign(currentSlope) != Math.Sign(adjacentSlope) &&
                Math.Abs(tangent) > Math.Abs(3 * currentSlope))
            {
                return 3 * currentSlope;
            }

            return tangent;
        }

        private static int AddClamped(int current, int value)
        {
            if (value <= 0)
            {
                return current;
            }

            if (current > int.MaxValue - value)
            {
                return int.MaxValue;
            }

            return current + value;
        }

        private static int MultiplyClamped(int count, int value)
        {
            var safeCount = Math.Max(0, count);
            var result = (long)safeCount * value;
            return result > int.MaxValue ? int.MaxValue : (int)result;
        }
    }
}
