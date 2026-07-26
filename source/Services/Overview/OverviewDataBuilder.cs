using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Achievements.Scoring;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.Cache;
using PlayniteAchievements.Services.GameCustomData;
using PlayniteAchievements.Services.Summaries;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.ViewModels.Items;
using Playnite.SDK;

namespace PlayniteAchievements.Services.Overview
{
    public sealed class OverviewDataBuilder
    {
        private sealed class GamePresentation
        {
            public string DisplayName { get; set; }
            public string SortingName { get; set; }

            public string IconPath { get; set; }

            public string CoverPath { get; set; }

            public DateTime? LastPlayed { get; set; }

            public string PlatformText { get; set; }

            public IReadOnlyList<string> Platforms { get; set; }

            public string RegionText { get; set; }

            public ulong PlaytimeSeconds { get; set; }

            public Playnite.SDK.Models.Game Game { get; set; }
        }

        private readonly AchievementDataService _achievementDataService;
        private readonly IReadOnlyList<IDataProvider> _providers;
        private readonly IPlayniteAPI _playniteApi;
        private readonly ILogger _logger;
        private readonly GameSummaryItemBuilder _summaryBuilder;

        public OverviewDataBuilder(
            AchievementDataService achievementDataService,
            IReadOnlyList<IDataProvider> providers,
            IPlayniteAPI playniteApi,
            ILogger logger)
        {
            _achievementDataService = achievementDataService ?? throw new ArgumentNullException(nameof(achievementDataService));
            _providers = providers ?? new List<IDataProvider>();
            _playniteApi = playniteApi;
            _logger = logger;
            _summaryBuilder = new GameSummaryItemBuilder(_providers, _playniteApi, _logger);
        }

        public OverviewDataSnapshot Build(
            PlayniteAchievementsSettings settings,
            CancellationToken cancel)
        {
            settings ??= new PlayniteAchievementsSettings();

            var providerLookup = BuildProviderLookup();

            CachedSummaryData queryData;
            using (PerfScope.Start(_logger, "Overview.GetCachedSummaryData", thresholdMs: 25))
            {
                queryData = _achievementDataService.GetCachedSummaryDataForOverview(0);
            }

            if (queryData == null)
            {
                // Summary reads only fail when the store itself is unavailable, so a
                // whole-library fallback load would fail too; render empty instead.
                _logger?.Warn("[Overview] Cached summary unavailable; returning empty overview snapshot.");
                queryData = new CachedSummaryData();
            }

            using (PerfScope.Start(_logger, "Overview.BuildFromCachedSummaryData", thresholdMs: 25))
            {
                return BuildFromCachedSummaryData(settings, queryData, providerLookup, cancel);
            }
        }

        private OverviewDataSnapshot BuildFromCachedSummaryData(
            PlayniteAchievementsSettings settings,
            CachedSummaryData queryData,
            IReadOnlyDictionary<string, (string iconKey, string colorHex)> providerLookup,
            CancellationToken cancel)
        {
            settings ??= new PlayniteAchievementsSettings();
            queryData ??= new CachedSummaryData();
            providerLookup ??= BuildProviderLookup();

            var snapshot = new OverviewDataSnapshot
            {
                Achievements = new List<AchievementDisplayItem>(),
                GameSummaries = new List<GameSummaryItem>(),
                RecentAchievements = new List<AchievementDisplayItem>(),
                GlobalUnlockCountsByDate = CloneCounts(queryData.GlobalUnlockCountsByDate),
                UnlockCountsByDateByGame = CloneCountsByGame(queryData.UnlockCountsByDateByGame),
                UnlockedByProvider = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                TotalByProvider = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
            };

            var games = queryData.Games ?? new List<CachedGameSummaryData>();
            var recentUnlocks = queryData.RecentUnlocks ?? new List<CachedRecentUnlockData>();
            var referencedGameIds = games
                .Where(g => g?.PlayniteGameId.HasValue == true)
                .Select(g => g.PlayniteGameId.Value)
                .Concat(recentUnlocks
                    .Where(r => r?.PlayniteGameId.HasValue == true)
                    .Select(r => r.PlayniteGameId.Value));
            var presentationByGameId = BuildGamePresentationCache(referencedGameIds);

            for (var i = 0; i < games.Count; i++)
            {
                cancel.ThrowIfCancellationRequested();

                var game = games[i];
                if (game == null ||
                    !game.HasAchievements ||
                    game.TotalAchievements <= 0)
                {
                    continue;
                }

                var providerKey = ResolveEffectiveProviderKey(game.ProviderKey, game.ProviderPlatformKey);
                var providerName = ProviderRegistry.GetLocalizedName(providerKey);
                if (string.IsNullOrWhiteSpace(providerName))
                {
                    providerName = providerKey;
                }

                if (!providerLookup.TryGetValue(providerKey, out var providerMetadata))
                {
                    providerMetadata = ("ProviderIcon" + providerKey, "#888888");
                }

                var presentation = ResolveGamePresentation(game.PlayniteGameId, presentationByGameId);
                var summaryArt = GameSummaryArtResolver.ResolveForGame(game.PlayniteGameId);
                snapshot.GameSummaries.Add(new GameSummaryItem
                {
                    GameName = presentation.DisplayName ?? game.GameName ?? "Unknown",
                    SortingName = presentation.SortingName ?? presentation.DisplayName ?? game.GameName ?? "Unknown",
                    GameLogo = summaryArt ?? presentation.IconPath,
                    GameCoverPath = summaryArt ?? presentation.CoverPath,
                    PlatformText = presentation.PlatformText,
                    Platforms = presentation.Platforms ?? Array.Empty<string>(),
                    RegionText = presentation.RegionText,
                    PlaytimeSeconds = presentation.PlaytimeSeconds,
                    AppId = game.AppId,
                    ProviderGameKey = game.ProviderGameKey,
                    PlayniteGameId = game.PlayniteGameId,
                    TotalAchievements = game.TotalAchievements,
                    UnlockedAchievements = game.UnlockedAchievements,
                    CommonCount = game.CommonCount,
                    UncommonCount = game.UncommonCount,
                    RareCount = game.RareCount,
                    UltraRareCount = game.UltraRareCount,
                    CollectionScore = game.CollectionScore,
                    PrestigeScore = game.PrestigeScore,
                    CollectionScoreTotal = game.CollectionScoreTotal,
                    PrestigeScoreTotal = game.PrestigeScoreTotal,
                    Points = game.Points,
                    TotalCommonPossible = game.TotalCommonPossible,
                    TotalUncommonPossible = game.TotalUncommonPossible,
                    TotalRarePossible = game.TotalRarePossible,
                    TotalUltraRarePossible = game.TotalUltraRarePossible,
                    TrophyPlatinumCount = game.TrophyPlatinumCount,
                    TrophyGoldCount = game.TrophyGoldCount,
                    TrophySilverCount = game.TrophySilverCount,
                    TrophyBronzeCount = game.TrophyBronzeCount,
                    TrophyPlatinumTotal = game.TrophyPlatinumTotal,
                    TrophyGoldTotal = game.TrophyGoldTotal,
                    TrophySilverTotal = game.TrophySilverTotal,
                    TrophyBronzeTotal = game.TrophyBronzeTotal,
                    LastPlayed = presentation.LastPlayed,
                    LastUnlockUtc = game.LastUnlockUtc,
                    IsCompleted = game.IsCompleted,
                    Provider = providerName,
                    ProviderKey = providerKey,
                    ProviderIconKey = providerMetadata.iconKey,
                    ProviderColorHex = providerMetadata.colorHex
                });

                snapshot.TotalAchievements += game.TotalAchievements;
                snapshot.TotalUnlocked += game.UnlockedAchievements;
                snapshot.TotalCommon += game.CommonCount;
                snapshot.TotalUncommon += game.UncommonCount;
                snapshot.TotalRare += game.RareCount;
                snapshot.TotalUltraRare += game.UltraRareCount;
                snapshot.CollectorScore = AddClamped(snapshot.CollectorScore, game.CollectionScore);
                snapshot.PrestigeScore = AddClamped(snapshot.PrestigeScore, game.PrestigeScore);
                snapshot.TotalCommonPossible += game.TotalCommonPossible;
                snapshot.TotalUncommonPossible += game.TotalUncommonPossible;
                snapshot.TotalRarePossible += game.TotalRarePossible;
                snapshot.TotalUltraRarePossible += game.TotalUltraRarePossible;
                if (game.IsCompleted)
                {
                    snapshot.CompletedGames++;
                }

                if (!snapshot.UnlockedByProvider.ContainsKey(providerKey))
                {
                    snapshot.UnlockedByProvider[providerKey] = 0;
                    snapshot.TotalByProvider[providerKey] = 0;
                }

                snapshot.UnlockedByProvider[providerKey] += game.UnlockedAchievements;
                snapshot.TotalByProvider[providerKey] += game.TotalAchievements;
            }

            snapshot.RecentAchievements = MaterializeRecentAchievements(
                settings,
                recentUnlocks,
                presentationByGameId,
                cancel);

            snapshot.GameSummaries = snapshot.GameSummaries
                .OrderByDescending(g => g.LastPlayed ?? DateTime.MinValue)
                .ToList();
            snapshot.TotalGames = snapshot.GameSummaries.Count;
            snapshot.TotalLocked = Math.Max(0, snapshot.TotalAchievements - snapshot.TotalUnlocked);
            snapshot.GlobalProgressionPercent = snapshot.TotalAchievements > 0
                ? (double)snapshot.TotalUnlocked / snapshot.TotalAchievements * 100
                : 0;
            ApplyScoreSnapshotFromValues(snapshot, snapshot.CollectorScore, snapshot.PrestigeScore);

            return snapshot;
        }

        private static void ApplyScoreSnapshotFromValues(
            OverviewDataSnapshot snapshot,
            int collectionScore,
            int prestigeScore)
        {
            if (snapshot == null)
            {
                return;
            }

            ApplyScoreSnapshot(snapshot, AchievementScoreCalculator.CreateModernScoreSnapshot(
                collectionScore,
                prestigeScore));
        }

        private static void ApplyScoreSnapshot(OverviewDataSnapshot snapshot, AchievementScoreSnapshot scoreSnapshot)
        {
            if (snapshot == null || scoreSnapshot == null)
            {
                return;
            }

            snapshot.CollectorScore = scoreSnapshot.CollectorScore;
            snapshot.CollectorLevel = GetDisplayLevel(scoreSnapshot.CollectorLevel);
            snapshot.CollectorLevelProgress = scoreSnapshot.CollectorLevel?.LevelProgress ?? 0;
            snapshot.CollectorRank = scoreSnapshot.CollectorLevel?.Rank ?? "Bronze5";

            snapshot.PrestigeScore = scoreSnapshot.PrestigeScore;
            snapshot.PrestigeLevel = GetDisplayLevel(scoreSnapshot.PrestigeLevel);
            snapshot.PrestigeLevelProgress = scoreSnapshot.PrestigeLevel?.LevelProgress ?? 0;
            snapshot.PrestigeRank = scoreSnapshot.PrestigeLevel?.Rank ?? "Bronze5";
        }

        private static int GetDisplayLevel(AchievementLevelSnapshot snapshot)
        {
            if (snapshot == null)
            {
                return 0;
            }

            return snapshot.DisplayLevel > 0 ? snapshot.DisplayLevel : snapshot.Level;
        }

        public OverviewGameFragment BuildGameFragment(
            PlayniteAchievementsSettings settings,
            ISet<string> revealedKeys,
            GameAchievementData gameData,
            IReadOnlyDictionary<string, (string iconKey, string colorHex)> providerLookup = null,
            bool includeAchievementItems = true)
        {
            settings ??= new PlayniteAchievementsSettings();
            revealedKeys ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (gameData?.ExcludedFromSummaries == true)
            {
                return null;
            }

            if (gameData?.Achievements == null || !gameData.HasAchievements || gameData.Achievements.Count == 0)
            {
                return null;
            }

            var playniteGame = gameData.Game;
            if (playniteGame == null && gameData.PlayniteGameId.HasValue)
            {
                playniteGame = _playniteApi?.Database?.Games?.Get(gameData.PlayniteGameId.Value);
            }

            var providerKey = gameData.EffectiveProviderKey;
            providerKey = string.IsNullOrWhiteSpace(providerKey) ? "Unknown" : providerKey;
            var providerName = ProviderRegistry.GetLocalizedName(providerKey);
            if (string.IsNullOrWhiteSpace(providerName))
            {
                providerName = providerKey;
            }

            var presentation = CreateGamePresentation(playniteGame);
            var gameIconPath = presentation.IconPath;
            var gameCoverPath = presentation.CoverPath;

            var fragment = new OverviewGameFragment
            {
                CacheKey = gameData.PlayniteGameId?.ToString(),
                PlayniteGameId = gameData.PlayniteGameId,
                ProviderKey = providerKey,
                ProviderName = providerName
            };

            var achievements = gameData.Achievements;
            var appearanceSettings = AchievementDisplayItem.CreateAppearanceSettingsSnapshot(
                settings,
                gameData.PlayniteGameId,
                gameData.UseSeparateLockedIconsWhenAvailable);
            var stats = new AchievementGameStats();

            for (var i = 0; i < achievements.Count; i++)
            {
                var ach = achievements[i];
                if (ach == null)
                {
                    continue;
                }

                AchievementStatsAccumulator.Add(stats, ach);

                if (includeAchievementItems)
                {
                    var displayItem = AchievementDisplayItem.Create(
                        gameData,
                        ach,
                        settings,
                        revealedKeys,
                        gameData.PlayniteGameId,
                        appearanceSettings);
                    if (displayItem != null)
                    {
                        fragment.Achievements.Add(displayItem);
                    }
                }

                if (ach.Unlocked)
                {
                    if (ach.UnlockTimeUtc.HasValue)
                    {
                        if (gameData.PlayniteGameId.HasValue)
                        {
                            var recentItem = AchievementDisplayItem.CreateRecent(
                                gameData,
                                ach,
                                settings,
                                gameIconPath,
                                gameCoverPath,
                                appearanceSettings);
                            if (recentItem != null)
                            {
                                fragment.RecentAchievements.Add(recentItem);
                            }
                        }
                    }
                }
            }

            fragment.TotalAchievements = stats.TotalAchievements;
            fragment.UnlockedAchievements = stats.UnlockedAchievements;
            fragment.CommonCount = stats.CommonCount;
            fragment.UncommonCount = stats.UncommonCount;
            fragment.RareCount = stats.RareCount;
            fragment.UltraRareCount = stats.UltraRareCount;

            fragment.TrophyPlatinumCount = stats.TrophyPlatinumCount;
            fragment.TrophyGoldCount = stats.TrophyGoldCount;
            fragment.TrophySilverCount = stats.TrophySilverCount;
            fragment.TrophyBronzeCount = stats.TrophyBronzeCount;
            fragment.TrophyPlatinumTotal = stats.TrophyPlatinumTotal;
            fragment.TrophyGoldTotal = stats.TrophyGoldTotal;
            fragment.TrophySilverTotal = stats.TrophySilverTotal;
            fragment.TrophyBronzeTotal = stats.TrophyBronzeTotal;
            fragment.CollectionScore = stats.CollectionScore;
            fragment.PrestigeScore = stats.PrestigeScore;
            fragment.CollectionScoreTotal = stats.CollectionScoreTotal;
            fragment.PrestigeScoreTotal = stats.PrestigeScoreTotal;

            fragment.TotalCommonPossible = stats.TotalCommonPossible;
            fragment.TotalUncommonPossible = stats.TotalUncommonPossible;
            fragment.TotalRarePossible = stats.TotalRarePossible;
            fragment.TotalUltraRarePossible = stats.TotalUltraRarePossible;
            foreach (var kvp in stats.UnlockCountsByDate)
            {
                fragment.UnlockCountsByDate[kvp.Key] = kvp.Value;
            }

            fragment.IsCompleted = gameData.IsCompleted;

            // GameSummaryItem projection is owned by the shared, Overview-independent builder so
            // every surface produces an identical row. The fragment keeps its own aggregates above
            // for the Overview-only cross-game rollups, per-date counts, and recent achievements.
            fragment.GameSummary = _summaryBuilder.Build(gameData, settings);

            return fragment;
        }

        private List<AchievementDisplayItem> MaterializeRecentAchievements(
            PlayniteAchievementsSettings settings,
            IEnumerable<CachedRecentUnlockData> recentAchievements,
            Dictionary<Guid, GamePresentation> presentationByGameId,
            CancellationToken cancel)
        {
            var items = new List<AchievementDisplayItem>();
            if (recentAchievements == null)
            {
                return items;
            }

            presentationByGameId ??= new Dictionary<Guid, GamePresentation>();

            var recentGameDataByKey = new Dictionary<string, GameAchievementData>(StringComparer.OrdinalIgnoreCase);
            var appearanceByGameKey = new Dictionary<string, AchievementDisplayItem.AppearanceSettingsSnapshot>(StringComparer.OrdinalIgnoreCase);
            foreach (var recent in recentAchievements)
            {
                cancel.ThrowIfCancellationRequested();

                if (recent == null || !recent.UnlockTimeUtc.HasValue)
                {
                    continue;
                }

                var presentation = ResolveGamePresentation(recent.PlayniteGameId, presentationByGameId);
                var gameKey = BuildRecentGameKey(recent);
                if (!recentGameDataByKey.TryGetValue(gameKey, out var gameData))
                {
                    gameData = new GameAchievementData
                    {
                        ProviderKey = recent.ProviderKey,
                        ProviderPlatformKey = recent.ProviderPlatformKey,
                        GameName = recent.GameName,
                        AppId = recent.AppId,
                        ProviderGameKey = recent.ProviderGameKey,
                        PlayniteGameId = recent.PlayniteGameId,
                        Game = presentation.Game,
                        HasAchievements = true,
                        UseSeparateLockedIconsWhenAvailable = recent.UseSeparateLockedIconsWhenAvailable,
                        Achievements = new List<AchievementDetail>()
                    };
                    AttachCategoryMetadata(gameData, settings);
                    recentGameDataByKey[gameKey] = gameData;

                    appearanceByGameKey[gameKey] = AchievementDisplayItem.CreateAppearanceSettingsSnapshot(
                        settings,
                        gameData.PlayniteGameId,
                        gameData.UseSeparateLockedIconsWhenAvailable);
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
                    CategoryType = recent.CategoryType,
                    TrophyType = recent.TrophyType,
                    Hidden = recent.Hidden,
                    IsCapstone = recent.IsCapstone,
                    AchievementNote = recent.AchievementNote,
                    ProviderKey = recent.ProviderKey,
                    GlobalPercentUnlocked = recent.GlobalPercentUnlocked,
                    Rarity = recent.Rarity,
                    Unlocked = true,
                    UnlockTimeUtc = recent.UnlockTimeUtc,
                    ProgressNum = recent.ProgressNum,
                    ProgressDenom = recent.ProgressDenom
                };

                var item = AchievementDisplayItem.CreateRecent(
                    gameData,
                    detail,
                    settings,
                    presentation.IconPath,
                    presentation.CoverPath,
                    appearanceByGameKey.TryGetValue(gameKey, out var appearance)
                        ? appearance
                        : null);
                if (item != null)
                {
                    items.Add(item);
                }
            }

            return AchievementSortHelper.CreateDefaultSortedList(
                items,
                AchievementSortScope.RecentAchievements);
        }

        private Dictionary<Guid, GamePresentation> BuildGamePresentationCache(IEnumerable<Guid> playniteGameIds)
        {
            var cache = new Dictionary<Guid, GamePresentation>();
            var ids = new HashSet<Guid>(
                (playniteGameIds ?? Enumerable.Empty<Guid>())
                    .Where(id => id != Guid.Empty));
            if (ids.Count == 0 || _playniteApi?.Database?.Games == null)
            {
                return cache;
            }

            foreach (var playniteGameId in ids)
            {
                var playniteGame = _playniteApi.Database.Games.Get(playniteGameId);
                if (playniteGame == null)
                {
                    continue;
                }

                cache[playniteGameId] = CreateGamePresentation(playniteGame);
            }

            return cache;
        }

        private Dictionary<string, (string iconKey, string colorHex)> BuildProviderLookup()
        {
            var lookup = new Dictionary<string, (string iconKey, string colorHex)>(StringComparer.OrdinalIgnoreCase);
            if (_providers != null)
            {
                foreach (var provider in _providers)
                {
                    if (provider == null || string.IsNullOrWhiteSpace(provider.ProviderKey))
                    {
                        continue;
                    }

                    lookup[provider.ProviderKey] = (provider.ProviderIconKey, provider.ProviderColorHex);
                }
            }

            return lookup;
        }

        private GamePresentation ResolveGamePresentation(
            Guid? playniteGameId,
            Dictionary<Guid, GamePresentation> cache)
        {
            if (!playniteGameId.HasValue)
            {
                return new GamePresentation();
            }

            if (cache.TryGetValue(playniteGameId.Value, out var cached))
            {
                return cached;
            }

            var playniteGame = _playniteApi?.Database?.Games?.Get(playniteGameId.Value);
            var presentation = CreateGamePresentation(playniteGame);
            cache[playniteGameId.Value] = presentation;
            return presentation;
        }

        private GamePresentation CreateGamePresentation(Playnite.SDK.Models.Game playniteGame)
        {
            return new GamePresentation
            {
                Game = playniteGame,
                DisplayName = playniteGame?.Name,
                SortingName = playniteGame?.SortingName,
                IconPath = !string.IsNullOrEmpty(playniteGame?.Icon)
                    ? ResolveGameAssetPath(playniteGame.Icon)
                    : null,
                CoverPath = !string.IsNullOrEmpty(playniteGame?.CoverImage)
                    ? ResolveGameAssetPath(playniteGame.CoverImage)
                    : null,
                LastPlayed = playniteGame?.LastActivity,
                PlatformText = PlayniteGameMetadataFormatter.GetPlatformText(playniteGame),
                Platforms = PlayniteGameMetadataFormatter.GetPlatformNames(playniteGame),
                RegionText = PlayniteGameMetadataFormatter.GetRegionText(playniteGame),
                PlaytimeSeconds = playniteGame?.Playtime ?? 0
            };
        }

        private static string ResolveEffectiveProviderKey(string providerKey, string providerPlatformKey)
        {
            var resolved = !string.IsNullOrWhiteSpace(providerPlatformKey)
                ? providerPlatformKey
                : providerKey;
            return string.IsNullOrWhiteSpace(resolved) ? "Unknown" : resolved.Trim();
        }

        private static string BuildRecentGameKey(CachedRecentUnlockData recent)
        {
            if (recent?.PlayniteGameId.HasValue == true)
            {
                return recent.PlayniteGameId.Value.ToString("D");
            }

            if (!string.IsNullOrWhiteSpace(recent?.CacheKey))
            {
                return recent.CacheKey.Trim();
            }

            if (!string.IsNullOrWhiteSpace(recent?.ProviderGameKey))
            {
                return $"{recent.ProviderKey ?? "Unknown"}::{recent.ProviderGameKey.Trim()}";
            }

            return $"{recent?.ProviderKey ?? "Unknown"}::{recent?.GameName ?? "Unknown"}";
        }

        private static void AttachCategoryMetadata(
            GameAchievementData gameData,
            PlayniteAchievementsSettings settings)
        {
            if (gameData?.PlayniteGameId == null)
            {
                return;
            }

            var resolved = GameCustomDataLookup.ResolveGameCustomData(
                gameData.PlayniteGameId.Value,
                settings?.Persisted);
            gameData.AchievementCategoryOrder = resolved.AchievementCategoryOrder != null && resolved.AchievementCategoryOrder.Count > 0
                ? new List<string>(resolved.AchievementCategoryOrder)
                : null;
            gameData.AchievementCategoryImageOverrides = CloneCategoryImageOverrideMap(resolved.AchievementCategoryImageOverrides);
        }

        private static Dictionary<string, CategoryImageOverrideData> CloneCategoryImageOverrideMap(
            IReadOnlyDictionary<string, CategoryImageOverrideData> source)
        {
            if (source == null || source.Count == 0)
            {
                return null;
            }

            var result = new Dictionary<string, CategoryImageOverrideData>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in source)
            {
                var category = AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(pair.Key);
                if (string.IsNullOrWhiteSpace(category) || pair.Value == null)
                {
                    continue;
                }

                result[category] = pair.Value.Clone();
            }

            return result.Count > 0 ? result : null;
        }

        private string ResolveGameAssetPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            return _playniteApi?.Database?.GetFullFilePath(path) ?? path;
        }

        private static Dictionary<DateTime, int> CloneCounts(IDictionary<DateTime, int> source)
        {
            return source != null
                ? new Dictionary<DateTime, int>(source)
                : new Dictionary<DateTime, int>();
        }

        private static Dictionary<Guid, Dictionary<DateTime, int>> CloneCountsByGame(
            IDictionary<Guid, Dictionary<DateTime, int>> source)
        {
            var result = new Dictionary<Guid, Dictionary<DateTime, int>>();
            if (source == null)
            {
                return result;
            }

            foreach (var kvp in source)
            {
                result[kvp.Key] = CloneCounts(kvp.Value);
            }

            return result;
        }

        private static int AddClamped(int current, int value)
        {
            if (value <= 0)
            {
                return current;
            }

            if (current > int.MaxValue - value)
            {
                return int.MaxValue;
            }

            return current + value;
        }
    }
}

