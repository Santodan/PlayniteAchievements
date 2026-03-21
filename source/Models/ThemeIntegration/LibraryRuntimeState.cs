using System.Collections.Generic;
using PlayniteAchievements.Models.Achievements;

namespace PlayniteAchievements.Models.ThemeIntegration
{
    internal sealed class LibraryRuntimeState
    {
        public bool HasData => TotalTrophies > 0;
        public bool HeavyListsBuilt { get; set; } = true;

        public List<GameAchievementSummary> AllGamesWithAchievements { get; set; } = new List<GameAchievementSummary>();
        public List<GameAchievementSummary> CompletedGamesAsc { get; set; } = new List<GameAchievementSummary>();
        public List<GameAchievementSummary> CompletedGamesDesc { get; set; } = new List<GameAchievementSummary>();
        public List<GameAchievementSummary> GameSummariesAsc { get; set; } = new List<GameAchievementSummary>();
        public List<GameAchievementSummary> GameSummariesDesc { get; set; } = new List<GameAchievementSummary>();
        public List<GameAchievementSummary> PlatinumGames { get; set; } = new List<GameAchievementSummary>();
        public List<GameAchievementSummary> PlatinumGamesAscending { get; set; } = new List<GameAchievementSummary>();

        public List<GameAchievementSummary> SteamGames { get; set; } = new List<GameAchievementSummary>();
        public List<GameAchievementSummary> GOGGames { get; set; } = new List<GameAchievementSummary>();
        public List<GameAchievementSummary> EpicGames { get; set; } = new List<GameAchievementSummary>();
        public List<GameAchievementSummary> XboxGames { get; set; } = new List<GameAchievementSummary>();
        public List<GameAchievementSummary> PSNGames { get; set; } = new List<GameAchievementSummary>();
        public List<GameAchievementSummary> RetroAchievementsGames { get; set; } = new List<GameAchievementSummary>();
        public List<GameAchievementSummary> RPCS3Games { get; set; } = new List<GameAchievementSummary>();
        public List<GameAchievementSummary> XeniaGames { get; set; } = new List<GameAchievementSummary>();
        public List<GameAchievementSummary> ShadPS4Games { get; set; } = new List<GameAchievementSummary>();
        public List<GameAchievementSummary> ManualGames { get; set; } = new List<GameAchievementSummary>();

        public int TotalTrophies { get; set; }
        public int PlatinumTrophies { get; set; }
        public int GoldTrophies { get; set; }
        public int SilverTrophies { get; set; }
        public int BronzeTrophies { get; set; }
        public AchievementRarityStats TotalCommon { get; set; } = new AchievementRarityStats();
        public AchievementRarityStats TotalUncommon { get; set; } = new AchievementRarityStats();
        public AchievementRarityStats TotalRare { get; set; } = new AchievementRarityStats();
        public AchievementRarityStats TotalUltraRare { get; set; } = new AchievementRarityStats();
        public AchievementRarityStats TotalRareAndUltraRare { get; set; } = new AchievementRarityStats();
        public AchievementRarityStats TotalOverall { get; set; } = new AchievementRarityStats();
        public int Score { get; set; }
        public int Level { get; set; }
        public double LevelProgress { get; set; }
        public string Rank { get; set; } = "Bronze1";

        public List<AchievementDetail> AllAchievementsUnlockAsc { get; set; } = new List<AchievementDetail>();
        public List<AchievementDetail> AllAchievementsUnlockDesc { get; set; } = new List<AchievementDetail>();
        public List<AchievementDetail> AllAchievementsRarityAsc { get; set; } = new List<AchievementDetail>();
        public List<AchievementDetail> AllAchievementsRarityDesc { get; set; } = new List<AchievementDetail>();
        public List<AchievementDetail> MostRecentUnlocks { get; set; } = new List<AchievementDetail>();
        public List<AchievementDetail> RarestRecentUnlocks { get; set; } = new List<AchievementDetail>();
        public List<AchievementDetail> MostRecentUnlocksTop3 { get; set; } = new List<AchievementDetail>();
        public List<AchievementDetail> MostRecentUnlocksTop5 { get; set; } = new List<AchievementDetail>();
        public List<AchievementDetail> MostRecentUnlocksTop10 { get; set; } = new List<AchievementDetail>();
        public List<AchievementDetail> RarestRecentUnlocksTop3 { get; set; } = new List<AchievementDetail>();
        public List<AchievementDetail> RarestRecentUnlocksTop5 { get; set; } = new List<AchievementDetail>();
        public List<AchievementDetail> RarestRecentUnlocksTop10 { get; set; } = new List<AchievementDetail>();
    }
}
