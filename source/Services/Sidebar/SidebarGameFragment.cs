using System;
using System.Collections.Generic;
using PlayniteAchievements.ViewModels;

namespace PlayniteAchievements.Services.Sidebar
{
    public sealed class SidebarGameFragment
    {
        public string CacheKey { get; set; }
        public Guid? PlayniteGameId { get; set; }
        public string ProviderKey { get; set; }
        public string ProviderName { get; set; }

        public List<AchievementDisplayItem> Achievements { get; set; } = new List<AchievementDisplayItem>();
        public List<RecentAchievementItem> RecentAchievements { get; set; } = new List<RecentAchievementItem>();
        public GameOverviewItem GameOverview { get; set; }
        public Dictionary<DateTime, int> UnlockCountsByDate { get; set; } = new Dictionary<DateTime, int>();

        public int TotalAchievements { get; set; }
        public int UnlockedAchievements { get; set; }

        // Unlocked rarity counts
        public int CommonCount { get; set; }
        public int UncommonCount { get; set; }
        public int RareCount { get; set; }
        public int UltraRareCount { get; set; }

        // Total rarity counts (including locked achievements)
        public int TotalCommonPossible { get; set; }
        public int TotalUncommonPossible { get; set; }
        public int TotalRarePossible { get; set; }
        public int TotalUltraRarePossible { get; set; }

        // Trophy counts (for PlayStation games)
        public int TrophyPlatinumCount { get; set; }
        public int TrophyGoldCount { get; set; }
        public int TrophySilverCount { get; set; }
        public int TrophyBronzeCount { get; set; }
        public int TrophyPlatinumTotal { get; set; }
        public int TrophyGoldTotal { get; set; }
        public int TrophySilverTotal { get; set; }
        public int TrophyBronzeTotal { get; set; }

        public bool IsCompleted { get; set; }
    }
}
