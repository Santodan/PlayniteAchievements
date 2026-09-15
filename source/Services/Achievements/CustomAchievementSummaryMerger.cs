using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Achievements.Scoring;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Cache;
using PlayniteAchievements.Services.Images;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Services.Achievements
{
    /// <summary>
    /// Folds custom achievement projections into a SQL-backed overview summary. Custom
    /// achievements live only in custom data, so the summary queries never see them; this
    /// adds their counts, scores, recent unlocks, and timeline entries the same way the
    /// reader derives those from stored rows, keeping the summary path the single builder.
    /// </summary>
    internal static class CustomAchievementSummaryMerger
    {
        public static void Merge(
            CachedSummaryData summaryData,
            IReadOnlyDictionary<Guid, GameCustomDataFile> customDataByGameId,
            ISet<Guid> excludedSummaryIds,
            int recentAchievementDetailLimit,
            Func<Guid, string> resolveGameName,
            ManagedCustomIconService managedCustomIconService,
            Func<string, string> resolveCustomProviderKey = null)
        {
            if (summaryData == null || customDataByGameId == null || customDataByGameId.Count == 0)
            {
                return;
            }

            summaryData.Games ??= new List<CachedGameSummaryData>();
            summaryData.RecentUnlocks ??= new List<CachedRecentUnlockData>();
            summaryData.Achievements ??= new List<CachedRecentUnlockData>();
            summaryData.GlobalUnlockCountsByDate ??= new Dictionary<DateTime, int>();
            summaryData.UnlockCountsByDateByGame ??= new Dictionary<Guid, Dictionary<DateTime, int>>();

            var gamesByGameId = new Dictionary<Guid, CachedGameSummaryData>();
            foreach (var game in summaryData.Games)
            {
                if (game?.PlayniteGameId.HasValue == true && !gamesByGameId.ContainsKey(game.PlayniteGameId.Value))
                {
                    gamesByGameId[game.PlayniteGameId.Value] = game;
                }
            }

            var addedRecent = false;
            foreach (var pair in customDataByGameId)
            {
                var gameId = pair.Key;
                var customData = pair.Value;
                if (gameId == Guid.Empty ||
                    customData == null ||
                    excludedSummaryIds?.Contains(gameId) == true ||
                    !CustomAchievementProjectionService.HasCustomAchievements(customData))
                {
                    continue;
                }

                var hiddenFromSummary = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                hiddenFromSummary.UnionWith(customData.FilteredAchievementApiNames ?? Enumerable.Empty<string>());
                hiddenFromSummary.UnionWith(customData.SummaryFilteredAchievementApiNames ?? Enumerable.Empty<string>());

                var visible = CustomAchievementProjectionService
                    .ProjectAchievements(gameId, customData.CustomAchievements, managedCustomIconService)
                    .Where(achievement => achievement != null && !hiddenFromSummary.Contains(achievement.ApiName ?? string.Empty))
                    .ToList();
                if (visible.Count == 0)
                {
                    continue;
                }

                if (!gamesByGameId.TryGetValue(gameId, out var game))
                {
                    game = new CachedGameSummaryData
                    {
                        CacheKey = gameId.ToString("D"),
                        PlayniteGameId = gameId,
                        ProviderKey = CustomAchievementProjectionService.ProviderKey,
                        // Custom-only games display as their assigned custom provider when the id
                        // still resolves; the summary row mirrors the synthetic game data.
                        ProviderPlatformKey = string.IsNullOrWhiteSpace(customData.CustomProviderId)
                            ? null
                            : resolveCustomProviderKey?.Invoke(customData.CustomProviderId),
                        GameName = resolveGameName?.Invoke(gameId),
                        LastUpdatedUtc = DateTime.UtcNow
                    };
                    summaryData.Games.Add(game);
                    gamesByGameId[gameId] = game;
                }

                var platinumApiNames = new List<string>();
                foreach (var achievement in visible)
                {
                    Accumulate(game, achievement, platinumApiNames);
                    if (!achievement.Unlocked)
                    {
                        continue;
                    }

                    var unlockDate = achievement.UnlockTimeUtc?.Date;
                    if (unlockDate.HasValue)
                    {
                        Increment(summaryData.GlobalUnlockCountsByDate, unlockDate.Value);
                        if (!summaryData.UnlockCountsByDateByGame.TryGetValue(gameId, out var gameCounts))
                        {
                            gameCounts = new Dictionary<DateTime, int>();
                            summaryData.UnlockCountsByDateByGame[gameId] = gameCounts;
                        }

                        Increment(gameCounts, unlockDate.Value);
                    }

                    var recentUnlock = CreateRecentUnlock(game, achievement);
                    summaryData.RecentUnlocks.Add(recentUnlock);
                    if (recentAchievementDetailLimit == 0)
                    {
                        // Unbounded reads fill Achievements with every unlocked row and derive
                        // RecentUnlocks from it, so append to both lists to keep that contract.
                        summaryData.Achievements.Add(recentUnlock);
                    }

                    addedRecent = true;
                }

                AppendPlatinumApiNames(game, platinumApiNames);

                game.HasAchievements = true;
                // Finishing takes every capstone, not any one of them, and the custom ones the
                // query never saw are now part of that count. Recomputed rather than OR-ed into
                // what the query decided, since a custom capstone can leave a game unfinished
                // that its provider achievements alone had finished.
                game.IsCompleted =
                    (game.TotalAchievements > 0 && game.UnlockedAchievements >= game.TotalAchievements) ||
                    (game.CapstoneTotal > 0 && game.CapstoneUnlocked >= game.CapstoneTotal);
            }

            if (!addedRecent)
            {
                return;
            }

            // The reader returns recent unlocks newest first and trims to the requested limit;
            // re-apply that after appending so the merged list keeps the same contract.
            summaryData.RecentUnlocks = summaryData.RecentUnlocks
                .OrderByDescending(recent => recent?.UnlockTimeUtc ?? DateTime.MinValue)
                .ToList();
            if (recentAchievementDetailLimit == 0)
            {
                summaryData.Achievements = summaryData.Achievements
                    .OrderByDescending(item => item?.UnlockTimeUtc ?? DateTime.MinValue)
                    .ToList();
            }
            if (recentAchievementDetailLimit > 0 && summaryData.RecentUnlocks.Count > recentAchievementDetailLimit)
            {
                summaryData.HasMoreRecentUnlocks = true;
                summaryData.RecentUnlocks = summaryData.RecentUnlocks.Take(recentAchievementDetailLimit).ToList();
            }
        }

        /// <summary>
        /// Packs the custom platinums onto the row beside the ones the query found, so the stored
        /// capstone overlay sees every platinum the game has rather than only its provider ones.
        /// </summary>
        private static void AppendPlatinumApiNames(CachedGameSummaryData game, List<string> apiNames)
        {
            if (apiNames.Count == 0)
            {
                return;
            }

            var packed = string.Join(CachedGameSummaryData.PlatinumApiNameSeparator, apiNames);
            game.PlatinumApiNames = string.IsNullOrEmpty(game.PlatinumApiNames)
                ? packed
                : game.PlatinumApiNames + CachedGameSummaryData.PlatinumApiNameSeparator + packed;
        }

        private static bool IsPlatinum(AchievementDetail achievement)
        {
            return string.Equals(
                (achievement.TrophyType ?? string.Empty).Trim(),
                "platinum",
                StringComparison.OrdinalIgnoreCase);
        }

        private static void Accumulate(
            CachedGameSummaryData game,
            AchievementDetail achievement,
            List<string> platinumApiNames)
        {
            game.TotalAchievements++;

            // The capstone counts and the platinum identity are carried the same way the summary
            // query carries them for stored achievements, so a custom capstone counts toward the
            // finish badge and a custom platinum can stand in for it.
            var isPlatinum = IsPlatinum(achievement);
            if (isPlatinum && !string.IsNullOrWhiteSpace(achievement.ApiName))
            {
                platinumApiNames.Add(achievement.ApiName.Trim());
            }

            if (achievement.IsCapstone)
            {
                game.CapstoneTotal++;
                if (achievement.Unlocked)
                {
                    game.CapstoneUnlocked++;
                }

                if (!isPlatinum)
                {
                    game.CapstonesNotPlatinum++;
                }
            }
            else if (isPlatinum)
            {
                game.PlatinumsNotCapstone++;
            }

            game.CollectionScoreTotal = AddClamped(game.CollectionScoreTotal, AchievementScoreCalculator.GetCollectionValue(achievement.Rarity));
            game.PrestigeScoreTotal = AddClamped(game.PrestigeScoreTotal, AchievementScoreCalculator.GetPrestigeValue(achievement.GlobalPercentUnlocked, achievement.Rarity));
            AddRarity(game, achievement.Rarity, possible: true);
            AddTrophy(game, achievement.TrophyType, possible: true);

            if (!achievement.Unlocked)
            {
                return;
            }

            game.UnlockedAchievements++;
            game.CollectionScore = AddClamped(game.CollectionScore, AchievementScoreCalculator.GetCollectionValue(achievement.Rarity));
            game.PrestigeScore = AddClamped(game.PrestigeScore, AchievementScoreCalculator.GetPrestigeValue(achievement.GlobalPercentUnlocked, achievement.Rarity));
            game.Points = AddClamped(game.Points, achievement.Points ?? 0);
            AddRarity(game, achievement.Rarity, possible: false);
            AddTrophy(game, achievement.TrophyType, possible: false);
            if (achievement.UnlockTimeUtc.HasValue &&
                (!game.LastUnlockUtc.HasValue || achievement.UnlockTimeUtc.Value > game.LastUnlockUtc.Value))
            {
                game.LastUnlockUtc = achievement.UnlockTimeUtc;
            }
        }

        private static void AddRarity(CachedGameSummaryData game, RarityTier rarity, bool possible)
        {
            switch (rarity)
            {
                case RarityTier.UltraRare:
                    if (possible) game.TotalUltraRarePossible++; else game.UltraRareCount++;
                    break;
                case RarityTier.Rare:
                    if (possible) game.TotalRarePossible++; else game.RareCount++;
                    break;
                case RarityTier.Uncommon:
                    if (possible) game.TotalUncommonPossible++; else game.UncommonCount++;
                    break;
                default:
                    if (possible) game.TotalCommonPossible++; else game.CommonCount++;
                    break;
            }
        }

        private static void AddTrophy(CachedGameSummaryData game, string trophyType, bool possible)
        {
            switch ((trophyType ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "platinum":
                    if (possible) game.TrophyPlatinumTotal++; else game.TrophyPlatinumCount++;
                    break;
                case "gold":
                    if (possible) game.TrophyGoldTotal++; else game.TrophyGoldCount++;
                    break;
                case "silver":
                    if (possible) game.TrophySilverTotal++; else game.TrophySilverCount++;
                    break;
                case "bronze":
                    if (possible) game.TrophyBronzeTotal++; else game.TrophyBronzeCount++;
                    break;
            }
        }

        private static CachedRecentUnlockData CreateRecentUnlock(CachedGameSummaryData game, AchievementDetail achievement)
        {
            return new CachedRecentUnlockData
            {
                CacheKey = game.CacheKey,
                PlayniteGameId = game.PlayniteGameId,
                ProviderKey = game.ProviderKey,
                ProviderPlatformKey = game.ProviderPlatformKey,
                AppId = game.AppId,
                ProviderGameKey = game.ProviderGameKey,
                GameName = game.GameName,
                ApiName = achievement.ApiName,
                DisplayName = achievement.DisplayName,
                Description = achievement.Description,
                UnlockedIconPath = achievement.UnlockedIconPath,
                LockedIconPath = achievement.LockedIconPath,
                Points = achievement.Points,
                ScaledPoints = achievement.ScaledPoints,
                Category = achievement.Category,
                CategoryType = achievement.CategoryType,
                TrophyType = achievement.TrophyType,
                Hidden = achievement.Hidden,
                IsCapstone = achievement.IsCapstone,
                GlobalPercentUnlocked = achievement.GlobalPercentUnlocked,
                Rarity = achievement.Rarity,
                Unlocked = achievement.Unlocked,
                UnlockTimeUtc = achievement.UnlockTimeUtc,
                ProgressNum = achievement.ProgressNum,
                ProgressDenom = achievement.ProgressDenom
            };
        }

        private static void Increment(IDictionary<DateTime, int> counts, DateTime date)
        {
            counts.TryGetValue(date, out var current);
            counts[date] = AddClamped(current, 1);
        }

        private static int AddClamped(int current, int value)
        {
            var sum = (long)current + value;
            return sum > int.MaxValue ? int.MaxValue : (int)sum;
        }
    }
}
