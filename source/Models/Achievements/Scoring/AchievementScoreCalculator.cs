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
        private const int MinimumPrestigeValue = 10;
        private const int MaximumPrestigeValue = 500;

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

                    collectorScore = AddClamped(collectorScore, GetCollectorValue(achievement.Rarity));
                    prestigeScore = AddClamped(prestigeScore, GetPrestigeValue(achievement));
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
            var rawValue = 100 * Math.Log10((100 / rarityPercent) + 1);
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
