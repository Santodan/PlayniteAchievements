using System;
using System.Collections.Generic;
using PlayniteAchievements.Models.Achievements;

namespace PlayniteAchievements.Models.ThemeIntegration
{
    internal sealed class SelectedGameRuntimeState
    {
        public static SelectedGameRuntimeState Empty { get; } = new SelectedGameRuntimeState();

        public Guid GameId { get; }
        public DateTime LastUpdatedUtc { get; }
        public bool HasAchievements { get; }
        public int AchievementCount { get; }
        public int UnlockedCount { get; }
        public int LockedCount { get; }
        public double ProgressPercentage { get; }
        public bool IsCompleted { get; }
        public bool HasCustomAchievementOrder { get; }
        public List<AchievementDetail> AchievementDefaultOrder { get; }
        public List<AchievementDetail> AllAchievements { get; }
        public List<AchievementDetail> AchievementsOldestFirst { get; }
        public List<AchievementDetail> AchievementsNewestFirst { get; }
        public List<AchievementDetail> AchievementsRarityAsc { get; }
        public List<AchievementDetail> AchievementsRarityDesc { get; }
        public AchievementRarityStats Common { get; }
        public AchievementRarityStats Uncommon { get; }
        public AchievementRarityStats Rare { get; }
        public AchievementRarityStats UltraRare { get; }
        public AchievementRarityStats RareAndUltraRare { get; }

        public SelectedGameRuntimeState()
            : this(
                Guid.Empty,
                default,
                false,
                0,
                0,
                0,
                0,
                false,
                false,
                new List<AchievementDetail>(),
                new List<AchievementDetail>(),
                new List<AchievementDetail>(),
                new List<AchievementDetail>(),
                new List<AchievementDetail>(),
                new List<AchievementDetail>(),
                new AchievementRarityStats(),
                new AchievementRarityStats(),
                new AchievementRarityStats(),
                new AchievementRarityStats(),
                new AchievementRarityStats())
        {
        }

        public SelectedGameRuntimeState(
            Guid gameId,
            DateTime lastUpdatedUtc,
            bool hasAchievements,
            int achievementCount,
            int unlockedCount,
            int lockedCount,
            double progressPercentage,
            bool isCompleted,
            bool hasCustomAchievementOrder,
            List<AchievementDetail> achievementDefaultOrder,
            List<AchievementDetail> allAchievements,
            List<AchievementDetail> achievementsOldestFirst,
            List<AchievementDetail> achievementsNewestFirst,
            List<AchievementDetail> achievementsRarityAsc,
            List<AchievementDetail> achievementsRarityDesc,
            AchievementRarityStats common,
            AchievementRarityStats uncommon,
            AchievementRarityStats rare,
            AchievementRarityStats ultraRare,
            AchievementRarityStats rareAndUltraRare)
        {
            GameId = gameId;
            LastUpdatedUtc = lastUpdatedUtc;
            HasAchievements = hasAchievements;
            AchievementCount = achievementCount;
            UnlockedCount = unlockedCount;
            LockedCount = lockedCount;
            ProgressPercentage = progressPercentage;
            IsCompleted = isCompleted;
            HasCustomAchievementOrder = hasCustomAchievementOrder;
            AchievementDefaultOrder = achievementDefaultOrder ?? new List<AchievementDetail>();
            AllAchievements = allAchievements ?? new List<AchievementDetail>();
            AchievementsOldestFirst = achievementsOldestFirst ?? new List<AchievementDetail>();
            AchievementsNewestFirst = achievementsNewestFirst ?? new List<AchievementDetail>();
            AchievementsRarityAsc = achievementsRarityAsc ?? new List<AchievementDetail>();
            AchievementsRarityDesc = achievementsRarityDesc ?? new List<AchievementDetail>();
            Common = common ?? new AchievementRarityStats();
            Uncommon = uncommon ?? new AchievementRarityStats();
            Rare = rare ?? new AchievementRarityStats();
            UltraRare = ultraRare ?? new AchievementRarityStats();
            RareAndUltraRare = rareAndUltraRare ?? new AchievementRarityStats();
        }
    }
}
