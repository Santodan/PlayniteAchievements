using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Services;
using PlayniteAchievements.ViewModels;
using Playnite.SDK;

namespace PlayniteAchievements.Services.Sidebar
{
    public sealed class SidebarDataBuilder
    {
        private const int InitialRecentAchievementMaterializationLimit = 250;

        private sealed class GamePresentation
        {
            public string SortingName { get; set; }

            public string IconPath { get; set; }

            public string CoverPath { get; set; }

            public DateTime? LastPlayed { get; set; }

            public string PlatformText { get; set; }

            public string RegionText { get; set; }

            public ulong PlaytimeSeconds { get; set; }

            public Playnite.SDK.Models.Game Game { get; set; }
        }

        private readonly AchievementDataService _achievementDataService;
        private readonly IReadOnlyList<IDataProvider> _providers;
        private readonly IPlayniteAPI _playniteApi;
        private readonly ILogger _logger;

        public SidebarDataBuilder(
            AchievementDataService achievementDataService,
            IReadOnlyList<IDataProvider> providers,
            IPlayniteAPI playniteApi,
            ILogger logger)
        {
            _achievementDataService = achievementDataService ?? throw new ArgumentNullException(nameof(achievementDataService));
            _providers = providers ?? new List<IDataProvider>();
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
            var gamesOverview = new List<GameOverviewItem>();
            var recentAchievements = new List<AchievementDisplayItem>();

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
            var queryData = _achievementDataService.GetCachedSummaryData(InitialRecentAchievementMaterializationLimit);
            if (queryData != null)
            {
                return BuildFromCachedSummaryData(settings, queryData, providerLookup, cancel);
            }

            var allGameData = _achievementDataService.GetAllGameAchievementDataForSidebar() ?? new List<GameAchievementData>();
            for (var i = 0; i < allGameData.Count; i++)
            {
                cancel.ThrowIfCancellationRequested();

                var fragment = BuildGameFragment(
                    settings,
                    revealedKeys,
                    allGameData[i],
                    providerLookup,
                    includeAchievementItems: false);
                if (fragment == null)
                {
                    continue;
                }

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

                var provider = string.IsNullOrWhiteSpace(fragment.ProviderKey)
                    ? "Unknown"
                    : fragment.ProviderKey;
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

            recentAchievements = AchievementGridSortHelper.CreateDefaultSortedList(
                recentAchievements,
                AchievementGridSortScope.RecentAchievements);

            snapshot.Achievements = new List<AchievementDisplayItem>();
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

        private SidebarDataSnapshot BuildFromCachedSummaryData(
            PlayniteAchievementsSettings settings,
            CachedSummaryData queryData,
            IReadOnlyDictionary<string, (string iconKey, string colorHex)> providerLookup,
            CancellationToken cancel)
        {
            settings ??= new PlayniteAchievementsSettings();
            queryData ??= new CachedSummaryData();
            providerLookup ??= BuildProviderLookup();

            var snapshot = new SidebarDataSnapshot
            {
                Achievements = new List<AchievementDisplayItem>(),
                GamesOverview = new List<GameOverviewItem>(),
                RecentAchievements = new List<AchievementDisplayItem>(),
                GlobalUnlockCountsByDate = CloneCounts(queryData.GlobalUnlockCountsByDate),
                UnlockCountsByDateByGame = CloneCountsByGame(queryData.UnlockCountsByDateByGame),
                UnlockedByProvider = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                TotalByProvider = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                HasDeferredRecentAchievements = queryData.HasMoreRecentUnlocks
            };

            var excludedSummaryIds = GameCustomDataLookup.GetExcludedSummaryGameIds(settings?.Persisted);
            RemoveExcludedTimelineCounts(
                snapshot.GlobalUnlockCountsByDate,
                snapshot.UnlockCountsByDateByGame,
                excludedSummaryIds);
            var referencedGameIds = queryData.Games
                .Where(g => g?.PlayniteGameId.HasValue == true)
                .Select(g => g.PlayniteGameId.Value)
                .Concat(queryData.RecentUnlocks
                    .Where(r => r?.PlayniteGameId.HasValue == true)
                    .Select(r => r.PlayniteGameId.Value));
            var presentationByGameId = BuildGamePresentationCache(referencedGameIds);

            for (var i = 0; i < queryData.Games.Count; i++)
            {
                cancel.ThrowIfCancellationRequested();

                var game = queryData.Games[i];
                if (game == null ||
                    !game.HasAchievements ||
                    game.TotalAchievements <= 0 ||
                    (game.PlayniteGameId.HasValue && excludedSummaryIds.Contains(game.PlayniteGameId.Value)))
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
                snapshot.GamesOverview.Add(new GameOverviewItem
                {
                    GameName = game.GameName ?? "Unknown",
                    SortingName = presentation.SortingName ?? game.GameName ?? "Unknown",
                    GameLogo = presentation.IconPath,
                    GameCoverPath = presentation.CoverPath,
                    PlatformText = presentation.PlatformText,
                    RegionText = presentation.RegionText,
                    PlaytimeSeconds = presentation.PlaytimeSeconds,
                    AppId = game.AppId,
                    PlayniteGameId = game.PlayniteGameId,
                    TotalAchievements = game.TotalAchievements,
                    UnlockedAchievements = game.UnlockedAchievements,
                    CommonCount = game.CommonCount,
                    UncommonCount = game.UncommonCount,
                    RareCount = game.RareCount,
                    UltraRareCount = game.UltraRareCount,
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
                queryData.RecentUnlocks,
                excludedSummaryIds,
                presentationByGameId,
                cancel);

            snapshot.GamesOverview = snapshot.GamesOverview
                .OrderByDescending(g => g.LastPlayed ?? DateTime.MinValue)
                .ToList();
            snapshot.TotalGames = snapshot.GamesOverview.Count;
            snapshot.TotalLocked = Math.Max(0, snapshot.TotalAchievements - snapshot.TotalUnlocked);
            snapshot.GlobalProgressionPercent = snapshot.TotalAchievements > 0
                ? (double)snapshot.TotalUnlocked / snapshot.TotalAchievements * 100
                : 0;

            return snapshot;
        }

        public List<AchievementDisplayItem> BuildDeferredRecentAchievements(
            PlayniteAchievementsSettings settings,
            CancellationToken cancel)
        {
            settings ??= new PlayniteAchievementsSettings();

            var queryData = _achievementDataService.GetCachedSummaryData() ?? new CachedSummaryData();
            var recentAchievements = queryData.RecentUnlocks ?? new List<CachedRecentUnlockData>();
            var excludedSummaryIds = GameCustomDataLookup.GetExcludedSummaryGameIds(settings?.Persisted);
            var presentationByGameId = BuildGamePresentationCache(
                recentAchievements
                    .Where(r => r?.PlayniteGameId.HasValue == true)
                    .Select(r => r.PlayniteGameId.Value));

            return MaterializeRecentAchievements(
                settings,
                recentAchievements,
                excludedSummaryIds,
                presentationByGameId,
                cancel);
        }

        public SidebarGameFragment BuildGameFragment(
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

            providerLookup ??= BuildProviderLookup();
            var providerKey = gameData.EffectiveProviderKey;

            providerKey = string.IsNullOrWhiteSpace(providerKey) ? "Unknown" : providerKey;
            var providerName = ProviderRegistry.GetLocalizedName(providerKey);
            if (string.IsNullOrWhiteSpace(providerName))
            {
                providerName = providerKey;
            }
            
            if (!providerLookup.TryGetValue(providerKey, out var providerMetadata))
            {
                // Fallback for providers without an active integration
                providerMetadata = ("ProviderIcon" + providerKey, "#888888");
            }

            var presentation = CreateGamePresentation(playniteGame);
            var gameIconPath = presentation.IconPath;
            var gameCoverPath = presentation.CoverPath;

            var fragment = new SidebarGameFragment
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

                // Calculate total rarity tier for ALL achievements (including locked)
                // Only count if rarity data is available (null means no rarity info for this provider)
                AchievementDisplayItem.AccumulateRarity(ach, ref gameTotalCommon, ref gameTotalUncommon, ref gameTotalRare, ref gameTotalUltraRare);
                AchievementDisplayItem.AccumulateTrophy(
                    ach,
                    ref gameTrophyPlatinumTotal,
                    ref gameTrophyGoldTotal,
                    ref gameTrophySilverTotal,
                    ref gameTrophyBronzeTotal);

                if (ach.Unlocked)
                {
                    gameUnlocked++;

                    // Only count rarity if data is available
                    AchievementDisplayItem.AccumulateRarity(ach, ref gameCommon, ref gameUncommon, ref gameRare, ref gameUltraRare);

                    // Track trophy types for unlocked achievements
                    AchievementDisplayItem.AccumulateTrophy(ach, ref gameTrophyPlatinum, ref gameTrophyGold, ref gameTrophySilver, ref gameTrophyBronze);

                    if (ach.UnlockTimeUtc.HasValue)
                    {
                        var unlockDate = DateTimeUtilities.AsUtcKind(ach.UnlockTimeUtc.Value).Date;
                        Increment(fragment.UnlockCountsByDate, unlockDate);

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
                SortingName = presentation.SortingName ?? gameData.GameName ?? "Unknown",
                GameLogo = gameIconPath,
                GameCoverPath = gameCoverPath,
                PlatformText = presentation.PlatformText,
                RegionText = presentation.RegionText,
                PlaytimeSeconds = presentation.PlaytimeSeconds,
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
                LastPlayed = presentation.LastPlayed,
                IsCompleted = gameData.IsCompleted,
                Provider = providerName,
                ProviderKey = providerKey,
                ProviderIconKey = providerMetadata.iconKey,
                ProviderColorHex = providerMetadata.colorHex
            };

            return fragment;
        }

        private List<AchievementDisplayItem> MaterializeRecentAchievements(
            PlayniteAchievementsSettings settings,
            IEnumerable<CachedRecentUnlockData> recentAchievements,
            ISet<Guid> excludedSummaryIds,
            Dictionary<Guid, GamePresentation> presentationByGameId,
            CancellationToken cancel)
        {
            var items = new List<AchievementDisplayItem>();
            if (recentAchievements == null)
            {
                return items;
            }

            presentationByGameId ??= new Dictionary<Guid, GamePresentation>();
            excludedSummaryIds ??= new HashSet<Guid>();

            var recentGameDataByKey = new Dictionary<string, GameAchievementData>(StringComparer.OrdinalIgnoreCase);
            var appearanceByGameKey = new Dictionary<string, AchievementDisplayItem.AppearanceSettingsSnapshot>(StringComparer.OrdinalIgnoreCase);
            foreach (var recent in recentAchievements)
            {
                cancel.ThrowIfCancellationRequested();

                if (recent == null ||
                    !recent.UnlockTimeUtc.HasValue ||
                    (recent.PlayniteGameId.HasValue && excludedSummaryIds.Contains(recent.PlayniteGameId.Value)))
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
                        PlayniteGameId = recent.PlayniteGameId,
                        Game = presentation.Game,
                        HasAchievements = true,
                        Achievements = new List<AchievementDetail>()
                    };
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

            return AchievementGridSortHelper.CreateDefaultSortedList(
                items,
                AchievementGridSortScope.RecentAchievements);
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

            foreach (var playniteGame in _playniteApi.Database.Games)
            {
                if (playniteGame == null || !ids.Contains(playniteGame.Id))
                {
                    continue;
                }

                cache[playniteGame.Id] = CreateGamePresentation(playniteGame);

                if (cache.Count >= ids.Count)
                {
                    break;
                }
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
                SortingName = playniteGame?.SortingName,
                IconPath = !string.IsNullOrEmpty(playniteGame?.Icon)
                    ? ResolveGameAssetPath(playniteGame.Icon)
                    : null,
                CoverPath = !string.IsNullOrEmpty(playniteGame?.CoverImage)
                    ? ResolveGameAssetPath(playniteGame.CoverImage)
                    : null,
                LastPlayed = playniteGame?.LastActivity,
                PlatformText = PlayniteGameMetadataFormatter.GetPlatformText(playniteGame),
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

            return $"{recent?.ProviderKey ?? "Unknown"}::{recent?.GameName ?? "Unknown"}";
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

        private static void RemoveExcludedTimelineCounts(
            IDictionary<DateTime, int> globalCounts,
            IDictionary<Guid, Dictionary<DateTime, int>> countsByGame,
            ISet<Guid> excludedSummaryIds)
        {
            if (globalCounts == null || countsByGame == null || excludedSummaryIds == null || excludedSummaryIds.Count == 0)
            {
                return;
            }

            foreach (var gameId in excludedSummaryIds)
            {
                if (!countsByGame.TryGetValue(gameId, out var excludedCounts) || excludedCounts == null)
                {
                    continue;
                }

                foreach (var kvp in excludedCounts)
                {
                    if (!globalCounts.TryGetValue(kvp.Key, out var existing))
                    {
                        continue;
                    }

                    var remaining = existing - kvp.Value;
                    if (remaining > 0)
                    {
                        globalCounts[kvp.Key] = remaining;
                    }
                    else
                    {
                        globalCounts.Remove(kvp.Key);
                    }
                }

                countsByGame.Remove(gameId);
            }
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



