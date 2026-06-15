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

        public int MaxDisplayLevel { get; set; }

        public IReadOnlyList<AchievementRankThreshold> RankThresholds { get; set; }

        public static AchievementLevelCurveSettings LegacyCompatible => new AchievementLevelCurveSettings
        {
            InitialLevelSize = 100,
            BaseLevelGrowth = 100,
            TopEndEaseStartLevel = int.MaxValue,
            TopEndGrowthMultiplier = 1d,
            MaxDisplayLevel = int.MaxValue,
            RankThresholds = CreateDefaultRankThresholds()
        };

        public static AchievementLevelCurveSettings ModernDefault => new AchievementLevelCurveSettings
        {
            InitialLevelSize = 100,
            BaseLevelGrowth = 40,
            TopEndEaseStartLevel = 98,
            TopEndGrowthMultiplier = 0.5d,
            MaxDisplayLevel = 250,
            RankThresholds = CreateDefaultRankThresholds()
        };

        public static IReadOnlyList<AchievementRankThreshold> CreateDefaultRankThresholds()
        {
            return new List<AchievementRankThreshold>
            {
                new AchievementRankThreshold { MaxLevel = 9, Rank = AchievementRank.Bronze5 },
                new AchievementRankThreshold { MaxLevel = 19, Rank = AchievementRank.Bronze4 },
                new AchievementRankThreshold { MaxLevel = 29, Rank = AchievementRank.Bronze3 },
                new AchievementRankThreshold { MaxLevel = 39, Rank = AchievementRank.Bronze2 },
                new AchievementRankThreshold { MaxLevel = 49, Rank = AchievementRank.Bronze1 },
                new AchievementRankThreshold { MaxLevel = 59, Rank = AchievementRank.Silver5 },
                new AchievementRankThreshold { MaxLevel = 69, Rank = AchievementRank.Silver4 },
                new AchievementRankThreshold { MaxLevel = 79, Rank = AchievementRank.Silver3 },
                new AchievementRankThreshold { MaxLevel = 89, Rank = AchievementRank.Silver2 },
                new AchievementRankThreshold { MaxLevel = 99, Rank = AchievementRank.Silver1 },
                new AchievementRankThreshold { MaxLevel = 109, Rank = AchievementRank.Gold5 },
                new AchievementRankThreshold { MaxLevel = 119, Rank = AchievementRank.Gold4 },
                new AchievementRankThreshold { MaxLevel = 129, Rank = AchievementRank.Gold3 },
                new AchievementRankThreshold { MaxLevel = 139, Rank = AchievementRank.Gold2 },
                new AchievementRankThreshold { MaxLevel = 149, Rank = AchievementRank.Gold1 },
                new AchievementRankThreshold { MaxLevel = 159, Rank = AchievementRank.Plat5 },
                new AchievementRankThreshold { MaxLevel = 169, Rank = AchievementRank.Plat4 },
                new AchievementRankThreshold { MaxLevel = 179, Rank = AchievementRank.Plat3 },
                new AchievementRankThreshold { MaxLevel = 189, Rank = AchievementRank.Plat2 },
                new AchievementRankThreshold { MaxLevel = 199, Rank = AchievementRank.Plat1 },
                new AchievementRankThreshold { MaxLevel = 209, Rank = AchievementRank.Master5 },
                new AchievementRankThreshold { MaxLevel = 219, Rank = AchievementRank.Master4 },
                new AchievementRankThreshold { MaxLevel = 229, Rank = AchievementRank.Master3 },
                new AchievementRankThreshold { MaxLevel = 239, Rank = AchievementRank.Master2 },
                new AchievementRankThreshold { MaxLevel = 249, Rank = AchievementRank.Master1 }
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
                MaxDisplayLevel = settings.MaxDisplayLevel <= 0
                    ? int.MaxValue
                    : Math.Max(1, settings.MaxDisplayLevel),
                RankThresholds = settings.RankThresholds ?? CreateDefaultRankThresholds()
            };
        }
    }
}
