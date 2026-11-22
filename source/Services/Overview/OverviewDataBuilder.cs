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
using PlayniteAchievements.Services.Database;
using PlayniteAchievements.Services.GameCustomData;
using PlayniteAchievements.Services.Summaries;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.ViewModels.Items;
using PlayniteAchievements.ViewModels.Showcase.Widgets;
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

            public bool IsFavorite { get; set; }

            public Playnite.SDK.Models.Game Game { get; set; }
        }

        private readonly AchievementDataService _achievementDataService;
        private readonly IPlayniteAPI _playniteApi;
        private readonly ILogger _logger;
        private readonly Func<List<Models.Friends.FriendIdentity>> _currentUserIdentityLoader;

        /// <param name="providers">
        /// Registered providers, accepted for call-site symmetry with the other library services.
        /// Provider visuals resolve by display provider key through <see cref="ProviderRegistry"/>,
        /// which also covers the custom keys no registered provider owns.
        /// </param>
        public OverviewDataBuilder(
            AchievementDataService achievementDataService,
            IReadOnlyList<IDataProvider> providers,
            IPlayniteAPI playniteApi,
            ILogger logger,
            Func<List<Models.Friends.FriendIdentity>> currentUserIdentityLoader = null)
        {
            _achievementDataService = achievementDataService ?? throw new ArgumentNullException(nameof(achievementDataService));
            _playniteApi = playniteApi;
            _logger = logger;
            _currentUserIdentityLoader = currentUserIdentityLoader;
        }

        public OverviewDataSnapshot Build(
            PlayniteAchievementsSettings settings,
            CancellationToken cancel)
        {
            settings ??= new PlayniteAchievementsSettings();

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
                return BuildFromCachedSummaryData(settings, queryData, cancel);
            }
        }

        private OverviewDataSnapshot BuildFromCachedSummaryData(
            PlayniteAchievementsSettings settings,
            CachedSummaryData queryData,
            CancellationToken cancel)
        {
            settings ??= new PlayniteAchievementsSettings();
            queryData ??= new CachedSummaryData();

            // Memo for this pass only: the registry resolves provider color overrides and custom
            // provider definitions live, so a longer-lived cache would serve pre-edit visuals.
            var providerVisuals = new Dictionary<string, (string iconKey, string colorHex)>(
                StringComparer.OrdinalIgnoreCase);
            var providerNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            var snapshot = new OverviewDataSnapshot
            {
                GameSummaries = new List<GameSummaryItem>(),
                Unlocks = queryData.Unlocks ?? new List<UnlockFact>(),
                GlobalUnlockCountsByDate = queryData.GlobalUnlockCountsByDate ?? new Dictionary<DateTime, int>(),
                UnlockCountsByDateByGame = queryData.UnlockCountsByDateByGame ?? new Dictionary<Guid, Dictionary<DateTime, int>>(),
                UnlockedByProvider = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                TotalByProvider = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
            };

            try
            {
                snapshot.CurrentUserIdentities =
                    _currentUserIdentityLoader?.Invoke() ?? new List<Models.Friends.FriendIdentity>();
            }
            catch (Exception ex)
            {
                _logger?.Debug($"[Overview] Failed to load current-user identities: {ex.Message}");
            }

            var games = queryData.Games ?? new List<CachedGameSummaryData>();
            var referencedGameIds = games
                .Where(g => g?.PlayniteGameId.HasValue == true)
                .Select(g => g.PlayniteGameId.Value);
            // Split so a capture says which phase of a whole-library build the time is in. The
            // build was measured at 1499ms for 500 games and 4982 achievements, under one tag.
            Dictionary<Guid, GamePresentation> presentationByGameId;
            using (var presentationScope = PerfScope.Start(
                _logger, "Overview.Build.Presentation", thresholdMs: 25))
            {
                presentationByGameId = BuildGamePresentationCache(referencedGameIds);
                presentationScope?.SetContext("games=" + presentationByGameId.Count);
            }

            // Disposed explicitly below rather than with a using, so the scope covers the loop
            // and not the phases that follow it.
            var gameRowScope = PerfScope.Start(_logger, "Overview.Build.GameRows", thresholdMs: 25);
            var platformScores = new PlatformScoreTotals();
            gameRowScope?.SetContext("games=" + games.Count);

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
                var pointsFamily = PointsFamilies.FromProviderKey(providerKey);

                // Memoized with the visuals below rather than resolved per game: there are a
                // few dozen provider keys and a library's worth of games, and the name is a
                // localized resource lookup.
                if (!providerNames.TryGetValue(providerKey, out var providerName))
                {
                    providerName = ProviderRegistry.GetLocalizedName(providerKey);
                    if (string.IsNullOrWhiteSpace(providerName))
                    {
                        providerName = providerKey;
                    }

                    providerNames[providerKey] = providerName;
                }

                if (!providerVisuals.TryGetValue(providerKey, out var providerMetadata))
                {
                    providerMetadata = ProviderRegistry.ResolveProviderVisualsOrFallback(providerKey);
                    providerVisuals[providerKey] = providerMetadata;
                }

                var presentation = ResolveGamePresentation(game.PlayniteGameId, presentationByGameId);
                var summaryArt = GameSummaryArtResolver.ResolveForGame(game.PlayniteGameId);
                snapshot.GameSummaries.Add(new GameSummaryItem
                {
                    GameName = presentation.DisplayName ?? game.GameName ?? ResourceProvider.GetString("LOCPlayAch_Common_Unknown"),
                    SortingName = presentation.SortingName ?? presentation.DisplayName ?? game.GameName ?? "Unknown",
                    GameLogo = summaryArt ?? presentation.IconPath,
                    GameCoverPath = summaryArt ?? presentation.CoverPath,
                    IsFavorite = presentation.IsFavorite,
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
                    ShowsRarity = game.RaritySource != RaritySource.None,
                    CollectionScore = game.CollectionScore,
                    PrestigeScore = game.PrestigeScore,
                    CollectionScoreTotal = game.CollectionScoreTotal,
                    PrestigeScoreTotal = game.PrestigeScoreTotal,
                    Points = game.Points,
                    PlatformScorePoints = game.PlatformScorePoints,
                    PlatformScorePointsTotal = game.PlatformScorePointsTotal,
                    PointsFamily = pointsFamily,
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
                    Tallies = game.Tallies,
                    LastPlayed = presentation.LastPlayed,
                    Added = presentation.Game?.Added,
                    ReleaseDate = presentation.Game?.ReleaseDate?.Date,
                    LastUnlockUtc = game.LastUnlockUtc,
                    LastCapstoneUnlockUtc = game.LastCapstoneUnlockUtc,
                    FirstPlatinumUnlockUtc = game.FirstPlatinumUnlockUtc,
                    IsCompleted = game.IsCompleted,
                    CapstoneTotal = game.CapstoneTotal,
                    CapstoneUnlocked = game.CapstoneUnlocked,
                    CapstonesMatchPlatinums = game.CapstonesMatchPlatinums,
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
                platformScores.AddToFamily(pointsFamily, game.PlatformScorePoints, game.PlatformScorePointsTotal);
                snapshot.TotalCommonPossible += game.TotalCommonPossible;
                snapshot.TotalUncommonPossible += game.TotalUncommonPossible;
                snapshot.TotalRarePossible += game.TotalRarePossible;
                snapshot.TotalUltraRarePossible += game.TotalUltraRarePossible;
                if (game.IsCompleted)
                {
                    snapshot.CompletedGames++;
                }

                // A game with capstones contributes one completion per capstone earned; one
                // without any contributes one for a clean 100%, so a platform that names no
                // finish line still counts for something.
                snapshot.Completions += game.CapstoneTotal > 0
                    ? game.CapstoneUnlocked
                    : (game.IsCompleted ? 1 : 0);
                snapshot.PossibleCompletions += game.CapstoneTotal > 0
                    ? game.CapstoneTotal
                    : (game.TotalAchievements > 0 ? 1 : 0);

                if (!snapshot.UnlockedByProvider.ContainsKey(providerKey))
                {
                    snapshot.UnlockedByProvider[providerKey] = 0;
                    snapshot.TotalByProvider[providerKey] = 0;
                }

                snapshot.UnlockedByProvider[providerKey] += game.UnlockedAchievements;
                snapshot.TotalByProvider[providerKey] += game.TotalAchievements;
            }

            gameRowScope?.Dispose();

            // Every pin's row, read here so no widget reads the database on the UI thread.
            var pins = OverviewDataSnapshot.Pins(settings.Persisted?.Showcase);
            snapshot.PinKeysAtBuild = OverviewDataSnapshot.PinKeys(settings.Persisted?.Showcase);
            try
            {
                snapshot.PinnedRows = pins.Count == 0 ? snapshot.PinnedRows : LoadRows(settings,
                    AchievementQuery.CurrentUser.ForAchievements(pins.Select(pin => new KeyValuePair<Guid, string>(pin.GameId, pin.ApiName))));
            }
            catch (Exception ex)
            {
                _logger?.Debug($"[Overview] Failed to read the pinned achievements: {ex.Message}");
            }

            var unlockNextGames = UnlockNextCandidateSelector.SelectGames(snapshot.GameSummaries)
                .Select(game => game.PlayniteGameId.Value)
                .ToList();
            snapshot.UnlockNextReader = slice => LoadUnlockNextCandidates(settings, unlockNextGames, slice);

            snapshot.GameSummaries = snapshot.GameSummaries
                .OrderByDescending(g => g.LastPlayed ?? DateTime.MinValue)
                .ToList();
            snapshot.TotalGames = snapshot.GameSummaries.Count;
            snapshot.TotalLocked = Math.Max(0, snapshot.TotalAchievements - snapshot.TotalUnlocked);
            snapshot.ApplyTrophyTotals(snapshot.GameSummaries);
            snapshot.GlobalProgressionPercent = snapshot.TotalAchievements > 0
                ? (double)snapshot.TotalUnlocked / snapshot.TotalAchievements * 100
                : 0;
            ApplyScoreSnapshotFromValues(snapshot, snapshot.CollectorScore, snapshot.PrestigeScore);
            snapshot.ApplyPlatformScores(platformScores);
            ScoreCardChoices.PublishFamiliesWithPoints(platformScores);

            Common.LeakWatch.Track("OverviewSnapshot.full", snapshot);
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

        // The bounded pool of locked rows Unlock Next draws from, read when a shown widget asks
        // for it: a superset of every widget's criterion and window, since editing those options
        // re-projects from the snapshot. The keys each candidate game keeps are chosen in SQL, and
        // only their rows are read as display rows.
        private List<AchievementDisplayItem> LoadUnlockNextCandidates(
            PlayniteAchievementsSettings settings,
            IReadOnlyList<Guid> games,
            int slice)
        {
            var reads = _achievementDataService.CacheReads;
            if (reads == null || games.Count == 0)
            {
                return new List<AchievementDisplayItem>();
            }

            using (PerfScope.Start(_logger, "Overview.UnlockNext", thresholdMs: 25))
            {
                try
                {
                    var query = AchievementQuery.CurrentUser.WithSummaryRules().ForGames(games).WhereUnlocked(false).OrderBy(AchievementSort.Display);
                    return LoadRows(settings, query, reads.Reads.SlicedKeys(query, slice));
                }
                catch (Exception ex)
                {
                    _logger?.Debug($"[Overview] Failed to load Unlock Next candidates: {ex.Message}");
                    return new List<AchievementDisplayItem>();
                }
            }
        }

        /// <summary>
        /// The display rows of <paramref name="keys"/>, or of every row <paramref name="query"/>
        /// matches, in its order. The read names an authored row's provider as its game's.
        /// </summary>
        internal List<AchievementDisplayItem> LoadRows(
            PlayniteAchievementsSettings settings,
            AchievementQuery query,
            IReadOnlyList<AchievementQuerySql.RowKey> keys = null)
        {
            var reads = _achievementDataService.CacheReads;
            if (reads == null)
            {
                return new List<AchievementDisplayItem>();
            }

            var rows = reads.LoadRows(query, keys ?? reads.Reads.SortedKeys(query));
            var separateLockedIcons = new Dictionary<Guid, bool>();
            foreach (var row in rows)
            {
                var gameId = row.PlayniteGameId ?? Guid.Empty;
                if (!separateLockedIcons.TryGetValue(gameId, out var separate))
                {
                    separate = GameCustomDataLookup.ShouldUseSeparateLockedIcons(gameId, settings?.Persisted);
                    separateLockedIcons[gameId] = separate;
                }

                row.UseSeparateLockedIconsWhenAvailable = separate;
            }

            return BuildRows(settings, rows);
        }

        /// <summary>Display rows for <paramref name="rows"/>, named and illustrated as Playnite shows their games.</summary>
        internal List<AchievementDisplayItem> BuildRows(PlayniteAchievementsSettings settings, IReadOnlyList<CachedRecentUnlockData> rows) =>
            MaterializeAchievements(
                settings ?? new PlayniteAchievementsSettings(),
                rows,
                BuildGamePresentationCache(rows.Where(row => row?.PlayniteGameId != null).Select(row => row.PlayniteGameId.Value)),
                CancellationToken.None);

        private List<AchievementDisplayItem> MaterializeAchievements(
            PlayniteAchievementsSettings settings,
            IEnumerable<CachedRecentUnlockData> achievements,
            Dictionary<Guid, GamePresentation> presentationByGameId,
            CancellationToken cancel)
        {
            var items = new List<AchievementDisplayItem>();
            if (achievements == null)
            {
                return items;
            }

            presentationByGameId ??= new Dictionary<Guid, GamePresentation>();

            var gameDataByKey = new Dictionary<string, GameAchievementData>(StringComparer.OrdinalIgnoreCase);
            var appearanceByGameKey = new Dictionary<string, AchievementDisplayItem.AppearanceSettingsSnapshot>(StringComparer.OrdinalIgnoreCase);
            // One memo per game, matching the memo's documented single-game scope. Recent unlocks
            // cluster by game, so without it every unlock in the library repeats its game's
            // category art resolution and disk probing.
            var categoryMemoByGameKey = new Dictionary<string, AchievementDisplayItem.CategoryPresentationMemo>(StringComparer.OrdinalIgnoreCase);
            foreach (var achievement in achievements)
            {
                cancel.ThrowIfCancellationRequested();

                if (achievement == null)
                {
                    continue;
                }

                var presentation = ResolveGamePresentation(achievement.PlayniteGameId, presentationByGameId);
                var gameKey = BuildAchievementGameKey(achievement);
                if (!gameDataByKey.TryGetValue(gameKey, out var gameData))
                {
                    gameData = new GameAchievementData
                    {
                        ProviderKey = achievement.ProviderKey,
                        ProviderPlatformKey = achievement.ProviderPlatformKey,
                        GameName = presentation.DisplayName ?? achievement.GameName,
                        AppId = achievement.AppId,
                        ProviderGameKey = achievement.ProviderGameKey,
                        PlayniteGameId = achievement.PlayniteGameId,
                        Game = presentation.Game,
                        HasAchievements = true,
                        UseSeparateLockedIconsWhenAvailable = achievement.UseSeparateLockedIconsWhenAvailable,
                        Achievements = new List<AchievementDetail>()
                    };
                    AttachCategoryMetadata(gameData, settings);
                    gameDataByKey[gameKey] = gameData;

                    appearanceByGameKey[gameKey] = AchievementDisplayItem.CreateAppearanceSettingsSnapshot(
                        settings,
                        gameData.PlayniteGameId,
                        gameData.UseSeparateLockedIconsWhenAvailable);
                    categoryMemoByGameKey[gameKey] = new AchievementDisplayItem.CategoryPresentationMemo();
                }

                var detail = new AchievementDetail
                {
                    ApiName = achievement.ApiName,
                    DisplayName = achievement.DisplayName,
                    Description = achievement.Description,
                    UnlockedIconPath = achievement.UnlockedIconPath,
                    LockedIconPath = achievement.LockedIconPath,
                    Points = achievement.Points,
                    ScaledPoints = achievement.ScaledPoints,
                    Category = achievement.Category,
                    ProviderCategory = achievement.ProviderCategory,
                    Tags = achievement.Tags,
                    CategoryKind = achievement.CategoryKind,
                    TrophyType = achievement.TrophyType,
                    Hidden = achievement.Hidden,
                    IsCapstone = achievement.IsCapstone,
                    AchievementNote = achievement.AchievementNote,
                    ProviderKey = achievement.ProviderKey,
                    GlobalPercentUnlocked = achievement.GlobalPercentUnlocked,
                    Rarity = achievement.Rarity,
                    Unlocked = achievement.Unlocked,
                    UnlockTimeUtc = achievement.UnlockTimeUtc,
                    ProgressNum = achievement.ProgressNum,
                    ProgressDenom = achievement.ProgressDenom
                };

                var item = AchievementDisplayItem.Create(
                    gameData,
                    detail,
                    settings,
                    playniteGameIdOverride: gameData.PlayniteGameId,
                    appearanceSettings: appearanceByGameKey.TryGetValue(gameKey, out var appearance)
                        ? appearance
                        : null,
                    categoryMemo: categoryMemoByGameKey.TryGetValue(gameKey, out var categoryMemo)
                        ? categoryMemo
                        : null);
                if (item != null)
                {
                    item.GameIconPath = presentation.IconPath;
                    item.GameCoverPath = presentation.CoverPath;
                    items.Add(item);
                }
            }

            return items;
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
                PlaytimeSeconds = playniteGame?.Playtime ?? 0,
                IsFavorite = playniteGame?.Favorite == true
            };
        }

        private static string ResolveEffectiveProviderKey(string providerKey, string providerPlatformKey)
        {
            var resolved = !string.IsNullOrWhiteSpace(providerPlatformKey)
                ? providerPlatformKey
                : providerKey;
            return string.IsNullOrWhiteSpace(resolved) ? "Unknown" : CustomProviders.CustomProviderKeys.ToDisplayKey(resolved.Trim());
        }

        private static string BuildAchievementGameKey(CachedRecentUnlockData recent)
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

            // Two fields, read as two fields. Resolving the whole record deep-cloned it and
            // rebuilt roughly ten collections off it, and this then copied both of the two it
            // wanted a second time. That ran once per distinct game in the materialization, so
            // a library where every game is customized paid all of it per game.
            GameCustomDataLookup.GetCategoryMetadata(
                gameData.PlayniteGameId.Value,
                out var categoryOrder,
                out var categoryImageOverrides);
            gameData.AchievementCategoryOrder = categoryOrder;
            gameData.AchievementCategoryImageOverrides = categoryImageOverrides;
        }

        private string ResolveGameAssetPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            return _playniteApi?.Database?.GetFullFilePath(path) ?? path;
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

