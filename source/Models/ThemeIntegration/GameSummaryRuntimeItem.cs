using System;
using PlayniteAchievements.Models.Achievements;

namespace PlayniteAchievements.Models.ThemeIntegration
{
    internal sealed class GameSummaryRuntimeItem
    {
        public Guid GameId { get; set; }
        public string Name { get; set; }
        public string Platform { get; set; }
        public string CoverImagePath { get; set; }
        public int Progress { get; set; }
        public int GoldCount { get; set; }
        public int SilverCount { get; set; }
        public int BronzeCount { get; set; }
        public bool IsCompleted { get; set; }
        public DateTime LastUnlockDate { get; set; }
        public AchievementRarityStats Common { get; set; } = new AchievementRarityStats();
        public AchievementRarityStats Uncommon { get; set; } = new AchievementRarityStats();
        public AchievementRarityStats Rare { get; set; } = new AchievementRarityStats();
        public AchievementRarityStats UltraRare { get; set; } = new AchievementRarityStats();
        public AchievementRarityStats RareAndUltraRare { get; set; } = new AchievementRarityStats();
        public AchievementRarityStats Overall { get; set; } = new AchievementRarityStats();
    }
}
