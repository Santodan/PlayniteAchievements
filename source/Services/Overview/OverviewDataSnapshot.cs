using System;
using System.Collections.Generic;
using PlayniteAchievements.Models.Friends;
using PlayniteAchievements.Models.Settings;
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

        /// <summary>
        /// Every achievement pin (see <see cref="AchievementPinKey"/>) the build considered when it
        /// hydrated locked pinned rows into <see cref="Achievements"/>. A pin added afterwards is
        /// absent here, so its locked row was never hydrated; consumers rebuild off
        /// <see cref="HasSeenAchievementPins"/>. A pin whose game data is unavailable stays in
        /// the set, so it does not trigger a rebuild on every configuration change.
        /// </summary>
        public HashSet<string> AchievementPinKeysAtBuild { get; set; } =
            new HashSet<string>(StringComparer.Ordinal);

        public static string AchievementPinKey(Guid gameId, string apiName) =>
            gameId.ToString("N") + "|" + (apiName ?? string.Empty).ToUpperInvariant();

        /// <summary>Whether this snapshot's build already accounted for every current achievement pin.</summary>
        public bool HasSeenAchievementPins(ShowcaseSettings showcase)
        {
            var collections = showcase?.AchievementPinCollections;
            if (collections == null)
            {
                return true;
            }

            foreach (var collection in collections)
            {
                if (collection?.Pins == null)
                {
                    continue;
                }

                foreach (var pin in collection.Pins)
                {
                    if (pin == null || pin.GameId == Guid.Empty || string.IsNullOrWhiteSpace(pin.ApiName))
                    {
                        continue;
                    }

                    if (AchievementPinKeysAtBuild == null ||
                        !AchievementPinKeysAtBuild.Contains(AchievementPinKey(pin.GameId, pin.ApiName)))
                    {
                        return false;
                    }
                }
            }

            return true;
        }
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

        /// <summary>
        /// Applies every total derived from the game summaries in one pass: counts, rarity,
        /// completions, trophies, the two per-provider maps, and the raw collector and prestige
        /// scores.
        /// </summary>
        /// <remarks>
        /// The two snapshot builders used to compute these as roughly twenty separate LINQ
        /// <c>Sum</c>/<c>Count</c> calls plus a loop, so each build walked the library about
        /// twenty times over. The overview rebuilds a snapshot on every delta tick, and the pie
        /// charts build two more, so one custom-data edit paid that many library walks. The
        /// arithmetic is unchanged -- only the number of passes is.
        ///
        /// <paramref name="addClamped"/> is how the caller adds the two scores; both saturate
        /// rather than overflow.
        /// </remarks>
        public void ApplyGameSummaryTotals(
            IReadOnlyList<GameSummaryItem> games,
            Func<int, int, int> addClamped)
        {
            TotalGames = games?.Count ?? 0;
            TotalAchievements = 0;
            TotalUnlocked = 0;
            TotalCommon = 0;
            TotalUncommon = 0;
            TotalRare = 0;
            TotalUltraRare = 0;
            TotalCommonPossible = 0;
            TotalUncommonPossible = 0;
            TotalRarePossible = 0;
            TotalUltraRarePossible = 0;
            CompletedGames = 0;
            Completions = 0;
            PossibleCompletions = 0;
            CollectorScore = 0;
            PrestigeScore = 0;
            TotalPlatinum = 0;
            TotalGold = 0;
            TotalSilver = 0;
            TotalBronze = 0;
            TotalPlatinumPossible = 0;
            TotalGoldPossible = 0;
            TotalSilverPossible = 0;
            TotalBronzePossible = 0;

            if (UnlockedByProvider == null)
            {
                UnlockedByProvider = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            }

            if (TotalByProvider == null)
            {
                TotalByProvider = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            }

            if (games == null)
            {
                return;
            }

            for (var i = 0; i < games.Count; i++)
            {
                var game = games[i];
                if (game == null)
                {
                    continue;
                }

                TotalAchievements += game.TotalAchievements;
                TotalUnlocked += game.UnlockedAchievements;
                TotalCommon += game.CommonCount;
                TotalUncommon += game.UncommonCount;
                TotalRare += game.RareCount;
                TotalUltraRare += game.UltraRareCount;
                TotalCommonPossible += game.TotalCommonPossible;
                TotalUncommonPossible += game.TotalUncommonPossible;
                TotalRarePossible += game.TotalRarePossible;
                TotalUltraRarePossible += game.TotalUltraRarePossible;

                if (game.IsCompleted)
                {
                    CompletedGames++;
                }

                Completions += game.Completions;
                PossibleCompletions += game.PossibleCompletions;

                TotalPlatinum += game.TrophyPlatinumCount;
                TotalGold += game.TrophyGoldCount;
                TotalSilver += game.TrophySilverCount;
                TotalBronze += game.TrophyBronzeCount;
                TotalPlatinumPossible += game.TrophyPlatinumTotal;
                TotalGoldPossible += game.TrophyGoldTotal;
                TotalSilverPossible += game.TrophySilverTotal;
                TotalBronzePossible += game.TrophyBronzeTotal;

                var provider = string.IsNullOrWhiteSpace(game.ProviderKey) ? "Unknown" : game.ProviderKey;
                UnlockedByProvider.TryGetValue(provider, out var providerUnlocked);
                UnlockedByProvider[provider] = providerUnlocked + game.UnlockedAchievements;
                TotalByProvider.TryGetValue(provider, out var providerTotal);
                TotalByProvider[provider] = providerTotal + game.TotalAchievements;

                if (addClamped != null)
                {
                    CollectorScore = addClamped(CollectorScore, game.CollectionScore);
                    PrestigeScore = addClamped(PrestigeScore, game.PrestigeScore);
                }
            }

            TotalLocked = Math.Max(0, TotalAchievements - TotalUnlocked);
            GlobalProgressionPercent = TotalAchievements > 0
                ? (double)TotalUnlocked / TotalAchievements * 100
                : 0;
        }

        // Total rarity counts (including locked achievements) for "unlocked / total" display
        public int TotalCommonPossible { get; set; }
        public int TotalUncommonPossible { get; set; }
        public int TotalRarePossible { get; set; }
        public int TotalUltraRarePossible { get; set; }
    }
}
