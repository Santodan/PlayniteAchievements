using PlayniteAchievements.Models.Achievements;
using System;
using System.Collections.Generic;

namespace PlayniteAchievements.Services.Cache
{
    internal sealed class CachedSummaryData
    {
        public List<CachedGameSummaryData> Games { get; set; } = new List<CachedGameSummaryData>();

        public List<CachedRecentUnlockData> RecentUnlocks { get; set; } = new List<CachedRecentUnlockData>();

        public Dictionary<DateTime, int> GlobalUnlockCountsByDate { get; set; } =
            new Dictionary<DateTime, int>();

        public Dictionary<Guid, Dictionary<DateTime, int>> UnlockCountsByDateByGame { get; set; } =
            new Dictionary<Guid, Dictionary<DateTime, int>>();

        public bool HasMoreRecentUnlocks { get; set; }
    }

    internal sealed class CachedGameSummaryData
    {
        public string CacheKey { get; set; }

        public Guid? PlayniteGameId { get; set; }

        public string ProviderKey { get; set; }

        public string ProviderPlatformKey { get; set; }

        public int AppId { get; set; }

        public string ProviderGameKey { get; set; }

        public string GameName { get; set; }

        public bool HasAchievements { get; set; }

        public DateTime LastUpdatedUtc { get; set; }

        public int TotalAchievements { get; set; }

        public int UnlockedAchievements { get; set; }

        public int CollectionScore { get; set; }

        public int CollectionScoreTotal { get; set; }

        public int PrestigeScore { get; set; }

        public int PrestigeScoreTotal { get; set; }

        public int Points { get; set; }

        public int CommonCount { get; set; }

        public int UncommonCount { get; set; }

        public int RareCount { get; set; }

        public int UltraRareCount { get; set; }

        public int TotalCommonPossible { get; set; }

        public int TotalUncommonPossible { get; set; }

        public int TotalRarePossible { get; set; }

        public int TotalUltraRarePossible { get; set; }

        public int TrophyPlatinumCount { get; set; }

        public int TrophyGoldCount { get; set; }

        public int TrophySilverCount { get; set; }

        public int TrophyBronzeCount { get; set; }

        public int TrophyPlatinumTotal { get; set; }

        public int TrophyGoldTotal { get; set; }

        public int TrophySilverTotal { get; set; }

        public int TrophyBronzeTotal { get; set; }

        public bool IsCompleted { get; set; }

        public DateTime? LastUnlockUtc { get; set; }
    }

    internal sealed class CachedRecentUnlockData
    {
        public string CacheKey { get; set; }

        public Guid? PlayniteGameId { get; set; }

        public string ProviderKey { get; set; }

        public string ProviderPlatformKey { get; set; }

        public int AppId { get; set; }

        public string ProviderGameKey { get; set; }

        public string GameName { get; set; }

        public string ApiName { get; set; }

        public string DisplayName { get; set; }

        public string Description { get; set; }

        public string UnlockedIconPath { get; set; }

        public string LockedIconPath { get; set; }

        public int? Points { get; set; }

        public int? ScaledPoints { get; set; }

        public string Category { get; set; }

        public string CategoryType { get; set; }

        public string TrophyType { get; set; }

        public bool Hidden { get; set; }

        public bool IsCapstone { get; set; }

        public string AchievementNote { get; set; }

        public double? GlobalPercentUnlocked { get; set; }

        public RarityTier Rarity { get; set; }

        public DateTime? UnlockTimeUtc { get; set; }

        public int? ProgressNum { get; set; }

        public int? ProgressDenom { get; set; }

        public bool UseSeparateLockedIconsWhenAvailable { get; set; }
    }
}
