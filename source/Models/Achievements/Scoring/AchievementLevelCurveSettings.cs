using System;
using System.Collections.Generic;

namespace PlayniteAchievements.Models.Achievements.Scoring
{
    public sealed class AchievementLevelCurveSettings
    {
        public int InitialLevelSize { get; set; }

        public int BaseLevelGrowth { get; set; }

        public int TopEndEaseStartLevel { get; set; }

        public double TopEndGrowthMultiplier { get; set; }

        public IReadOnlyList<AchievementRankThreshold> RankThresholds { get; set; }

        public static AchievementLevelCurveSettings LegacyCompatible => new AchievementLevelCurveSettings
        {
            InitialLevelSize = 100,
            BaseLevelGrowth = 100,
            TopEndEaseStartLevel = int.MaxValue,
            TopEndGrowthMultiplier = 1d,
            RankThresholds = CreateDefaultRankThresholds()
        };

        public static AchievementLevelCurveSettings ModernDefault => new AchievementLevelCurveSettings
        {
            InitialLevelSize = 100,
            BaseLevelGrowth = 100,
            TopEndEaseStartLevel = 98,
            TopEndGrowthMultiplier = 0.65d,
            RankThresholds = CreateDefaultRankThresholds()
        };

        public static IReadOnlyList<AchievementRankThreshold> CreateDefaultRankThresholds()
        {
            return new List<AchievementRankThreshold>
            {
                new AchievementRankThreshold { MaxLevel = 3, Rank = AchievementRank.Bronze1 },
                new AchievementRankThreshold { MaxLevel = 7, Rank = AchievementRank.Bronze2 },
                new AchievementRankThreshold { MaxLevel = 12, Rank = AchievementRank.Bronze3 },
                new AchievementRankThreshold { MaxLevel = 21, Rank = AchievementRank.Silver1 },
                new AchievementRankThreshold { MaxLevel = 31, Rank = AchievementRank.Silver2 },
                new AchievementRankThreshold { MaxLevel = 44, Rank = AchievementRank.Silver3 },
                new AchievementRankThreshold { MaxLevel = 59, Rank = AchievementRank.Gold1 },
                new AchievementRankThreshold { MaxLevel = 77, Rank = AchievementRank.Gold2 },
                new AchievementRankThreshold { MaxLevel = 97, Rank = AchievementRank.Gold3 },
                new AchievementRankThreshold { MaxLevel = 119, Rank = AchievementRank.Plat1 },
                new AchievementRankThreshold { MaxLevel = 144, Rank = AchievementRank.Plat2 },
                new AchievementRankThreshold { MaxLevel = 171, Rank = AchievementRank.Plat3 },
                new AchievementRankThreshold { MaxLevel = int.MaxValue, Rank = AchievementRank.Plat }
            }.AsReadOnly();
        }

        internal static AchievementLevelCurveSettings Normalize(AchievementLevelCurveSettings settings)
        {
            settings ??= ModernDefault;

            var multiplier = settings.TopEndGrowthMultiplier;
            if (double.IsNaN(multiplier) || double.IsInfinity(multiplier) || multiplier <= 0)
            {
                multiplier = 1d;
            }

            return new AchievementLevelCurveSettings
            {
                InitialLevelSize = Math.Max(1, settings.InitialLevelSize),
                BaseLevelGrowth = Math.Max(1, settings.BaseLevelGrowth),
                TopEndEaseStartLevel = Math.Max(0, settings.TopEndEaseStartLevel),
                TopEndGrowthMultiplier = multiplier,
                RankThresholds = settings.RankThresholds ?? CreateDefaultRankThresholds()
            };
        }
    }
}
