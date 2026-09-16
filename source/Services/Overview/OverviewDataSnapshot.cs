using System;
using System.Collections.Generic;
using PlayniteAchievements.Models.Friends;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.Services.Overview
{
    public sealed class OverviewDataSnapshot
    {
        public List<AchievementDisplayItem> Achievements { get; set; } = new List<AchievementDisplayItem>();
        public List<GameSummaryItem> GameSummaries { get; set; } = new List<GameSummaryItem>();
        public List<AchievementDisplayItem> RecentAchievements { get; set; } = new List<AchievementDisplayItem>();

        /// <summary>
        /// Bounded pool of LOCKED achievements the Unlock Next mosaic draws from, kept out of
        /// <see cref="Achievements"/> so the grid, the search indexes, and the other mosaic
        /// sources keep seeing unlocked rows only. Empty unless some live widget asks for it;
        /// <see cref="UnlockNextPoolBuilt"/> distinguishes "not requested" from "nothing found".
        /// </summary>
        public List<AchievementDisplayItem> UnlockNextCandidates { get; set; } =
            new List<AchievementDisplayItem>();

        /// <summary>
        /// Whether this snapshot was built with the Unlock Next pool populated. A dashboard that
        /// starts needing the pool triggers a rebuild off this flag.
        /// </summary>
        public bool UnlockNextPoolBuilt { get; set; }
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

        /// <summary>
        /// How many finishes the library holds, which is not the same as how many games are
        /// finished: a game with several capstones contributes one per capstone earned.
        /// </summary>
        public int Completions { get; set; }

        /// <summary>
        /// Every finish the library offers, earned or not, which is what the completions pie
        /// partitions.
        /// </summary>
        public int PossibleCompletions { get; set; }
        public double GlobalProgressionPercent { get; set; }
        public int CollectorScore { get; set; }
        public int CollectorLevel { get; set; }
        public double CollectorLevelProgress { get; set; }
        public string CollectorRank { get; set; } = "Bronze5";
        public int PrestigeScore { get; set; }
        public int PrestigeLevel { get; set; }
        public double PrestigeLevelProgress { get; set; }
        public string PrestigeRank { get; set; } = "Bronze5";

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

        /// <summary>
        /// Current-user identities persisted by the friends providers (empty when never scanned).
        /// </summary>
        public List<FriendIdentity> CurrentUserIdentities { get; set; } = new List<FriendIdentity>();

        // Library-wide trophy grade counts, summed from the game summaries. Only the
        // PlayStation-shaped providers populate a trophy type, so these are all zero for a
        // library without them. Set through ApplyTrophyTotals so every snapshot builder sums
        // them the same way.
        public int TotalPlatinum { get; set; }
        public int TotalGold { get; set; }
        public int TotalSilver { get; set; }
        public int TotalBronze { get; set; }
        public int TotalPlatinumPossible { get; set; }
        public int TotalGoldPossible { get; set; }
        public int TotalSilverPossible { get; set; }
        public int TotalBronzePossible { get; set; }

        /// <summary>
        /// Sums the eight trophy totals from the given summaries in one pass. Call once per
        /// snapshot build; there are three builders and they must not each carry their own copy
        /// of these sums.
        /// </summary>
        public void ApplyTrophyTotals(IEnumerable<GameSummaryItem> games)
        {
            TotalPlatinum = 0;
            TotalGold = 0;
            TotalSilver = 0;
            TotalBronze = 0;
            TotalPlatinumPossible = 0;
            TotalGoldPossible = 0;
            TotalSilverPossible = 0;
            TotalBronzePossible = 0;
            if (games == null)
            {
                return;
            }

            foreach (var game in games)
            {
                if (game == null)
                {
                    continue;
                }

                TotalPlatinum += game.TrophyPlatinumCount;
                TotalGold += game.TrophyGoldCount;
                TotalSilver += game.TrophySilverCount;
                TotalBronze += game.TrophyBronzeCount;
                TotalPlatinumPossible += game.TrophyPlatinumTotal;
                TotalGoldPossible += game.TrophyGoldTotal;
                TotalSilverPossible += game.TrophySilverTotal;
                TotalBronzePossible += game.TrophyBronzeTotal;
            }
        }

        // Total rarity counts (including locked achievements) for "unlocked / total" display
        public int TotalCommonPossible { get; set; }
        public int TotalUncommonPossible { get; set; }
        public int TotalRarePossible { get; set; }
        public int TotalUltraRarePossible { get; set; }
    }
}
