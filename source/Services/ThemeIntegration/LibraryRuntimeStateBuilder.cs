using Playnite.SDK;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Achievements.Scoring;
using PlayniteAchievements.Models.ThemeIntegration;
using PlayniteAchievements.Providers;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.Cache;
using PlayniteAchievements.Services.Captures;
using PlayniteAchievements.Services.GameCustomData;
using PlayniteAchievements.Services.Images;
using PlayniteAchievements.Services.Summaries;

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
            new ProviderBucket("Local", (state, items) => state.LocalGames = items),
            new ProviderBucket("Ubisoft", (state, items) => state.UbisoftGames = items),
            new ProviderBucket("RPCS3", (state, items) => state.RPCS3Games = items),
            new ProviderBucket("Xenia", (state, items) => state.XeniaGames = items),
            new ProviderBucket("ShadPS4", (state, items) => state.ShadPS4Games = items),
            new ProviderBucket("GameJolt", (state, items) => state.GameJoltGames = items),
            new ProviderBucket("Riot", (state, items) => state.RiotGames = items),
            new ProviderBucket("FFXIV", (state, items) => state.FFXIVGames = items),
            new ProviderBucket("Manual", (state, items) => state.ManualGames = items)
        };

        /// <summary>
        /// The library state: game summaries, totals and scores from the summary, the newest
        /// unlocks from its recent rows, and <paramref name="rarestRecent"/>, the rarest unlocks
        /// of the last 180 days.
        /// </summary>
        public static LibraryRuntimeState BuildFromCachedSummary(
            CachedSummaryData summaryData,
            IReadOnlyList<CachedRecentUnlockData> rarestRecent,
            IPlayniteAPI api,
            CancellationToken token,
            GameCustomDataStore customDataStore = null)
        {
            token.ThrowIfCancellationRequested();

            summaryData ??= new CachedSummaryData();
            summaryData.Games ??= new List<CachedGameSummaryData>();
            summaryData.RecentUnlocks ??= new List<CachedRecentUnlockData>();
            var state = new LibraryRuntimeState();

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
            var platformScores = new PlatformScoreTotals();
            var tags = new AchievementTallyBuilder();

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
                tags.AddAll(game.Tallies);

                var rareAndUltraRare = AchievementRarityStatsCombiner.Combine(rare, ultraRare);
                var overall = AchievementRarityStatsCombiner.Combine(common, uncommon, rare, ultraRare);
                var providerKey = ResolveEffectiveProviderKey(game.ProviderKey, game.ProviderPlatformKey);
                var providerName = ProviderRegistry.GetLocalizedName(providerKey);
                var platform = ResolveSummaryPlatform(presentation.Platform, providerKey, providerName);

                allGames.Add(new GameAchievementSummary(
                    gameId,
                    presentation.Game?.Name ?? game.GameName ?? string.Empty,
                    platform,
                    GameSummaryArtResolver.ResolveForGame(gameId, customDataStore) ?? presentation.CoverImagePath,
                    AchievementCompletionPercentCalculator.ComputeRoundedPercent(
                        game.UnlockedAchievements,
                        game.TotalAchievements),
                    game.RareCount + game.UltraRareCount,
                    game.UncommonCount,
                    game.CommonCount,
                    game.IsCompleted,
                    game.LastUnlockUtc?.ToLocalTime() ?? DateTime.MinValue,
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
                platformScores.Add(providerKey, game.PlatformScorePoints, game.PlatformScorePointsTotal);
            }

            ApplySummaryListsAndTotals(state, allGames);
            state.TotalTagStats = AchievementTagStatsBuilder.Build(tags.Build(), null);
            PopulateProviderLists(state, allGames);

            var scoreSnapshot = AchievementScoreCalculator.CreateModernScoreSnapshot(collectorScore, prestigeScore);
            scoreSnapshot.LegacyScore = AchievementScoreCalculator.CalculateLegacyScore(
                state.PlatinumTrophies,
                state.GoldTrophies,
                state.SilverTrophies,
                state.BronzeTrophies);
            scoreSnapshot.LegacyLevel = AchievementLevelCalculator.CalculateLegacy(scoreSnapshot.LegacyScore);
            ApplyScores(state, scoreSnapshot, platformScores);

            var mostRecent = Details(summaryData.RecentUnlocks, api, presentationByGameId);
            var rarest = Details(rarestRecent, api, presentationByGameId);
            state.MostRecentUnlocksTop3 = mostRecent.Take(3).ToList();
            state.MostRecentUnlocksTop5 = mostRecent.Take(5).ToList();
            state.MostRecentUnlocksTop10 = mostRecent.Take(10).ToList();
            state.RarestRecentUnlocksTop3 = rarest.Take(3).ToList();
            state.RarestRecentUnlocksTop5 = rarest.Take(5).ToList();
            state.RarestRecentUnlocksTop10 = rarest.Take(10).ToList();
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

        private static void ApplyScores(
            LibraryRuntimeState state,
            AchievementScoreSnapshot scoreSnapshot,
            PlatformScoreTotals platformScores)
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
            state.CollectorMastery = scoreSnapshot.CollectorLevel?.Mastery ?? 0;
            state.PrestigeMastery = scoreSnapshot.PrestigeLevel?.Mastery ?? 0;

            platformScores ??= new PlatformScoreTotals();
            state.GamerscoreScore = platformScores.Gamerscore;
            state.GamerscoreTotal = platformScores.GamerscoreTotal;
            state.EpicXpScore = platformScores.EpicXp;
            state.EpicXpTotal = platformScores.EpicXpTotal;
            state.RetroPointsScore = platformScores.RetroPoints;
            state.RetroPointsTotal = platformScores.RetroPointsTotal;

            state.PointsScores = BuildPointsScores(platformScores);
            ScoreCardChoices.PublishFamiliesWithPoints(platformScores);
        }

        private static List<PointsScoreItem> BuildPointsScores(PlatformScoreTotals platformScores)
        {
            var items = new List<PointsScoreItem>();
            foreach (var family in platformScores.Families)
            {
                items.Add(new PointsScoreItem
                {
                    FamilyKey = family,
                    Name = PointsFamilyNames.GetLabel(family),
                    Score = platformScores.Get(family),
                    Total = platformScores.GetPossible(family)
                });
            }

            return items;
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

        /// <summary>
        /// Rows as theme items: each with its Playnite game, its effective provider and its
        /// saved captures.
        /// </summary>
        public static List<AchievementDetail> Details(
            IEnumerable<CachedRecentUnlockData> rows,
            IPlayniteAPI api,
            IDictionary<Guid, GamePresentation> presentationByGameId = null)
        {
            presentationByGameId ??= new Dictionary<Guid, GamePresentation>();
            var captures = new Dictionary<string, GameCaptureSet>(StringComparer.Ordinal);
            var categoryArt = new Dictionary<Guid, CategoryArtChainMemo>();
            var result = new List<AchievementDetail>();
            foreach (var recent in rows ?? Enumerable.Empty<CachedRecentUnlockData>())
            {
                var game = ResolveRecentGame(recent, api, presentationByGameId);
                var gameId = recent.PlayniteGameId ?? Guid.Empty;
                if (!categoryArt.TryGetValue(gameId, out var memo))
                {
                    categoryArt[gameId] = memo = new CategoryArtChainMemo();
                }
                var detail = new AchievementDetail
                {
                    ApiName = recent.ApiName,
                    DisplayName = recent.DisplayName,
                    Description = recent.Description,
                    UnlockedIconPath = recent.UnlockedIconPath,
                    LockedIconPath = recent.LockedIconPath,
                    Points = recent.Points,
                    ScaledPoints = recent.ScaledPoints,
                    Category = recent.Category,
                    ProviderCategory = recent.ProviderCategory,
                    Tags = recent.Tags,
                    CategoryKind = recent.CategoryKind,
                    TrophyType = recent.TrophyType,
                    Hidden = recent.Hidden,
                    IsCapstone = recent.IsCapstone,
                    AchievementNote = recent.AchievementNote,
                    Game = game,
                    ProviderKey = ResolveEffectiveProviderKey(recent.ProviderKey, recent.ProviderPlatformKey),
                    GlobalPercentUnlocked = recent.GlobalPercentUnlocked,
                    Rarity = recent.Rarity,
                    Unlocked = recent.Unlocked,
                    UnlockTimeUtc = recent.UnlockTimeUtc,
                    ProgressNum = recent.ProgressNum,
                    ProgressDenom = recent.ProgressDenom,

                    // A goal is one only while locked; the category's art is the user's image, else
                    // the default art of the category or its nearest ancestor, as on the other surfaces.
                    IsGoal = recent.GoalOrder.HasValue && !recent.Unlocked,
                    GoalOrderIndex = recent.GoalOrder.HasValue && !recent.Unlocked ? recent.GoalOrder.Value : int.MaxValue,
                    CategoryOrderIndex = recent.CategoryOrder ?? int.MaxValue,
                    CategoryArtPath = recent.CategoryArtPath ?? (gameId == Guid.Empty ? null : CategoryArtChainResolver.Resolve(
                        gameId,
                        recent.Category,
                        recent.ProviderCategory,
                        null,
                        CategoryArtDisplayMode.FilePath,
                        memo))
                };

                var gameName = recent.GameName ?? game?.Name ?? string.Empty;
                if (!captures.TryGetValue(gameName, out var set))
                {
                    set = AchievementCapturePathResolver.ResolveGameSet(gameName);
                    captures[gameName] = set;
                }

                AchievementCapturePathResolver.Apply(detail, set);
                result.Add(detail);
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

        private static string ResolveEffectiveProviderKey(string providerKey, string providerPlatformKey)
        {
            return ProviderKeyResolver.ResolveEffectiveProviderKey(
                providerKey,
                providerPlatformKey,
                string.Empty);
        }

        private static string ResolveSummaryPlatform(string platform, string providerKey, string providerName)
        {
            var normalizedPlatform = string.IsNullOrWhiteSpace(platform) ? null : platform.Trim();
            if (!string.IsNullOrWhiteSpace(normalizedPlatform) &&
                !normalizedPlatform.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
            {
                return normalizedPlatform;
            }

            var normalizedProviderName = string.IsNullOrWhiteSpace(providerName) ? null : providerName.Trim();
            if (!string.IsNullOrWhiteSpace(normalizedProviderName) &&
                !normalizedProviderName.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
            {
                return normalizedProviderName;
            }

            return string.IsNullOrWhiteSpace(providerKey) ? "Unknown" : providerKey.Trim();
        }

        internal sealed class GamePresentation
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

