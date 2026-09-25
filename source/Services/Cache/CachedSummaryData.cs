using PlayniteAchievements.Models.Achievements;
using System;
using System.Collections.Generic;

namespace PlayniteAchievements.Services.Cache
{
    internal sealed class CachedSummaryData
    {
        public List<CachedGameSummaryData> Games { get; set; } = new List<CachedGameSummaryData>();

        public List<CachedRecentUnlockData> RecentUnlocks { get; set; } = new List<CachedRecentUnlockData>();

        // Full visible detail rows are populated by unbounded overview/widget reads.
        // Bounded theme reads keep this empty and materialize only RecentUnlocks.
        public List<CachedRecentUnlockData> Achievements { get; set; } =
            new List<CachedRecentUnlockData>();

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

        /// <summary>How many capstones the game has, and how many are earned.</summary>
        public int CapstoneTotal { get; set; }

        public int CapstoneUnlocked { get; set; }

        /// <summary>
        /// The two disagreements that decide whether the game's capstones are exactly its
        /// platinums. Kept as counts rather than the answer because a game's achievements reach
        /// this row from two places -- the summary query and the custom-achievement merge -- and
        /// counts add up where a boolean does not.
        /// </summary>
        public int CapstonesNotPlatinum { get; set; }

        public int PlatinumsNotCapstone { get; set; }

        /// <summary>
        /// Whether the game's capstones are exactly its platinum trophies, or it names none. A
        /// game with no capstones hands the finish badge to its platinum outright.
        /// </summary>
        public bool CapstonesMatchPlatinums =>
            CapstonesNotPlatinum == 0 && (CapstoneTotal == 0 || PlatinumsNotCapstone == 0);

        /// <summary>
        /// The game's platinum ApiNames, separated by <see cref="PlatinumApiNameSeparator"/>, so
        /// the capstone overlay can re-decide the identity for a game whose capstones the user has
        /// edited. Locked rows are absent from the unlock snapshot, which is why this rides along
        /// with the summary row.
        /// </summary>
        public string PlatinumApiNames { get; set; }

        /// <summary>
        /// Separates packed ApiNames. Rare enough in an ApiName that a provider's own punctuation
        /// cannot split one in half, which a comma could.
        /// </summary>
        public const string PlatinumApiNameSeparator = "~|~";

        public DateTime? LastUnlockUtc { get; set; }
    }

    internal sealed class CachedRecentUnlockData : Models.Achievements.IAchievementOverrideTarget
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

        /// <summary>
        /// The provider's category label from before a user rename replaced <see cref="Category"/>,
        /// which default category art is looked up by (as on the per-game path).
        /// </summary>
        public string ProviderCategory { get; set; }

        public string CategoryType { get; set; }

        public string TrophyType { get; set; }

        public bool Hidden { get; set; }

        public bool IsCapstone { get; set; }

        public string AchievementNote { get; set; }

        public double? GlobalPercentUnlocked { get; set; }

        public RarityTier Rarity { get; set; }

        public bool Unlocked { get; set; } = true;

        public DateTime? UnlockTimeUtc { get; set; }

        public int? ProgressNum { get; set; }

        public int? ProgressDenom { get; set; }

        public bool UseSeparateLockedIconsWhenAvailable { get; set; }
    }
}
