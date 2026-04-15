using System;
using System.Collections.Generic;
using PlayniteAchievements.ViewModels;

namespace PlayniteAchievements.Services.Sidebar
{
    public sealed class SidebarDataSnapshot
    {
        public List<AchievementDisplayItem> Achievements { get; set; } = new List<AchievementDisplayItem>();
        public List<GameOverviewItem> GamesOverview { get; set; } = new List<GameOverviewItem>();
        public List<AchievementDisplayItem> RecentAchievements { get; set; } = new List<AchievementDisplayItem>();
        public bool HasDeferredRecentAchievements { get; set; }

        public Dictionary<DateTime, int> GlobalUnlockCountsByDate { get; set; } =
            new Dictionary<DateTime, int>();

        public Dictionary<Guid, Dictionary<DateTime, int>> UnlockCountsByDateByGame { get; set; } =
            new Dictionary<Guid, Dictionary<DateTime, int>>();

        public int TotalGames { get; set; }
        public int TotalAchievements { get; set; }
        public int TotalUnlocked { get; set; }
        public int TotalCommon { get; set; }
        public int TotalUncommon { get; set; }
        public int TotalRare { get; set; }
        public int TotalUltraRare { get; set; }
        public int CompletedGames { get; set; }
        public double GlobalProgressionPercent { get; set; }

        /// <summary>
        /// Unlocked achievements per provider (for provider distribution pie chart).
        /// </summary>
        public Dictionary<string, int> UnlockedByProvider { get; set; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Total achievements per provider (including locked, for "unlocked / total" display).
        /// </summary>
        public Dictionary<string, int> TotalByProvider { get; set; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Total locked achievements across all providers (for the locked section of provider pie chart).
        /// </summary>
        public int TotalLocked { get; set; }

        // Total rarity counts (including locked achievements) for "unlocked / total" display
        public int TotalCommonPossible { get; set; }
        public int TotalUncommonPossible { get; set; }
        public int TotalRarePossible { get; set; }
        public int TotalUltraRarePossible { get; set; }
    }
}
