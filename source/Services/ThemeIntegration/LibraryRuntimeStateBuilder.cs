using Playnite.SDK;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Achievements.Scoring;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Models.ThemeIntegration;
using PlayniteAchievements.Providers;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.Cache;
using PlayniteAchievements.Services.GameCustomData;
using PlayniteAchievements.Services.Images;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.Services.Summaries;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.Services.ThemeIntegration
{
    internal static class LibraryRuntimeStateBuilder
    {
        private static readonly ProviderBucket[] ProviderBuckets =
        {
            new ProviderBucket("Steam", (state, items) => state.SteamGames = items),
            new ProviderBucket("GOG", (state, items) => state.GOGGames = items),
            new ProviderBucket("Epic", (state, items) => state.EpicGames = items),
            new ProviderBucket("BattleNet", (state, items) => state.BattleNetGames = items),
            new ProviderBucket("EA", (state, items) => state.EAGames = items),
            new ProviderBucket("Xbox", (state, items) => state.XboxGames = items),
            new ProviderBucket("PSN", (state, items) => state.PSNGames = items),
            new ProviderBucket("RetroAchievements", (state, items) => state.RetroAchievementsGames = items),
            new ProviderBucket("Apple", (state, items) => state.AppleGames = items),
            new ProviderBucket("GooglePlay", (state, items) => state.GooglePlayGames = items),
            new ProviderBucket("Hoyoverse", (state, items) => state.HoyoverseGames = items),
            new ProviderBucket("Ubisoft", (state, items) => state.UbisoftGames = items),
            new ProviderBucket("RPCS3", (state, items) => state.RPCS3Games = items),
            new ProviderBucket("Xenia", (state, items) => state.XeniaGames = items),
            new ProviderBucket("ShadPS4", (state, items) => state.ShadPS4Games = items),
            new ProviderBucket("Manual", (state, items) => state.ManualGames = items)
        };

        public static LibraryRuntimeState Build(
            List<GameAchievementData> allData,
            IPlayniteAPI api,
            CancellationToken token,
            bool includeHeavyAchievementLists = true)
        {
            token.ThrowIfCancellationRequested();

            allData ??= new List<GameAchievementData>();
            var state = new LibraryRuntimeState
            {
                HeavyListsBuilt = includeHeavyAchievementLists
            };

            var allGames = new List<GameAchievementSummary>();
            var collectorScore = 0;
            var prestigeScore = 0;

            foreach (var data in allData)
            {
                token.ThrowIfCancellationRequested();

                if (data?.PlayniteGameId == null || !data.HasAchievements)
                {
                    continue;
                }

                var total = data.Achievements?.Count ?? 0;
                if (total <= 0)
                {
                    continue;
                }

                var stats = AchievementStatsAccumulator.FromAchievements(data.Achievements);
                var common = stats.CommonStats;
                var uncommon = stats.UncommonStats;
                var rare = stats.RareStats;
                var ultraRare = stats.UltraRareStats;
                AddRarityStats(state.TotalCommon, common);
                AddRarityStats(state.TotalUncommon, uncommon);
                AddRarityStats(state.TotalRare, rare);
                AddRarityStats(state.TotalUltraRare, ultraRare);

                var rareAndUltraRare = AchievementRarityStatsCombiner.Combine(rare, ultraRare);
                var overall = AchievementRarityStatsCombiner.Combine(common, uncommon, rare, ultraRare);
                var gold = stats.RareCount + stats.UltraRareCount;
                var silver = stats.UncommonCount;
                var bronze = stats.CommonCount;
                var providerKey = ResolveEffectiveProviderKey(data.ProviderKey, data.ProviderPlatformKey);
                var providerName = ProviderRegistry.GetLocalizedName(providerKey);
                collectorScore = AddScore(collectorScore, stats.CollectionScore);
                prestigeScore = AddScore(prestigeScore, stats.PrestigeScore);

                var summary = new GameAchievementSummary(
                    data.PlayniteGameId.Value,
                    data.Game?.Name ?? data.GameName ?? string.Empty,
                    data.Game?.Source?.Name ?? "Unknown",
                    GameSummaryArtResolver.Resolve(
                        data.PlayniteGameId,
                        data.GameSummaryCategory,
                        data.AchievementCategoryImageOverrides) ?? ResolveCoverImagePath(data.Game, api),
                    stats.ProgressPercent,
                    gold,
                    silver,
                    bronze,
                    data.IsCompleted,
                    stats.LastUnlockUtc.HasValue ? stats.LastUnlockUtc.Value.ToLocalTime() : DateTime.MinValue,
                    null,
                    common,
                    uncommon,
                    rare,
                    ultraRare,
                    rareAndUltraRare,
                    overall,
                    providerKey,
                    providerName,
                    data.Game?.LastActivity,
                    stats.UnlockedAchievements,
                    stats.TotalAchievements,
                    sortingName: data.Game?.SortingName ?? data.Game?.Name ?? data.GameName ?? string.Empty);

                allGames.Add(summary);
            }

            ApplySummaryListsAndTotals(state, allGames);
            PopulateProviderLists(state, allGames);

            var scoreSnapshot = AchievementScoreCalculator.CreateModernScoreSnapshot(collectorScore, prestigeScore);
            scoreSnapshot.LegacyScore = AchievementScoreCalculator.CalculateLegacyScore(
                state.PlatinumTrophies,
                state.GoldTrophies,
                state.SilverTrophies,
                state.BronzeTrophies);
            scoreSnapshot.LegacyLevel = AchievementLevelCalculator.CalculateLegacy(scoreSnapshot.LegacyScore);
            ApplyScores(state, scoreSnapshot);

            PopulateAchievementLists(state, allData, token, includeHeavyAchievementLists);
            return state;
        }

        public static LibraryRuntimeState BuildFromCachedSummary(
            CachedSummaryData summaryData,
            IPlayniteAPI api,
            CancellationToken token,
            GameCustomDataStore customDataStore = null)
        {
            token.ThrowIfCancellationRequested();

            summaryData ??= new CachedSummaryData();
            summaryData.Games ??= new List<CachedGameSummaryData>();
            summaryData.RecentUnlocks ??= new List<CachedRecentUnlockData>();
            summaryData.UnlockCountsByDateByGame ??= new Dictionary<Guid, Dictionary<DateTime, int>>();

            var state = new LibraryRuntimeState
            {
                HeavyListsBuilt = false
            };

            var referencedGameIds = summaryData.Games
                .Where(game => game?.PlayniteGameId.HasValue == true)
                .Select(game => game.PlayniteGameId.Value)
                .Concat(summaryData.RecentUnlocks
                    .Where(recent => recent?.PlayniteGameId.HasValue == true)
                    .Select(recent => recent.PlayniteGameId.Value));
            var presentationByGameId = BuildGamePresentationCache(api, referencedGameIds);
            var allGames = new List<GameAchievementSummary>();
            var collectorScore = 0;
            var prestigeScore = 0;

            for (var i = 0; i < summaryData.Games.Count; i++)
            {
                token.ThrowIfCancellationRequested();

                var game = summaryData.Games[i];
                if (game == null ||
                    game.PlayniteGameId.HasValue != true ||
                    game.PlayniteGameId.Value == Guid.Empty ||
                    !game.HasAchievements ||
                    game.TotalAchievements <= 0)
                {
                    continue;
                }

                var gameId = game.PlayniteGameId.Value;
                var presentation = ResolveGamePresentation(api, gameId, presentationByGameId);
                var common = CreateRarityStats(game.CommonCount, game.TotalCommonPossible);
                var uncommon = CreateRarityStats(game.UncommonCount, game.TotalUncommonPossible);
                var rare = CreateRarityStats(game.RareCount, game.TotalRarePossible);
                var ultraRare = CreateRarityStats(game.UltraRareCount, game.TotalUltraRarePossible);

                AddRarityStats(state.TotalCommon, common);
                AddRarityStats(state.TotalUncommon, uncommon);
                AddRarityStats(state.TotalRare, rare);
                AddRarityStats(state.TotalUltraRare, ultraRare);

                var rareAndUltraRare = AchievementRarityStatsCombiner.Combine(rare, ultraRare);
                var overall = AchievementRarityStatsCombiner.Combine(common, uncommon, rare, ultraRare);
                var providerKey = ResolveEffectiveProviderKey(game.ProviderKey, game.ProviderPlatformKey);
                var providerName = ProviderRegistry.GetLocalizedName(providerKey);
                var latestUnlockDate = ResolveLatestUnlockDate(summaryData.UnlockCountsByDateByGame, gameId);

                allGames.Add(new GameAchievementSummary(
                    gameId,
                    presentation.Game?.Name ?? game.GameName ?? string.Empty,
                    presentation.Platform ?? "Unknown",
                    GameSummaryArtResolver.ResolveForGame(gameId, customDataStore) ?? presentation.CoverImagePath,
                    AchievementCompletionPercentCalculator.ComputeRoundedPercent(
                        game.UnlockedAchievements,
                        game.TotalAchievements),
                    game.RareCount + game.UltraRareCount,
                    game.UncommonCount,
                    game.CommonCount,
                    game.IsCompleted,
                    latestUnlockDate,
                    null,
                    common,
                    uncommon,
                    rare,
                    ultraRare,
                    rareAndUltraRare,
                    overall,
                    providerKey,
                    providerName,
                    presentation.LastPlayed,
                    game.UnlockedAchievements,
                    game.TotalAchievements,
                    sortingName: presentation.SortingName ?? presentation.Game?.Name ?? game.GameName ?? string.Empty));

                collectorScore = AddScore(collectorScore, game.CollectionScore);
                prestigeScore = AddScore(prestigeScore, game.PrestigeScore);
            }

            ApplySummaryListsAndTotals(state, allGames);
            PopulateProviderLists(state, allGames);

            var scoreSnapshot = AchievementScoreCalculator.CreateModernScoreSnapshot(collectorScore, prestigeScore);
            scoreSnapshot.LegacyScore = AchievementScoreCalculator.CalculateLegacyScore(
                state.PlatinumTrophies,
                state.GoldTrophies,
                state.SilverTrophies,
                state.BronzeTrophies);
            scoreSnapshot.LegacyLevel = AchievementLevelCalculator.CalculateLegacy(scoreSnapshot.LegacyScore);
            ApplyScores(state, scoreSnapshot);

            PopulateRecentLists(
                state,
                MaterializeRecentUnlocks(summaryData.RecentUnlocks, api, presentationByGameId, token),
                includeFullLists: false);
            return state;
        }

        private static void ApplySummaryListsAndTotals(
            LibraryRuntimeState state,
            List<GameAchievementSummary> allGames)
        {
            allGames = (allGames ?? new List<GameAchievementSummary>())
                .Where(item => item != null)
                .OrderByDescending(item => item.LastUnlockDate)
                .ThenByDescending(item => item.Progress)
                .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            state.AllGamesWithAchievements = allGames;
            state.GameSummariesDesc = allGames;
            state.GameSummariesAsc = allGames
                .OrderBy(item => item.LastUnlockDate)
                .ThenByDescending(item => item.Progress)
                .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            state.CompletedGamesDesc = state.GameSummariesDesc.Where(item => item.IsCompleted).ToList();
            state.CompletedGamesAsc = state.GameSummariesAsc.Where(item => item.IsCompleted).ToList();
            state.PlatinumGames = allGames
                .Where(item => item.IsCompleted && item.LastUnlockDate != DateTime.MinValue)
                .OrderByDescending(item => item.LastUnlockDate)
                .ToList();
            state.PlatinumGamesAscending = state.PlatinumGames.OrderBy(item => item.LastUnlockDate).ToList();

            state.PlatinumTrophies = state.PlatinumGames.Count;
            state.GoldTrophies = allGames.Sum(item => item.GoldCount);
            state.SilverTrophies = allGames.Sum(item => item.SilverCount);
            state.BronzeTrophies = allGames.Sum(item => item.BronzeCount);
            state.TotalRareAndUltraRare = AchievementRarityStatsCombiner.Combine(state.TotalRare, state.TotalUltraRare);
            state.TotalOverall = AchievementRarityStatsCombiner.Combine(
                state.TotalCommon,
                state.TotalUncommon,
                state.TotalRare,
                state.TotalUltraRare);
            state.TotalTrophies =
                state.PlatinumTrophies +
                state.GoldTrophies +
                state.SilverTrophies +
                state.BronzeTrophies;
        }

        private static void ApplyScores(LibraryRuntimeState state, AchievementScoreSnapshot scoreSnapshot)
        {
            if (state == null || scoreSnapshot == null)
            {
                return;
            }

            state.Score = scoreSnapshot.LegacyScore;
            state.Level = scoreSnapshot.LegacyLevel?.Level ?? 0;
            state.LevelProgress = scoreSnapshot.LegacyLevel?.LevelProgress ?? 0;
            state.Rank = scoreSnapshot.LegacyLevel?.Rank ?? "Bronze5";

            state.CollectorScore = scoreSnapshot.CollectorScore;
            state.CollectorLevel = GetDisplayLevel(scoreSnapshot.CollectorLevel);
            state.CollectorLevelProgress = scoreSnapshot.CollectorLevel?.LevelProgress ?? 0;
            state.CollectorRank = scoreSnapshot.CollectorLevel?.Rank ?? "Bronze5";

            state.PrestigeScore = scoreSnapshot.PrestigeScore;
            state.PrestigeLevel = GetDisplayLevel(scoreSnapshot.PrestigeLevel);
            state.PrestigeLevelProgress = scoreSnapshot.PrestigeLevel?.LevelProgress ?? 0;
            state.PrestigeRank = scoreSnapshot.PrestigeLevel?.Rank ?? "Bronze5";
        }

        private static int GetDisplayLevel(AchievementLevelSnapshot snapshot)
        {
            if (snapshot == null)
            {
                return 0;
            }

            return snapshot.DisplayLevel > 0 ? snapshot.DisplayLevel : snapshot.Level;
        }

        private static void PopulateProviderLists(
            LibraryRuntimeState state,
            IEnumerable<GameAchievementSummary> allGames)
        {
            var buckets = ProviderBuckets.ToDictionary(
                bucket => bucket.Key,
                _ => new List<GameAchievementSummary>(),
                StringComparer.OrdinalIgnoreCase);

            foreach (var summary in allGames ?? Enumerable.Empty<GameAchievementSummary>())
            {
                if (summary == null ||
                    string.IsNullOrWhiteSpace(summary.ProviderKey) ||
                    !buckets.TryGetValue(summary.ProviderKey, out var providerGames))
                {
                    continue;
                }

                providerGames.Add(summary);
            }

            foreach (var bucket in ProviderBuckets)
            {
                bucket.Set(
                    state,
                    buckets[bucket.Key]
                        .OrderByDescending(item => item.LastUnlockDate)
                        .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                        .ToList());
            }
        }

        private static void PopulateAchievementLists(
            LibraryRuntimeState state,
            List<GameAchievementData> allData,
            CancellationToken token,
            bool includeHeavyAchievementLists)
        {
            if (includeHeavyAchievementLists)
            {
                var allAchievements = new List<AchievementDetail>();
                foreach (var data in allData)
                {
                    token.ThrowIfCancellationRequested();
                    if (data?.Achievements == null)
                    {
                        continue;
                    }

                    foreach (var achievement in data.Achievements)
                    {
                        if (achievement == null)
                        {
                            continue;
                        }

                        ApplyAchievementPresentation(achievement, data);
                        allAchievements.Add(achievement);
                    }
                }

                state.AllAchievements = allAchievements.ToList();
                state.AllAchievementsUnlockAsc = AchievementSortHelper.CreateSortedDetailList(
                    allAchievements,
                    nameof(AchievementDisplayItem.UnlockTime),
                    ListSortDirection.Ascending,
                    includeGameNameTieBreak: true);
                state.AllAchievementsUnlockDesc = AchievementSortHelper.CreateSortedDetailList(
                    allAchievements,
                    nameof(AchievementDisplayItem.UnlockTime),
                    ListSortDirection.Descending,
                    includeGameNameTieBreak: true);
                state.AllAchievementsRarityAsc = AchievementSortHelper.CreateSortedDetailList(
                    allAchievements,
                    nameof(AchievementDisplayItem.RaritySortValue),
                    ListSortDirection.Ascending,
                    includeGameNameTieBreak: true);
                state.AllAchievementsRarityDesc = AchievementSortHelper.CreateSortedDetailList(
                    allAchievements,
                    nameof(AchievementDisplayItem.RaritySortValue),
                    ListSortDirection.Descending,
                    includeGameNameTieBreak: true);

                var unlockedAchievements = allAchievements
                    .Where(a => a != null && a.UnlockTimeUtc.HasValue && a.UnlockTimeUtc.Value != DateTime.MinValue)
                    .ToList();
                PopulateRecentLists(state, unlockedAchievements, includeFullLists: true);
                return;
            }

            var unlockedRecent = new List<AchievementDetail>();
            foreach (var data in allData)
            {
                token.ThrowIfCancellationRequested();
                if (data?.Achievements == null)
                {
                    continue;
                }

                foreach (var achievement in data.Achievements)
                {
                    if (achievement == null ||
                        !achievement.UnlockTimeUtc.HasValue ||
                        achievement.UnlockTimeUtc.Value == DateTime.MinValue)
                    {
                        continue;
                    }

                    ApplyAchievementPresentation(achievement, data);
                    unlockedRecent.Add(achievement);
                }
            }

            PopulateRecentLists(state, unlockedRecent, includeFullLists: false);
        }

        private static void ApplyAchievementPresentation(AchievementDetail achievement, GameAchievementData data)
        {
            if (achievement == null)
            {
                return;
            }

            achievement.Game = data?.Game;
            achievement.ProviderKey = ResolveEffectiveProviderKey(data?.ProviderKey, data?.ProviderPlatformKey);
            ApplyCategoryImagePresentation(achievement, data);
        }

        private static void ApplyCategoryImagePresentation(AchievementDetail achievement, GameAchievementData data)
        {
            if (achievement == null)
            {
                return;
            }

            var category = AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(achievement.Category);
            achievement.CategoryOrderIndex =
                AchievementCategoryFilterOrderHelper.ResolveCategoryOrderIndex(category, data?.AchievementCategoryOrder);

            var gameId = data?.PlayniteGameId;
            if (!gameId.HasValue || gameId.Value == Guid.Empty)
            {
                achievement.CategoryArtPath = null;
                return;
            }

            CategoryImageOverrideData imageOverride = null;
            if (!string.IsNullOrWhiteSpace(category) &&
                data?.AchievementCategoryImageOverrides != null)
            {
                data.AchievementCategoryImageOverrides.TryGetValue(category, out imageOverride);
            }

            // Default images are keyed by the provider label; renames only change Category.
            var providerCategory = AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(
                achievement.ProviderCategory ?? achievement.Category);
            achievement.CategoryArtPath =
                NormalizeImageOverridePath(imageOverride?.Art) ??
                CategoryDefaultImageResolver.Resolve(gameId, providerCategory);
        }

        private static string NormalizeImageOverridePath(string value)
        {
            var normalized = (value ?? string.Empty).Trim();
            return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
        }

        private static void PopulateRecentLists(
            LibraryRuntimeState state,
            List<AchievementDetail> unlockedAchievements,
            bool includeFullLists)
        {
            unlockedAchievements ??= new List<AchievementDetail>();

            var mostRecent = AchievementSortHelper.CreateSortedDetailList(
                unlockedAchievements,
                nameof(AchievementDisplayItem.UnlockTime),
                ListSortDirection.Descending,
                includeGameNameTieBreak: true);
            var rareRecentCutoffUtc = DateTime.UtcNow.AddDays(-180);
            var rareRecent = AchievementSortHelper.CreateSortedDetailList(
                unlockedAchievements.Where(a => NormalizeUtc(a.UnlockTimeUtc.Value) >= rareRecentCutoffUtc),
                nameof(AchievementDisplayItem.RaritySortValue),
                ListSortDirection.Ascending,
                includeGameNameTieBreak: true);

            if (includeFullLists)
            {
                state.MostRecentUnlocks = mostRecent;
                state.RarestRecentUnlocks = rareRecent;
            }

            state.MostRecentUnlocksTop3 = mostRecent.Take(3).ToList();
            state.MostRecentUnlocksTop5 = mostRecent.Take(5).ToList();
            state.MostRecentUnlocksTop10 = mostRecent.Take(10).ToList();
            state.RarestRecentUnlocksTop3 = rareRecent.Take(3).ToList();
            state.RarestRecentUnlocksTop5 = rareRecent.Take(5).ToList();
            state.RarestRecentUnlocksTop10 = rareRecent.Take(10).ToList();
        }

        private static string ResolveCoverImagePath(Playnite.SDK.Models.Game game, IPlayniteAPI api)
        {
            if (game == null || api?.Database == null)
            {
                return string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(game.CoverImage))
            {
                var coverPath = api.Database.GetFullFilePath(game.CoverImage);
                if (!string.IsNullOrWhiteSpace(coverPath))
                {
                    return coverPath;
                }
            }

            if (!string.IsNullOrWhiteSpace(game.Icon))
            {
                var iconPath = api.Database.GetFullFilePath(game.Icon);
                if (!string.IsNullOrWhiteSpace(iconPath))
                {
                    return iconPath;
                }
            }

            return string.Empty;
        }

        private static Dictionary<Guid, GamePresentation> BuildGamePresentationCache(
            IPlayniteAPI api,
            IEnumerable<Guid> gameIds)
        {
            var result = new Dictionary<Guid, GamePresentation>();
            var distinctGameIds = new HashSet<Guid>(
                (gameIds ?? Enumerable.Empty<Guid>())
                    .Where(id => id != Guid.Empty));
            if (distinctGameIds.Count == 0)
            {
                return result;
            }

            foreach (var gameId in distinctGameIds)
            {
                var game = api?.Database?.Games?.Get(gameId);
                if (game != null)
                {
                    result[gameId] = CreateGamePresentation(api, game);
                }
            }

            return result;
        }

        private static GamePresentation ResolveGamePresentation(
            IPlayniteAPI api,
            Guid gameId,
            IDictionary<Guid, GamePresentation> presentationByGameId)
        {
            if (presentationByGameId != null &&
                presentationByGameId.TryGetValue(gameId, out var cached))
            {
                return cached;
            }

            var game = api?.Database?.Games?.Get(gameId);
            var presentation = CreateGamePresentation(api, game);
            if (presentationByGameId != null)
            {
                presentationByGameId[gameId] = presentation;
            }
            return presentation;
        }

        private static GamePresentation CreateGamePresentation(IPlayniteAPI api, Game game)
        {
            return new GamePresentation
            {
                Game = game,
                Platform = game?.Source?.Name ?? "Unknown",
                CoverImagePath = ResolveCoverImagePath(game, api),
                LastPlayed = game?.LastActivity,
                SortingName = game?.SortingName
            };
        }

        private static AchievementRarityStats CreateRarityStats(int unlocked, int total)
        {
            var normalizedTotal = Math.Max(0, total);
            var normalizedUnlocked = Math.Max(0, Math.Min(unlocked, normalizedTotal));
            return new AchievementRarityStats
            {
                Total = normalizedTotal,
                Unlocked = normalizedUnlocked,
                Locked = normalizedTotal - normalizedUnlocked
            };
        }

        private static DateTime ResolveLatestUnlockDate(
            IReadOnlyDictionary<Guid, Dictionary<DateTime, int>> countsByGameId,
            Guid gameId)
        {
            if (countsByGameId == null ||
                !countsByGameId.TryGetValue(gameId, out var counts) ||
                counts == null ||
                counts.Count == 0)
            {
                return DateTime.MinValue;
            }

            var latestUtc = counts.Keys
                .Where(date => date != DateTime.MinValue)
                .DefaultIfEmpty(DateTime.MinValue)
                .Max();
            return latestUtc == DateTime.MinValue
                ? DateTime.MinValue
                : NormalizeUtc(latestUtc).ToLocalTime();
        }

        private static List<AchievementDetail> MaterializeRecentUnlocks(
            IEnumerable<CachedRecentUnlockData> recentUnlocks,
            IPlayniteAPI api,
            IDictionary<Guid, GamePresentation> presentationByGameId,
            CancellationToken token)
        {
            var result = new List<AchievementDetail>();
            foreach (var recent in recentUnlocks ?? Enumerable.Empty<CachedRecentUnlockData>())
            {
                token.ThrowIfCancellationRequested();
                if (recent == null ||
                    !recent.UnlockTimeUtc.HasValue ||
                    recent.UnlockTimeUtc.Value == DateTime.MinValue)
                {
                    continue;
                }

                var game = ResolveRecentGame(recent, api, presentationByGameId);
                result.Add(new AchievementDetail
                {
                    ApiName = recent.ApiName,
                    DisplayName = recent.DisplayName,
                    Description = recent.Description,
                    UnlockedIconPath = recent.UnlockedIconPath,
                    LockedIconPath = recent.LockedIconPath,
                    Points = recent.Points,
                    ScaledPoints = recent.ScaledPoints,
                    Category = recent.Category,
                    CategoryType = recent.CategoryType,
                    TrophyType = recent.TrophyType,
                    Hidden = recent.Hidden,
                    IsCapstone = recent.IsCapstone,
                    AchievementNote = recent.AchievementNote,
                    Game = game,
                    ProviderKey = ResolveEffectiveProviderKey(recent.ProviderKey, recent.ProviderPlatformKey),
                    GlobalPercentUnlocked = recent.GlobalPercentUnlocked,
                    Rarity = recent.Rarity,
                    Unlocked = true,
                    UnlockTimeUtc = NormalizeUtc(recent.UnlockTimeUtc.Value),
                    ProgressNum = recent.ProgressNum,
                    ProgressDenom = recent.ProgressDenom
                });
            }

            return result;
        }

        private static Game ResolveRecentGame(
            CachedRecentUnlockData recent,
            IPlayniteAPI api,
            IDictionary<Guid, GamePresentation> presentationByGameId)
        {
            if (recent?.PlayniteGameId.HasValue == true &&
                recent.PlayniteGameId.Value != Guid.Empty)
            {
                var presentation = ResolveGamePresentation(api, recent.PlayniteGameId.Value, presentationByGameId);
                if (presentation.Game != null)
                {
                    return presentation.Game;
                }

                return new Game
                {
                    Id = recent.PlayniteGameId.Value,
                    Name = recent.GameName ?? string.Empty
                };
            }

            return null;
        }

        private static int AddScore(int current, int value)
        {
            if (value <= 0)
            {
                return current;
            }

            return current > int.MaxValue - value
                ? int.MaxValue
                : current + value;
        }

        private static void AccumulateGameRarityStats(
            GameAchievementData data,
            AchievementRarityStats common,
            AchievementRarityStats uncommon,
            AchievementRarityStats rare,
            AchievementRarityStats ultraRare)
        {
            if (data?.Achievements == null)
            {
                return;
            }

            foreach (var achievement in data.Achievements)
            {
                if (!TryGetEffectiveRarityTier(achievement, out var tier))
                {
                    continue;
                }

                var target = GetStatsForTier(tier, common, uncommon, rare, ultraRare);
                if (target == null)
                {
                    continue;
                }

                target.Total++;
                if (achievement.Unlocked)
                {
                    target.Unlocked++;
                }
                else
                {
                    target.Locked++;
                }
            }
        }

        private static void AddRarityStats(AchievementRarityStats target, AchievementRarityStats source)
        {
            if (target == null || source == null)
            {
                return;
            }

            target.Total += source.Total;
            target.Unlocked += source.Unlocked;
            target.Locked += source.Locked;
        }

        private static bool TryGetEffectiveRarityTier(AchievementDetail achievement, out RarityTier tier)
        {
            tier = RarityTier.Common;
            if (achievement == null)
            {
                return false;
            }

            tier = achievement.Rarity;
            return true;
        }

        private static AchievementRarityStats GetStatsForTier(
            RarityTier tier,
            AchievementRarityStats common,
            AchievementRarityStats uncommon,
            AchievementRarityStats rare,
            AchievementRarityStats ultraRare)
        {
            switch (tier)
            {
                case RarityTier.UltraRare:
                    return ultraRare;
                case RarityTier.Rare:
                    return rare;
                case RarityTier.Uncommon:
                    return uncommon;
                default:
                    return common;
            }
        }

        private static DateTime NormalizeUtc(DateTime timestamp)
        {
            if (timestamp.Kind == DateTimeKind.Unspecified)
            {
                return DateTime.SpecifyKind(timestamp, DateTimeKind.Utc);
            }

            return timestamp.Kind == DateTimeKind.Local ? timestamp.ToUniversalTime() : timestamp;
        }

        private static string ResolveEffectiveProviderKey(string providerKey, string providerPlatformKey)
        {
            var resolved = !string.IsNullOrWhiteSpace(providerPlatformKey)
                ? providerPlatformKey
                : providerKey;
            return string.IsNullOrWhiteSpace(resolved) ? string.Empty : resolved.Trim();
        }

        private sealed class GamePresentation
        {
            public Game Game { get; set; }

            public string Platform { get; set; }

            public string CoverImagePath { get; set; }

            public DateTime? LastPlayed { get; set; }

            public string SortingName { get; set; }
        }

        private sealed class ProviderBucket
        {
            public ProviderBucket(string key, Action<LibraryRuntimeState, List<GameAchievementSummary>> set)
            {
                Key = key;
                Set = set;
            }

            public string Key { get; }

            public Action<LibraryRuntimeState, List<GameAchievementSummary>> Set { get; }
        }
    }
}

