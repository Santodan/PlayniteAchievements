using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.ViewModels;
using Playnite.SDK;

namespace PlayniteAchievements.Services.Sidebar
{
    public sealed class SidebarDataBuilder
    {
        private readonly AchievementService _achievementService;
        private readonly IPlayniteAPI _playniteApi;
        private readonly ILogger _logger;

        public SidebarDataBuilder(AchievementService achievementService, IPlayniteAPI playniteApi, ILogger logger)
        {
            _achievementService = achievementService ?? throw new ArgumentNullException(nameof(achievementService));
            _playniteApi = playniteApi;
            _logger = logger;
        }

        public SidebarDataSnapshot Build(
            PlayniteAchievementsSettings settings,
            ISet<string> revealedKeys,
            CancellationToken cancel)
        {
            settings ??= new PlayniteAchievementsSettings();
            revealedKeys ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var snapshot = new SidebarDataSnapshot();

            var allAchievements = new List<AchievementDisplayItem>();
            var gamesOverview = new List<GameOverviewItem>();
            var recentAchievements = new List<RecentAchievementItem>();

            var globalCounts = new Dictionary<DateTime, int>();
            var singleGameCounts = new Dictionary<Guid, Dictionary<DateTime, int>>();

            int totalAchievements = 0;
            int totalUnlocked = 0;
            int commonCount = 0;
            int uncommonCount = 0;
            int rareCount = 0;
            int ultraRareCount = 0;
            int completedGames = 0;

            var unlockedByProvider = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var totalByProvider = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            // Total rarity counts (including locked achievements)
            int totalCommonPossible = 0;
            int totalUncommonPossible = 0;
            int totalRarePossible = 0;
            int totalUltraRarePossible = 0;

            var providerLookup = BuildProviderLookup();
            var allGameData = _achievementService.GetAllGameAchievementData() ?? new List<GameAchievementData>();
            for (var i = 0; i < allGameData.Count; i++)
            {
                cancel.ThrowIfCancellationRequested();

                var fragment = BuildGameFragment(settings, revealedKeys, allGameData[i], providerLookup);
                if (fragment == null)
                {
                    continue;
                }

                allAchievements.AddRange(fragment.Achievements);

                if (fragment.GameOverview != null)
                {
                    gamesOverview.Add(fragment.GameOverview);
                }

                if (fragment.RecentAchievements != null && fragment.RecentAchievements.Count > 0)
                {
                    recentAchievements.AddRange(fragment.RecentAchievements);
                }

                if (fragment.PlayniteGameId.HasValue)
                {
                    singleGameCounts[fragment.PlayniteGameId.Value] = fragment.UnlockCountsByDate;
                }

                foreach (var kvp in fragment.UnlockCountsByDate)
                {
                    IncrementBy(globalCounts, kvp.Key, kvp.Value);
                }

                totalAchievements += fragment.TotalAchievements;
                totalUnlocked += fragment.UnlockedAchievements;
                commonCount += fragment.CommonCount;
                uncommonCount += fragment.UncommonCount;
                rareCount += fragment.RareCount;
                ultraRareCount += fragment.UltraRareCount;
                if (fragment.IsCompleted)
                {
                    completedGames++;
                }

                var provider = allGameData[i].ProviderKey ?? "Unknown";
                if (!unlockedByProvider.ContainsKey(provider))
                {
                    unlockedByProvider[provider] = 0;
                }

                if (!totalByProvider.ContainsKey(provider))
                {
                    totalByProvider[provider] = 0;
                }

                unlockedByProvider[provider] += fragment.UnlockedAchievements;
                totalByProvider[provider] += fragment.TotalAchievements;

                // Aggregate total rarity counts
                totalCommonPossible += fragment.TotalCommonPossible;
                totalUncommonPossible += fragment.TotalUncommonPossible;
                totalRarePossible += fragment.TotalRarePossible;
                totalUltraRarePossible += fragment.TotalUltraRarePossible;
            }

            // Sort stable outputs once so UI work is minimal.
            gamesOverview = gamesOverview
                .OrderByDescending(g => g.LastPlayed ?? DateTime.MinValue)
                .ToList();

            recentAchievements = recentAchievements
                .OrderByDescending(a => a.UnlockTime)
                .ToList();

            snapshot.Achievements = allAchievements;
            snapshot.GamesOverview = gamesOverview;
            snapshot.RecentAchievements = recentAchievements;
            snapshot.GlobalUnlockCountsByDate = globalCounts;
            snapshot.UnlockCountsByDateByGame = singleGameCounts;

            snapshot.TotalGames = gamesOverview.Count;
            snapshot.TotalAchievements = totalAchievements;
            snapshot.TotalUnlocked = totalUnlocked;
            snapshot.TotalCommon = commonCount;
            snapshot.TotalUncommon = uncommonCount;
            snapshot.TotalRare = rareCount;
            snapshot.TotalUltraRare = ultraRareCount;
            snapshot.CompletedGames = completedGames;
            snapshot.GlobalProgressionPercent = totalAchievements > 0 ? (double)totalUnlocked / totalAchievements * 100 : 0;

            snapshot.TotalLocked = totalAchievements - totalUnlocked;
            snapshot.UnlockedByProvider = unlockedByProvider;
            snapshot.TotalByProvider = totalByProvider;

            snapshot.TotalCommonPossible = totalCommonPossible;
            snapshot.TotalUncommonPossible = totalUncommonPossible;
            snapshot.TotalRarePossible = totalRarePossible;
            snapshot.TotalUltraRarePossible = totalUltraRarePossible;

            return snapshot;
        }

        public SidebarGameFragment BuildGameFragment(
            PlayniteAchievementsSettings settings,
            ISet<string> revealedKeys,
            GameAchievementData gameData,
            IReadOnlyDictionary<string, (string iconKey, string colorHex)> providerLookup = null)
        {
            settings ??= new PlayniteAchievementsSettings();
            revealedKeys ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (gameData?.PlayniteGameId.HasValue == true &&
                settings?.Persisted?.ExcludedFromSummariesGameIds?.Contains(gameData.PlayniteGameId.Value) == true)
            {
                return null;
            }

            if (gameData?.Achievements == null || !gameData.HasAchievements || gameData.Achievements.Count == 0)
            {
                return null;
            }

            providerLookup ??= BuildProviderLookup();
            var providerKey = gameData.ProviderKey ?? "Unknown";
            providerLookup.TryGetValue(providerKey, out var providerMetadata);

            var playniteGame = gameData.PlayniteGameId.HasValue
                ? _playniteApi?.Database?.Games?.Get(gameData.PlayniteGameId.Value)
                : null;
            var gameIconPath = !string.IsNullOrEmpty(playniteGame?.Icon)
                ? _playniteApi.Database.GetFullFilePath(playniteGame.Icon)
                : null;
            var gameCoverPath = !string.IsNullOrEmpty(playniteGame?.CoverImage)
                ? _playniteApi.Database.GetFullFilePath(playniteGame.CoverImage)
                : null;

            var fragment = new SidebarGameFragment
            {
                CacheKey = gameData.PlayniteGameId?.ToString(),
                PlayniteGameId = gameData.PlayniteGameId,
                ProviderKey = providerKey,
                ProviderName = gameData.ProviderDisplayName
            };

            var projectionOptions = AchievementProjectionService.CreateOptions(settings, gameData, revealedKeys);

            var achievements = gameData.Achievements;
            int gameTotal = achievements.Count;
            int gameUnlocked = 0;
            int gameCommon = 0;
            int gameUncommon = 0;
            int gameRare = 0;
            int gameUltraRare = 0;

            // Total rarity counts (including locked achievements)
            int gameTotalCommon = 0;
            int gameTotalUncommon = 0;
            int gameTotalRare = 0;
            int gameTotalUltraRare = 0;

            // Trophy counts (for PlayStation games)
            int gameTrophyPlatinum = 0;
            int gameTrophyGold = 0;
            int gameTrophySilver = 0;
            int gameTrophyBronze = 0;
            int gameTrophyPlatinumTotal = 0;
            int gameTrophyGoldTotal = 0;
            int gameTrophySilverTotal = 0;
            int gameTrophyBronzeTotal = 0;

            for (var i = 0; i < achievements.Count; i++)
            {
                var ach = achievements[i];
                if (ach == null)
                {
                    continue;
                }

                var displayItem = AchievementProjectionService.CreateDisplayItem(gameData, ach, projectionOptions);
                if (displayItem != null)
                {
                    fragment.Achievements.Add(displayItem);
                }

                // Calculate total rarity tier for ALL achievements (including locked)
                // Only count if rarity data is available (null means no rarity info for this provider)
                AchievementProjectionService.AccumulateRarity(ach, ref gameTotalCommon, ref gameTotalUncommon, ref gameTotalRare, ref gameTotalUltraRare);
                AchievementProjectionService.AccumulateTrophy(
                    ach,
                    ref gameTrophyPlatinumTotal,
                    ref gameTrophyGoldTotal,
                    ref gameTrophySilverTotal,
                    ref gameTrophyBronzeTotal);

                if (ach.Unlocked)
                {
                    gameUnlocked++;

                    // Only count rarity if data is available
                    AchievementProjectionService.AccumulateRarity(ach, ref gameCommon, ref gameUncommon, ref gameRare, ref gameUltraRare);

                    // Track trophy types for unlocked achievements
                    AchievementProjectionService.AccumulateTrophy(ach, ref gameTrophyPlatinum, ref gameTrophyGold, ref gameTrophySilver, ref gameTrophyBronze);

                    if (ach.UnlockTimeUtc.HasValue)
                    {
                        var unlockDate = DateTimeUtilities.AsUtcKind(ach.UnlockTimeUtc.Value).Date;
                        Increment(fragment.UnlockCountsByDate, unlockDate);

                        if (gameData.PlayniteGameId.HasValue)
                        {
                            var recentItem = AchievementProjectionService.CreateRecentItem(
                                gameData,
                                ach,
                                projectionOptions,
                                gameIconPath,
                                gameCoverPath);
                            if (recentItem != null)
                            {
                                fragment.RecentAchievements.Add(recentItem);
                            }
                        }
                    }
                }
            }

            fragment.TotalAchievements = gameTotal;
            fragment.UnlockedAchievements = gameUnlocked;
            fragment.CommonCount = gameCommon;
            fragment.UncommonCount = gameUncommon;
            fragment.RareCount = gameRare;
            fragment.UltraRareCount = gameUltraRare;

            fragment.TrophyPlatinumCount = gameTrophyPlatinum;
            fragment.TrophyGoldCount = gameTrophyGold;
            fragment.TrophySilverCount = gameTrophySilver;
            fragment.TrophyBronzeCount = gameTrophyBronze;
            fragment.TrophyPlatinumTotal = gameTrophyPlatinumTotal;
            fragment.TrophyGoldTotal = gameTrophyGoldTotal;
            fragment.TrophySilverTotal = gameTrophySilverTotal;
            fragment.TrophyBronzeTotal = gameTrophyBronzeTotal;

            fragment.TotalCommonPossible = gameTotalCommon;
            fragment.TotalUncommonPossible = gameTotalUncommon;
            fragment.TotalRarePossible = gameTotalRare;
            fragment.TotalUltraRarePossible = gameTotalUltraRare;

            fragment.IsCompleted = gameData.IsCompleted;

            fragment.GameOverview = new GameOverviewItem
            {
                GameName = gameData.GameName ?? "Unknown",
                SortingName = playniteGame?.SortingName ?? gameData.GameName ?? "Unknown",
                GameLogo = gameIconPath,
                GameCoverPath = gameCoverPath,
                AppId = gameData.AppId,
                PlayniteGameId = gameData.PlayniteGameId,
                TotalAchievements = gameTotal,
                UnlockedAchievements = gameUnlocked,
                CommonCount = gameCommon,
                UncommonCount = gameUncommon,
                RareCount = gameRare,
                UltraRareCount = gameUltraRare,
                TotalCommonPossible = gameTotalCommon,
                TotalUncommonPossible = gameTotalUncommon,
                TotalRarePossible = gameTotalRare,
                TotalUltraRarePossible = gameTotalUltraRare,
                TrophyPlatinumCount = gameTrophyPlatinum,
                TrophyGoldCount = gameTrophyGold,
                TrophySilverCount = gameTrophySilver,
                TrophyBronzeCount = gameTrophyBronze,
                TrophyPlatinumTotal = gameTrophyPlatinumTotal,
                TrophyGoldTotal = gameTrophyGoldTotal,
                TrophySilverTotal = gameTrophySilverTotal,
                TrophyBronzeTotal = gameTrophyBronzeTotal,
                LastPlayed = playniteGame?.LastActivity,
                IsCompleted = gameData.IsCompleted,
                Provider = gameData.ProviderDisplayName,
                ProviderIconKey = providerMetadata.iconKey,
                ProviderColorHex = providerMetadata.colorHex
            };

            return fragment;
        }

        private Dictionary<string, (string iconKey, string colorHex)> BuildProviderLookup()
        {
            var lookup = new Dictionary<string, (string iconKey, string colorHex)>(StringComparer.OrdinalIgnoreCase);
            var providers = _achievementService?.GetProviders();
            if (providers == null)
            {
                return lookup;
            }

            foreach (var provider in providers)
            {
                if (provider == null || string.IsNullOrWhiteSpace(provider.ProviderKey))
                {
                    continue;
                }

                lookup[provider.ProviderKey] = (provider.ProviderIconKey, provider.ProviderColorHex);
            }

            return lookup;
        }

        private static void Increment(Dictionary<DateTime, int> dict, DateTime date)
        {
            if (dict.TryGetValue(date, out var existing))
            {
                dict[date] = existing + 1;
            }
            else
            {
                dict[date] = 1;
            }
        }

        private static void IncrementBy(Dictionary<DateTime, int> dict, DateTime date, int count)
        {
            if (dict.TryGetValue(date, out var existing))
            {
                dict[date] = existing + count;
            }
            else
            {
                dict[date] = count;
            }
        }
    }
}



