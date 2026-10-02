using Playnite.SDK;
using PlayniteAchievements.Services.Database.Rows;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Cache;
using PlayniteAchievements.Services.Database;
using PlayniteAchievements.Services.GameCustomData;
using PlayniteAchievements.Services.Hydration;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;

namespace PlayniteAchievements.Services.Achievements
{
    /// <summary>
    /// Centralized read-side service for cached achievement data and hydration overlays.
    /// </summary>
    public sealed class AchievementDataService
    {
        private static readonly IReadOnlyDictionary<string, string> EmptyIconOverrides =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string> EmptyStringMap =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> OverviewProjectionAffectingSettings =
            new HashSet<string>(StringComparer.Ordinal)
            {
                nameof(PersistedSettings.UseSeparateLockedIconsWhenAvailable),
                nameof(PersistedSettings.SeparateLockedIconEnabledGameIds),
                nameof(PersistedSettings.ExcludedFromSummariesGameIds),
                nameof(PersistedSettings.ManualCapstones),
                nameof(PersistedSettings.AchievementCategoryOverrides),
                nameof(PersistedSettings.AchievementCategoryTypeOverrides)
            };

        private sealed class SummaryCustomizationData
        {
            public ResolvedGameCustomData Resolved { get; set; } = ResolvedGameCustomData.Empty;

            public IReadOnlyDictionary<string, string> UnlockedIconOverrides { get; set; } =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            public IReadOnlyDictionary<string, string> LockedIconOverrides { get; set; } =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        private readonly ICacheManager _cacheService;
        private readonly ICacheReadOptimizations _cacheReadOptimizations;
        private readonly IAchievementOverrideMirror _overrideMirror;
        private readonly GameDataHydrator _hydrator;
        private readonly ILogger _logger;
        private readonly IPlayniteAPI _api;
        private readonly GameCustomDataStore _gameCustomDataStore;
        // The settings wrapper, not its PersistedSettings: CancelEdit replaces the
        // Persisted instance, so a captured instance would keep serving the values the
        // user reverted.
        private readonly PlayniteAchievementsSettings _settings;
        private PersistedSettingsSubscription _persistedSubscription;
        private readonly object _overviewProjectionCacheSync = new object();

        /// <summary>
        /// A memoized summary plus the games whose contribution to it is known to be stale. An
        /// entry with no dirty games is served as-is; one with dirty games is patched per game on
        /// the next read rather than rebuilt. Patching lazily -- on read rather than on the
        /// change event -- collapses an editing burst into one patch and keeps the scoped SQL
        /// read off the editor's save path.
        /// </summary>
        private sealed class OverviewSummaryMemoEntry
        {
            public OverviewSummaryMemoEntry(CachedSummaryData data)
            {
                Data = data;
            }

            public CachedSummaryData Data { get; set; }

            public HashSet<Guid> DirtyGameIds { get; } = new HashSet<Guid>();
        }

        private readonly Dictionary<int, OverviewSummaryMemoEntry> _overviewSummaryCacheByLimit =
            new Dictionary<int, OverviewSummaryMemoEntry>();

        // Bumped on every invalidation; a summary loaded before an invalidation must not be
        // memoized after it (it may have been built against since-replaced filter mirror rows).
        private int _overviewProjectionGeneration;

        private PersistedSettings Persisted => _settings.Persisted;

        public AchievementDataService(
            ICacheManager cacheService,
            IPlayniteAPI api,
            PlayniteAchievementsSettings settings,
            ILogger logger,
            GameCustomDataStore gameCustomDataStore = null)
        {
            _cacheService = cacheService ?? throw new ArgumentNullException(nameof(cacheService));
            if (api == null) throw new ArgumentNullException(nameof(api));
            if (settings == null) throw new ArgumentNullException(nameof(settings));

            _logger = logger;
            _api = api;
            _gameCustomDataStore = gameCustomDataStore;
            _settings = settings;
            _cacheReadOptimizations = cacheService as ICacheReadOptimizations;
            _overrideMirror = cacheService as IAchievementOverrideMirror;
            _hydrator = new GameDataHydrator(api, settings, _gameCustomDataStore);
            SubscribeOverviewProjectionInvalidation();
        }

        public GameAchievementData GetGameAchievementData(string playniteGameId)
        {
            if (string.IsNullOrWhiteSpace(playniteGameId))
            {
                return null;
            }

            try
            {
                return GetMergedGameAchievementData(playniteGameId, includeAchievementOverlays: true);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, string.Format(
                    "Failed to get achievement data for gameId={0}",
                    playniteGameId));
                return null;
            }
        }

        public GameAchievementData GetVisibleGameAchievementData(string playniteGameId)
        {
            if (string.IsNullOrWhiteSpace(playniteGameId))
            {
                return null;
            }

            try
            {
                return ProjectVisibleGameAchievementData(
                    GetMergedGameAchievementData(playniteGameId, includeAchievementOverlays: true));
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, string.Format(
                    "Failed to get visible achievement data for gameId={0}",
                    playniteGameId));
                return null;
            }
        }

        public GameAchievementData GetRawGameAchievementData(Guid playniteGameId)
        {
            return GetRawGameAchievementData(playniteGameId.ToString());
        }

        public GameAchievementData GetRawGameAchievementData(string playniteGameId)
        {
            if (string.IsNullOrWhiteSpace(playniteGameId))
            {
                return null;
            }

            try
            {
                return _cacheService.LoadGameData(playniteGameId);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, string.Format(
                    "Failed to get achievement data for gameId={0}",
                    playniteGameId));
                return null;
            }
        }

        /// <summary>
        /// Raw cached data plus the game's custom achievement projections, with no overlays
        /// applied. A game with only custom achievements yields the synthetic custom data.
        /// </summary>
        public GameAchievementData GetRawGameAchievementDataWithCustomAchievements(Guid playniteGameId)
        {
            if (playniteGameId == Guid.Empty)
            {
                return null;
            }

            try
            {
                var data = _cacheService.LoadGameData(playniteGameId.ToString());
                if (data == null)
                {
                    return CreateSyntheticCustomGameData(playniteGameId, LoadCustomData(playniteGameId));
                }

                _hydrator.AppendCustomAchievements(data);
                return data;
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, string.Format(
                    "Failed to get achievement data for gameId={0}",
                    playniteGameId));
                return null;
            }
        }

        public List<string> GetCachedGameIds()
        {
            try
            {
                return _cacheService.GetCachedGameIds() ?? new List<string>();
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Failed to get cached game ids");
                return new List<string>();
            }
        }

        public bool HasCachedGameData()
        {
            return GetCachedGameIds().Count > 0;
        }

        public GameAchievementData GetGameAchievementData(Guid playniteGameId)
        {
            return GetGameAchievementData(playniteGameId.ToString());
        }

        public GameAchievementData GetVisibleGameAchievementData(Guid playniteGameId)
        {
            return GetVisibleGameAchievementData(playniteGameId.ToString());
        }

        public GameAchievementData GetGameAchievementDataForOverview(Guid playniteGameId)
        {
            if (playniteGameId == Guid.Empty)
            {
                return null;
            }

            try
            {
                return ProjectVisibleGameAchievementData(
                    GetMergedGameAchievementData(playniteGameId.ToString(), includeAchievementOverlays: false),
                    excludeSummaryFiltered: true);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, string.Format(
                    "Failed to get overview achievement data for gameId={0}",
                    playniteGameId));
                return null;
            }
        }

        public List<GameAchievementData> GetAllGameAchievementData()
        {
            try
            {
                var result = LoadAllCachedGameData();
                HydrateAll(result, includeAchievementOverlays: true);
                return result;
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Failed to get all achievement data");
                return new List<GameAchievementData>();
            }
        }

        public List<GameAchievementData> GetAllVisibleGameAchievementDataForOverview()
        {
            try
            {
                var result = LoadAllCachedGameData();
                HydrateAll(result, includeAchievementOverlays: false);
                return ProjectVisibleGameAchievementData(result, excludeSummaryFiltered: true);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Failed to get all visible overview achievement data");
                return new List<GameAchievementData>();
            }
        }

        public List<GameAchievementData> GetAllGameAchievementDataForTheme()
        {
            try
            {
                var allData = GetAllGameAchievementData();
                return ExcludeSummaryGames(allData);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Failed to get all theme achievement data");
                return new List<GameAchievementData>();
            }
        }

        public List<GameAchievementData> GetAllVisibleGameAchievementDataForTheme()
        {
            try
            {
                var allData = GetAllGameAchievementData();
                return ExcludeSummaryGames(ProjectVisibleGameAchievementData(allData, excludeSummaryFiltered: true));
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Failed to get all visible theme achievement data");
                return new List<GameAchievementData>();
            }
        }

        internal CachedSummaryData GetCachedSummaryData(int recentAchievementDetailLimit = 0)
        {
            try
            {
                return _cacheReadOptimizations?.LoadCachedSummaryDataFast(recentAchievementDetailLimit);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Failed to get cached summary data");
                return null;
            }
        }

        internal CachedSummaryData GetCachedSummaryDataForOverview(int recentAchievementDetailLimit = 0)
        {
            var normalizedLimit = Math.Max(0, recentAchievementDetailLimit);
            int generation;
            CachedSummaryData patchBase = null;
            List<Guid> dirtyGameIds = null;

            lock (_overviewProjectionCacheSync)
            {
                if (_overviewSummaryCacheByLimit.TryGetValue(normalizedLimit, out var entry))
                {
                    if (entry.DirtyGameIds.Count == 0)
                    {
                        return entry.Data;
                    }

                    // Snapshot and patch outside the lock; the entry stays dirty until the patch
                    // is installed, so a concurrent reader either waits or takes the same path.
                    patchBase = entry.Data;
                    dirtyGameIds = entry.DirtyGameIds.ToList();
                }

                generation = _overviewProjectionGeneration;
            }

            if (patchBase != null && dirtyGameIds != null && dirtyGameIds.Count > 0)
            {
                var patched = TryPatchOverviewSummary(patchBase, dirtyGameIds, normalizedLimit, generation);
                if (patched != null)
                {
                    return patched;
                }

                // The patch refused, so fall through to the full rebuild below and drop the
                // stale entry rather than serve from it again.
                lock (_overviewProjectionCacheSync)
                {
                    _overviewSummaryCacheByLimit.Remove(normalizedLimit);
                }
            }

            var summaryData = GetCachedSummaryData(normalizedLimit);
            if (summaryData == null)
            {
                _logger?.Debug("[OverviewPerf] Cached summary fast path unavailable because cached summary data could not be loaded.");
                return null;
            }

            CachedSummaryData hydratedSummary;
            try
            {
                hydratedSummary = ApplyOverviewSummaryHydration(summaryData, normalizedLimit);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Failed to hydrate cached summary data for overview");
                hydratedSummary = summaryData;
            }

            lock (_overviewProjectionCacheSync)
            {
                // Memoize only when no invalidation landed mid-load; a stale result is still
                // returned to this caller (bounded staleness) but must not outlive the
                // invalidation in the memo.
                if (generation == _overviewProjectionGeneration)
                {
                    _overviewSummaryCacheByLimit[normalizedLimit] = new OverviewSummaryMemoEntry(hydratedSummary);
                }

                return hydratedSummary;
            }
        }

        /// <summary>
        /// Re-reads and re-hydrates each named game on its own, then splices the result into
        /// <paramref name="patchBase"/>. Returns null when anything about the patch is not
        /// expressible, in which case the caller rebuilds.
        /// </summary>
        private CachedSummaryData TryPatchOverviewSummary(
            CachedSummaryData patchBase,
            List<Guid> dirtyGameIds,
            int normalizedLimit,
            int generation)
        {
            // Only the unbounded read is patchable. A bounded one trims rows library-wide, and a
            // row trimmed away cannot be recovered from one game's slice.
            if (normalizedLimit != 0 ||
                dirtyGameIds.Count > Models.CacheInvalidatedEventArgs.MaxScopedGames)
            {
                return null;
            }

            CachedSummaryData patched;
            using (var scope = Common.PerfScope.Start(_logger, "Overview.PatchCachedSummary", thresholdMs: 25))
            {
                scope?.SetContext("games=" + dirtyGameIds.Count);

                var slices = new Dictionary<Guid, CachedSummaryData>();
                foreach (var gameId in dirtyGameIds)
                {
                    CachedSummaryData slice;
                    try
                    {
                        slice = _cacheReadOptimizations?.LoadCachedSummaryDataForGameFast(gameId);
                    }
                    catch (Exception ex)
                    {
                        _logger?.Error(ex, $"Scoped summary read failed for game {gameId}.");
                        return null;
                    }

                    if (slice == null)
                    {
                        return null;
                    }

                    try
                    {
                        // Same hydration code as the whole-library path, narrowed to this game.
                        slices[gameId] = ApplyOverviewSummaryHydration(slice, 0, new[] { gameId });
                    }
                    catch (Exception ex)
                    {
                        _logger?.Error(ex, $"Scoped summary hydration failed for game {gameId}.");
                        return null;
                    }
                }

                patched = OverviewSummaryPatcher.Patch(patchBase, dirtyGameIds, slices);
            }

            if (patched == null)
            {
                return null;
            }

            lock (_overviewProjectionCacheSync)
            {
                // Same rule the full path uses: install only when no invalidation landed while
                // the patch was being built. If one did, this caller still gets the patched
                // result (bounded staleness) and the ids stay dirty for the next read to retry.
                if (generation == _overviewProjectionGeneration &&
                    _overviewSummaryCacheByLimit.TryGetValue(normalizedLimit, out var entry))
                {
                    entry.Data = patched;
                    entry.DirtyGameIds.Clear();
                }
            }

            return patched;
        }

        internal CachedSummaryData GetCachedSummaryDataForTheme(int recentAchievementDetailLimit = 0)
        {
            return GetCachedSummaryDataForOverview(recentAchievementDetailLimit);
        }

        /// <summary>
        /// <paramref name="scopeGameIds"/> narrows the custom-data context to one game's slice.
        /// Every step below is already keyed by PlayniteGameId, so the scoped and whole-library
        /// paths run the same code -- which is the strongest guarantee available that a patched
        /// summary equals a full rebuild.
        /// </summary>
        private CachedSummaryData ApplyOverviewSummaryHydration(
            CachedSummaryData summaryData,
            int recentAchievementDetailLimit,
            IReadOnlyCollection<Guid> scopeGameIds = null)
        {
            summaryData ??= new CachedSummaryData();
            summaryData.Games ??= new List<CachedGameSummaryData>();
            summaryData.RecentUnlocks ??= new List<CachedRecentUnlockData>();
            summaryData.Achievements ??= new List<CachedRecentUnlockData>();
            summaryData.GlobalUnlockCountsByDate ??= new Dictionary<DateTime, int>();
            summaryData.UnlockCountsByDateByGame ??= new Dictionary<Guid, Dictionary<DateTime, int>>();

            var (customDataByGameId, excludedSummaryIds) = BuildOverviewCustomDataContext(scopeGameIds);
            if (excludedSummaryIds != null && excludedSummaryIds.Count > 0)
            {
                summaryData.Games = summaryData.Games
                    .Where(game => game?.PlayniteGameId.HasValue != true || !excludedSummaryIds.Contains(game.PlayniteGameId.Value))
                    .ToList();

                summaryData.RecentUnlocks = summaryData.RecentUnlocks
                    .Where(recent => recent?.PlayniteGameId.HasValue != true || !excludedSummaryIds.Contains(recent.PlayniteGameId.Value))
                    .ToList();

                summaryData.Achievements = summaryData.Achievements
                    .Where(item => item?.PlayniteGameId.HasValue != true ||
                                   !excludedSummaryIds.Contains(item.PlayniteGameId.Value))
                    .ToList();

                RemoveExcludedTimelineCounts(
                    summaryData.GlobalUnlockCountsByDate,
                    summaryData.UnlockCountsByDateByGame,
                    excludedSummaryIds);
            }

            // SQL summaries never see custom achievements, which live only in custom data.
            CustomAchievementSummaryMerger.Merge(
                summaryData,
                customDataByGameId,
                excludedSummaryIds,
                recentAchievementDetailLimit,
                gameId => GetGame(gameId)?.Name,
                PlayniteAchievementsPlugin.Instance?.ManagedCustomIconService,
                ResolveCustomProviderPlatformKey);

            var gameIdsNeedingCompletionOverrides = new HashSet<Guid>(
                summaryData.Games
                    .Where(game => game?.PlayniteGameId.HasValue == true && !game.IsCompleted)
                    .Select(game => game.PlayniteGameId.Value)
                    .Where(gameId => gameId != Guid.Empty));

            var achievementDetails = summaryData.Achievements.Count > 0
                ? summaryData.Achievements
                : summaryData.RecentUnlocks;
            var achievementGameIds = new HashSet<Guid>(
                achievementDetails
                    .Where(item => item?.PlayniteGameId.HasValue == true)
                    .Select(item => item.PlayniteGameId.Value)
                    .Where(gameId => gameId != Guid.Empty));
            gameIdsNeedingCompletionOverrides.UnionWith(achievementGameIds);

            var customizationByGameId = BuildSummaryCustomizationByGameId(
                gameIdsNeedingCompletionOverrides,
                achievementGameIds,
                customDataByGameId);
            ApplyGameSummaryCustomization(
                summaryData.Games,
                customizationByGameId,
                summaryData.Achievements,
                unlockedListIsPartial: recentAchievementDetailLimit > 0);
            ApplyAchievementSummaryCustomization(achievementDetails, customizationByGameId, summaryData);

            return summaryData;
        }

        private Dictionary<Guid, SummaryCustomizationData> BuildSummaryCustomizationByGameId(
            IEnumerable<Guid> playniteGameIds,
            ISet<Guid> includeIconOverrideGameIds,
            IReadOnlyDictionary<Guid, GameCustomDataFile> customDataByGameId)
        {
            var result = new Dictionary<Guid, SummaryCustomizationData>();
            if (playniteGameIds == null)
            {
                return result;
            }

            includeIconOverrideGameIds ??= new HashSet<Guid>();

            foreach (var gameId in playniteGameIds.Where(id => id != Guid.Empty).Distinct())
            {
                GameCustomDataFile customData = null;
                customDataByGameId?.TryGetValue(gameId, out customData);
                var resolved = ResolveSummaryCustomData(gameId, customData);

                var includeIconOverrides = includeIconOverrideGameIds.Contains(gameId);

                result[gameId] = new SummaryCustomizationData
                {
                    Resolved = resolved,
                    UnlockedIconOverrides = includeIconOverrides
                        ? CloneStringMap(customData?.AchievementUnlockedIconOverrides)
                        : EmptyIconOverrides,
                    LockedIconOverrides = includeIconOverrides
                        ? CloneStringMap(customData?.AchievementLockedIconOverrides)
                        : EmptyIconOverrides
                };
            }

            return result;
        }

        private Dictionary<Guid, GameCustomDataFile> LoadCustomDataByGameId()
        {
            var map = new Dictionary<Guid, GameCustomDataFile>();
            if (_gameCustomDataStore == null)
            {
                return map;
            }

            try
            {
                var rows = _gameCustomDataStore.LoadAll();
                if (rows == null || rows.Count == 0)
                {
                    return map;
                }

                for (var i = 0; i < rows.Count; i++)
                {
                    var row = rows[i];
                    if (row == null || row.PlayniteGameId == Guid.Empty)
                    {
                        continue;
                    }

                    map[row.PlayniteGameId] = row;
                }

                return map;
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Failed to load per-game custom data for overview summary hydration");
                return map;
            }
        }

        /// <param name="customDataByGameId">
        /// Read only if the store lookup throws. Taken as a factory so a caller that has no
        /// other use for the records does not materialize them: the map comes from LoadAll,
        /// which deep-clones every stored record, and the store's own lookup reads the cache
        /// without copying anything.
        /// </param>
        private HashSet<Guid> ResolveExcludedSummaryGameIds(
            Func<IReadOnlyDictionary<Guid, GameCustomDataFile>> customDataByGameId)
        {
            try
            {
                return GameCustomDataLookup.GetExcludedSummaryGameIds(Persisted, _gameCustomDataStore);
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, "Failed to resolve excluded summary game IDs from custom-data store. Falling back to persisted settings projection.");
                return BuildExcludedSummaryGameIdsFallback(customDataByGameId?.Invoke());
            }
        }

        private HashSet<Guid> BuildExcludedSummaryGameIdsFallback(IReadOnlyDictionary<Guid, GameCustomDataFile> customDataByGameId)
        {
            var result = Persisted?.ExcludedFromSummariesGameIds != null
                ? new HashSet<Guid>(Persisted.ExcludedFromSummariesGameIds)
                : new HashSet<Guid>();

            if (customDataByGameId == null || customDataByGameId.Count == 0)
            {
                return result;
            }

            foreach (var pair in customDataByGameId)
            {
                if (pair.Value?.ExcludedFromSummaries == true)
                {
                    result.Add(pair.Key);
                }
                else
                {
                    result.Remove(pair.Key);
                }
            }

            return result;
        }

        private ResolvedGameCustomData ResolveSummaryCustomData(Guid gameId, GameCustomDataFile customData)
        {
            if (gameId == Guid.Empty)
            {
                return ResolvedGameCustomData.Empty;
            }

            var hasCustomData = customData != null;
            var useSeparateLockedIconsDefault = Persisted?.UseSeparateLockedIconsWhenAvailable == true;
            return new ResolvedGameCustomData
            {
                ExcludedFromRefreshes = hasCustomData
                    ? customData.ExcludedFromRefreshes == true
                    : Persisted?.ExcludedGameIds?.Contains(gameId) == true,
                ExcludedFromSummaries = hasCustomData
                    ? customData.ExcludedFromSummaries == true
                    : Persisted?.ExcludedFromSummariesGameIds?.Contains(gameId) == true,
                // Same as GameCustomDataLookup: a manual link owns unlock times, so the override
                // applier must skip its unlock-time fields here exactly as the per-game path does.
                HasManualLink = hasCustomData && customData.ManualLink != null,
                UseSeparateLockedIcons = useSeparateLockedIconsDefault ||
                    (hasCustomData
                        ? customData.UseSeparateLockedIconsOverride == true
                        : Persisted?.SeparateLockedIconEnabledGameIds?.Contains(gameId) == true),
                CapstonesMaterialized = hasCustomData
                    ? customData.CapstonesMaterialized
                    : ResolveFallbackCapstones(gameId).Count > 0,
                Capstones = hasCustomData
                    ? customData.Capstones ?? new List<CapstoneAssignment>()
                    : ResolveFallbackCapstones(gameId),
                AchievementCategoryOverrides = hasCustomData
                    ? CloneStringMap(customData.AchievementCategoryOverrides)
                    : ResolveFallbackOverrides(Persisted?.AchievementCategoryOverrides, gameId),
                AchievementCategoryTypeOverrides = hasCustomData
                    ? CloneStringMap(customData.AchievementCategoryTypeOverrides)
                    : ResolveFallbackOverrides(Persisted?.AchievementCategoryTypeOverrides, gameId),
                AchievementCategoryOrder = hasCustomData
                    ? CloneCategoryOrder(customData.AchievementCategoryOrder)
                    : new List<string>(),
                AchievementCategoryImageOverrides = hasCustomData
                    ? CloneCategoryImageOverrideMap(customData.AchievementCategoryImageOverrides)
                    : new Dictionary<string, CategoryImageOverrideData>(StringComparer.OrdinalIgnoreCase),
                AchievementNotes = hasCustomData
                    ? CloneNoteMap(customData.AchievementNotes)
                    : EmptyStringMap,
                AchievementOverrides = hasCustomData
                    ? GameCustomDataFile.CloneAchievementOverrideMap(customData.AchievementOverrides) ??
                        new Dictionary<string, AchievementOverride>(StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, AchievementOverride>(StringComparer.OrdinalIgnoreCase)
            };
        }

        /// <summary>
        /// The pre-per-game-file capstone as a materialized single game-wide set, which is what it
        /// always behaved as: it suppressed every provider capstone.
        /// </summary>
        private List<CapstoneAssignment> ResolveFallbackCapstones(Guid gameId)
        {
            var capstones = new List<CapstoneAssignment>();
            if (Persisted?.ManualCapstones != null &&
                Persisted.ManualCapstones.TryGetValue(gameId, out var legacy))
            {
                var apiName = NormalizeText(legacy);
                if (!string.IsNullOrWhiteSpace(apiName))
                {
                    capstones.Add(new CapstoneAssignment { ApiName = apiName });
                }
            }

            return capstones;
        }

        /// <summary>
        /// When <paramref name="scopeGameIds"/> is given, loads only those games' custom data and
        /// narrows the exclusion set to them. LoadCustomDataByGameId goes through
        /// GameCustomDataStore.LoadAll, which deep-clones every stored record on every call, so
        /// an unscoped context costs one clone per game in the library per hydration.
        /// </summary>
        private (Dictionary<Guid, GameCustomDataFile> customDataByGameId, HashSet<Guid> excludedSummaryIds)
            BuildOverviewCustomDataContext(IReadOnlyCollection<Guid> scopeGameIds = null)
        {
            if (scopeGameIds == null || scopeGameIds.Count == 0)
            {
                var all = LoadCustomDataByGameId();
                return (all, ResolveExcludedSummaryGameIds(() => all));
            }

            var scoped = new Dictionary<Guid, GameCustomDataFile>();
            foreach (var gameId in scopeGameIds)
            {
                if (gameId == Guid.Empty || _gameCustomDataStore == null)
                {
                    continue;
                }

                try
                {
                    if (_gameCustomDataStore.TryLoad(gameId, out var record) && record != null)
                    {
                        scoped[gameId] = record;
                    }
                }
                catch (Exception ex)
                {
                    _logger?.Warn(ex, $"Failed to load custom data for game {gameId} during scoped hydration.");
                }
            }

            // Narrowed to the scope: the exclusion filter must only ever remove rows belonging to
            // games this pass actually re-read, or a patch would drop rows it never replaced.
            var excluded = ResolveExcludedSummaryGameIds(() => scoped);
            excluded?.IntersectWith(scopeGameIds);
            return (scoped, excluded);
        }

        private static Dictionary<string, string> ResolveFallbackOverrides(
            IReadOnlyDictionary<Guid, Dictionary<string, string>> mapByGameId,
            Guid gameId)
        {
            if (mapByGameId == null || !mapByGameId.TryGetValue(gameId, out var overrides))
            {
                return EmptyStringMap;
            }

            return CloneStringMap(overrides);
        }

        private static Dictionary<string, string> CloneStringMap(IReadOnlyDictionary<string, string> source)
        {
            if (source == null || source.Count == 0)
            {
                return EmptyStringMap;
            }

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in source)
            {
                var key = NormalizeText(pair.Key);
                var value = NormalizeText(pair.Value);
                if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                map[key] = value;
            }

            return map.Count == 0 ? EmptyStringMap : map;
        }

        private static List<string> CloneCategoryOrder(IEnumerable<string> source)
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var value in source ?? Enumerable.Empty<string>())
            {
                var normalized = AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(value);
                if (!string.IsNullOrWhiteSpace(normalized) && seen.Add(normalized))
                {
                    result.Add(normalized);
                }
            }

            return result;
        }

        private static Dictionary<string, CategoryImageOverrideData> CloneCategoryImageOverrideMap(
            IReadOnlyDictionary<string, CategoryImageOverrideData> source)
        {
            var map = new Dictionary<string, CategoryImageOverrideData>(StringComparer.OrdinalIgnoreCase);
            if (source == null || source.Count == 0)
            {
                return map;
            }

            foreach (var pair in source)
            {
                var key = AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(pair.Key);
                var art = NormalizeText(pair.Value?.Art);
                if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(art))
                {
                    continue;
                }

                map[key] = new CategoryImageOverrideData
                {
                    Art = art
                };
            }

            return map;
        }

        private static Dictionary<string, string> CloneNoteMap(IReadOnlyDictionary<string, string> source)
        {
            if (source == null || source.Count == 0)
            {
                return EmptyStringMap;
            }

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in source)
            {
                var key = NormalizeText(pair.Key);
                var value = AchievementNoteHelper.NormalizeNote(pair.Value);
                if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                map[key] = value;
            }

            return map.Count == 0 ? EmptyStringMap : map;
        }

        private List<GameAchievementData> ExcludeSummaryGames(List<GameAchievementData> allData)
        {
            allData ??= new List<GameAchievementData>();

            // The records are wanted only if the store lookup throws, so they are not loaded
            // up front: LoadCustomDataByGameId deep-clones every stored record, and this needs
            // a set of ids.
            var excludedSummaryIds = ResolveExcludedSummaryGameIds(LoadCustomDataByGameId);
            if (excludedSummaryIds == null || excludedSummaryIds.Count == 0)
            {
                return allData;
            }

            return allData
                .Where(data => data?.PlayniteGameId == null || !excludedSummaryIds.Contains(data.PlayniteGameId.Value))
                .ToList();
        }

        private List<GameAchievementData> ProjectVisibleGameAchievementData(
            IEnumerable<GameAchievementData> source,
            bool excludeSummaryFiltered = false)
        {
            return (source ?? Enumerable.Empty<GameAchievementData>())
                .Select(gameData => ProjectVisibleGameAchievementData(gameData, excludeSummaryFiltered))
                .Where(gameData => gameData != null)
                .ToList();
        }

        private GameAchievementData ProjectVisibleGameAchievementData(
            GameAchievementData source,
            bool excludeSummaryFiltered = false)
        {
            if (source == null)
            {
                return null;
            }

            var sourceAchievements = source.Achievements ?? new List<AchievementDetail>();
            var visibleAchievements = FilterVisibleAchievements(sourceAchievements, excludeSummaryFiltered);
            if (visibleAchievements.Count == sourceAchievements.Count)
            {
                return source;
            }

            return new GameAchievementData
            {
                LastUpdatedUtc = source.LastUpdatedUtc,
                ProviderKey = source.ProviderKey,
                ProviderPlatformKey = source.ProviderPlatformKey,
                LibrarySourceName = source.LibrarySourceName,
                HasAchievements = source.HasAchievements && visibleAchievements.Count > 0,
                ExcludedByUser = source.ExcludedByUser,
                IsAppIdOverridden = source.IsAppIdOverridden,
                GameName = source.GameName,
                AppId = source.AppId,
                ProviderGameKey = source.ProviderGameKey,
                PlayniteGameId = source.PlayniteGameId,
                Game = source.Game,
                AchievementOrder = source.AchievementOrder != null
                    ? new List<string>(source.AchievementOrder)
                    : null,
                AchievementCategoryOrder = source.AchievementCategoryOrder != null
                    ? new List<string>(source.AchievementCategoryOrder)
                    : null,
                AchievementCategoryImageOverrides = CloneCategoryImageOverrideMap(source.AchievementCategoryImageOverrides),
                GameSummaryCategory = source.GameSummaryCategory,
                ExcludedFromSummaries = source.ExcludedFromSummaries,
                UseSeparateLockedIconsWhenAvailable = source.UseSeparateLockedIconsWhenAvailable,
                Achievements = visibleAchievements
            };
        }

        private static List<AchievementDetail> FilterVisibleAchievements(
            IEnumerable<AchievementDetail> achievements,
            bool excludeSummaryFiltered = false)
        {
            var visibleAchievements = new List<AchievementDetail>();
            foreach (var achievement in achievements ?? Enumerable.Empty<AchievementDetail>())
            {
                if (achievement == null)
                {
                    continue;
                }

                if (achievement.IsFiltered)
                {
                    continue;
                }

                if (excludeSummaryFiltered && achievement.IsFilteredFromSummaries)
                {
                    continue;
                }

                visibleAchievements.Add(achievement);
            }

            return visibleAchievements;
        }

        private void ApplyGameSummaryCustomization(
            IList<CachedGameSummaryData> games,
            IReadOnlyDictionary<Guid, SummaryCustomizationData> customizationByGameId,
            IReadOnlyList<CachedRecentUnlockData> unlockedAchievements,
            bool unlockedListIsPartial = false)
        {
            if (games == null || games.Count == 0 || customizationByGameId == null || customizationByGameId.Count == 0)
            {
                return;
            }

            // Built once from the unlocked rows already in hand, and only for the games that need
            // it. This used to load each materialized game's whole achievement payload, which made
            // every summary hydration cost one full per-game cache read per capstone the user had
            // ever set -- a price that grew with use and was paid holding the cache lock.
            //
            // A bounded read holds only the most recent unlocks, so there the rows in hand cannot
            // say whether an older capstone was earned, and every stored capstone read as locked.
            // That read asks for exactly the capstones instead.
            var unlockedByGameId = unlockedListIsPartial
                ? LoadUnlockedCapstoneApiNamesByGame(games, customizationByGameId)
                : BuildUnlockedApiNamesByGame(
                    unlockedAchievements,
                    games,
                    customizationByGameId);

            foreach (var game in games)
            {
                if (game == null || !game.PlayniteGameId.HasValue)
                {
                    continue;
                }

                if (!customizationByGameId.TryGetValue(game.PlayniteGameId.Value, out var customization) ||
                    customization == null)
                {
                    continue;
                }

                // The summary SQL counts the provider seed, which is the right answer only while a
                // game is untouched. Once the user has edited its capstones the stored set is the
                // truth, and it can move completion either way: nominating a second capstone can
                // un-finish a game just as dropping one can finish it.
                var resolved = customization.Resolved;
                if (resolved?.CapstonesMaterialized != true)
                {
                    continue;
                }

                unlockedByGameId.TryGetValue(game.PlayniteGameId.Value, out var unlockedApiNames);
                ApplyStoredCapstoneCompletion(game, resolved, unlockedApiNames);
            }
        }

        /// <summary>
        /// Unlocked capstone ApiNames per materialized game, read for exactly the stored capstones
        /// rather than taken from a summary's unlock rows.
        /// </summary>
        /// <remarks>
        /// Authored capstones are answered from their definitions, since the cache never holds
        /// them; provider ones take one query for the whole set.
        /// </remarks>
        private Dictionary<Guid, HashSet<string>> LoadUnlockedCapstoneApiNamesByGame(
            IList<CachedGameSummaryData> games,
            IReadOnlyDictionary<Guid, SummaryCustomizationData> customizationByGameId)
        {
            var result = new Dictionary<Guid, HashSet<string>>();
            var wanted = new Dictionary<Guid, HashSet<string>>();
            foreach (var game in games)
            {
                var gameId = game?.PlayniteGameId;
                if (!gameId.HasValue ||
                    !customizationByGameId.TryGetValue(gameId.Value, out var customization) ||
                    customization?.Resolved?.CapstonesMaterialized != true)
                {
                    continue;
                }

                var resolved = customization.Resolved;
                var unlockedCustom = new HashSet<string>(
                    (resolved.CustomAchievements ?? new List<CustomAchievementDefinition>())
                        .Where(definition => definition?.Unlocked == true)
                        .Select(definition => CustomAchievementProjectionService.BuildApiName(definition.Id)),
                    StringComparer.OrdinalIgnoreCase);

                foreach (var assignment in resolved.Capstones ?? new List<CapstoneAssignment>())
                {
                    var apiName = NormalizeText(assignment?.ApiName);
                    if (string.IsNullOrWhiteSpace(apiName))
                    {
                        continue;
                    }

                    AddApiName(unlockedCustom.Contains(apiName) ? result : wanted, gameId.Value, apiName);
                }
            }

            if (wanted.Count == 0)
            {
                return result;
            }

            var unlockedProvider = _cacheReadOptimizations?.LoadUnlockedApiNamesFast(wanted);
            foreach (var pair in unlockedProvider ?? new Dictionary<Guid, HashSet<string>>())
            {
                foreach (var apiName in pair.Value ?? new HashSet<string>())
                {
                    AddApiName(result, pair.Key, apiName);
                }
            }

            return result;
        }

        private static void AddApiName(Dictionary<Guid, HashSet<string>> target, Guid gameId, string apiName)
        {
            if (!target.TryGetValue(gameId, out var set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                target[gameId] = set;
            }

            set.Add(apiName);
        }

        /// <summary>
        /// Unlocked ApiNames per game, for the materialized games only, so the capstone correction
        /// can count earned capstones without re-reading anything.
        /// </summary>
        private static Dictionary<Guid, HashSet<string>> BuildUnlockedApiNamesByGame(
            IReadOnlyList<CachedRecentUnlockData> unlockedAchievements,
            IList<CachedGameSummaryData> games,
            IReadOnlyDictionary<Guid, SummaryCustomizationData> customizationByGameId)
        {
            var result = new Dictionary<Guid, HashSet<string>>();
            if (unlockedAchievements == null || unlockedAchievements.Count == 0)
            {
                return result;
            }

            var wanted = new HashSet<Guid>();
            foreach (var game in games)
            {
                var gameId = game?.PlayniteGameId;
                if (gameId.HasValue &&
                    customizationByGameId.TryGetValue(gameId.Value, out var customization) &&
                    customization?.Resolved?.CapstonesMaterialized == true)
                {
                    wanted.Add(gameId.Value);
                }
            }

            if (wanted.Count == 0)
            {
                return result;
            }

            foreach (var unlock in unlockedAchievements)
            {
                var gameId = unlock?.PlayniteGameId;
                if (gameId == null || !wanted.Contains(gameId.Value))
                {
                    continue;
                }

                var apiName = NormalizeText(unlock.ApiName);
                if (string.IsNullOrWhiteSpace(apiName))
                {
                    continue;
                }

                if (!result.TryGetValue(gameId.Value, out var set))
                {
                    set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    result[gameId.Value] = set;
                }

                set.Add(apiName);
            }

            return result;
        }

        private static readonly string[] PlatinumApiNameSeparator =
            { CachedGameSummaryData.PlatinumApiNameSeparator };

        private static HashSet<string> SplitPlatinumApiNames(string packed)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(packed))
            {
                return result;
            }

            foreach (var part in packed.Split(PlatinumApiNameSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var apiName = NormalizeText(part);
                if (!string.IsNullOrWhiteSpace(apiName))
                {
                    result.Add(apiName);
                }
            }

            return result;
        }

        private static bool IsFilteredFromSummary(ResolvedGameCustomData resolved, string apiName)
        {
            return resolved?.FilteredAchievementApiNames?.Contains(apiName) == true ||
                   resolved?.SummaryFilteredAchievementApiNames?.Contains(apiName) == true;
        }

        /// <summary>
        /// Recomputes a summary row's capstone counts and completion from the game's stored set.
        /// </summary>
        /// <remarks>
        /// The stored set is taken as the total without checking that each entry still exists,
        /// because the write path prunes entries whose achievement the provider no longer sends.
        /// </remarks>
        private static void ApplyStoredCapstoneCompletion(
            CachedGameSummaryData game,
            ResolvedGameCustomData resolved,
            HashSet<string> unlockedApiNames)
        {
            var platinums = SplitPlatinumApiNames(game.PlatinumApiNames);
            var total = 0;
            var unlocked = 0;
            var capstonesThatAreNotPlatinum = 0;
            foreach (var assignment in resolved.Capstones ?? new List<CapstoneAssignment>())
            {
                var apiName = NormalizeText(assignment?.ApiName);
                if (string.IsNullOrWhiteSpace(apiName))
                {
                    continue;
                }

                // A filtered capstone is out of the counts like any filtered achievement, so it no
                // longer stands for finishing the game either; filtering it is how a capstone the
                // user does not want is set aside without deleting it. Dropped from the platinums
                // too, so it does not resurface there as a platinum that is not a capstone.
                if (IsFilteredFromSummary(resolved, apiName))
                {
                    platinums.Remove(apiName);
                    continue;
                }

                total++;
                if (unlockedApiNames != null && unlockedApiNames.Contains(apiName))
                {
                    unlocked++;
                }

                if (!platinums.Contains(apiName))
                {
                    capstonesThatAreNotPlatinum++;
                }
            }

            game.CapstoneTotal = total;
            game.CapstoneUnlocked = unlocked;
            // The stored set replaces both halves of the identity: the query counted the capstones
            // the provider flagged, which this game no longer goes by.
            game.CapstonesNotPlatinum = capstonesThatAreNotPlatinum;
            game.PlatinumsNotCapstone = platinums.Count - (total - capstonesThatAreNotPlatinum);
            game.IsCompleted =
                (game.TotalAchievements > 0 && game.UnlockedAchievements >= game.TotalAchievements) ||
                (total > 0 && unlocked >= total);
        }

        /// <param name="timeline">
        /// The summary whose unlock-date counts SQL bucketed by the recorded unlock time. An
        /// unlock-time override moves its row's count to the overridden date (or drops it when the
        /// override clears the time), matching the Overview's delta rebuild, which counts rows by
        /// their overridden time; otherwise the calendar changed between full and delta builds.
        /// </param>
        private void ApplyAchievementSummaryCustomization(
            IList<CachedRecentUnlockData> achievements,
            IReadOnlyDictionary<Guid, SummaryCustomizationData> customizationByGameId,
            CachedSummaryData timeline)
        {
            if (achievements == null || achievements.Count == 0)
            {
                return;
            }

            var defaultUseSeparateLockedIcons = Persisted?.UseSeparateLockedIconsWhenAvailable == true;
            foreach (var achievement in achievements)
            {
                if (achievement == null)
                {
                    continue;
                }

                achievement.UseSeparateLockedIconsWhenAvailable = defaultUseSeparateLockedIcons;

                if (!achievement.PlayniteGameId.HasValue ||
                    !customizationByGameId.TryGetValue(achievement.PlayniteGameId.Value, out var customization) ||
                    customization == null)
                {
                    continue;
                }

                var resolved = customization.Resolved ?? ResolvedGameCustomData.Empty;
                achievement.UseSeparateLockedIconsWhenAvailable = resolved.UseSeparateLockedIcons;

                var apiName = NormalizeText(achievement.ApiName);
                if (string.IsNullOrWhiteSpace(apiName))
                {
                    continue;
                }

                if (resolved.CapstonesMaterialized)
                {
                    achievement.IsCapstone = resolved.Capstones?.Any(capstone => capstone.Matches(apiName)) == true;
                }

                // Summary rows come straight from SQL, so the per-achievement record is applied
                // here the way the hydrator applies it to the achievement list. Without this the
                // overview's recent-unlock entries would show the provider's title and points
                // while the list showed the user's.
                var userOverride = ResolveSummaryOverride(resolved, apiName);
                achievement.ProviderCategory = achievement.ProviderCategory ?? achievement.Category;
                if (userOverride != null)
                {
                    if (!string.IsNullOrWhiteSpace(userOverride.Category))
                    {
                        // NormalizePath, as the hydrator does, so a nested path survives.
                        achievement.Category = CategoryPathHelper.NormalizePath(userOverride.Category);
                    }

                    if (!string.IsNullOrWhiteSpace(userOverride.CategoryType))
                    {
                        achievement.CategoryType = AchievementCategoryTypeHelper.NormalizeOrDefault(userOverride.CategoryType);
                    }

                    var countedDate = achievement.Unlocked && achievement.UnlockTimeUtc.HasValue
                        ? Overview.UnlockDayCounts.DayOf(achievement.UnlockTimeUtc.Value)
                        : (DateTime?)null;
                    AchievementOverrideApplier.Apply(achievement, userOverride, resolved.HasManualLink);
                    MoveTimelineCount(timeline, achievement, countedDate);
                }

                achievement.AchievementNote = userOverride?.Note;

                var unlockedOverride = AchievementIconOverrideHelper.GetOverrideValue(customization.UnlockedIconOverrides, apiName);
                if (!string.IsNullOrWhiteSpace(unlockedOverride))
                {
                    achievement.UnlockedIconPath = ResolveCustomIconOverridePath(
                        unlockedOverride,
                        achievement.PlayniteGameId.Value);
                }

                var lockedOverride = AchievementIconOverrideHelper.GetOverrideValue(customization.LockedIconOverrides, apiName);
                if (!string.IsNullOrWhiteSpace(lockedOverride))
                {
                    achievement.LockedIconPath = ResolveCustomIconOverridePath(
                        lockedOverride,
                        achievement.PlayniteGameId.Value);
                }
            }
        }

        private static void MoveTimelineCount(
            CachedSummaryData timeline,
            CachedRecentUnlockData achievement,
            DateTime? countedDate)
        {
            if (timeline == null || !countedDate.HasValue)
            {
                return;
            }

            var newDate = achievement.UnlockTimeUtc.HasValue
                ? Overview.UnlockDayCounts.DayOf(achievement.UnlockTimeUtc.Value)
                : (DateTime?)null;
            if (newDate == countedDate)
            {
                return;
            }

            // Only a row SQL actually counted is moved; a date missing from the bucket means it was
            // not (a filtered row, or a read without the timeline), and there is nothing to move.
            if (!Overview.UnlockDayCounts.RemoveDay(
                    timeline.GlobalUnlockCountsByDate,
                    timeline.UnlockCountsByDateByGame,
                    achievement.PlayniteGameId,
                    countedDate.Value))
            {
                return;
            }

            if (newDate.HasValue)
            {
                Overview.UnlockDayCounts.AddDay(
                    timeline.GlobalUnlockCountsByDate,
                    timeline.UnlockCountsByDateByGame,
                    achievement.PlayniteGameId,
                    newDate.Value);
            }
        }

        /// <summary>
        /// The per-achievement override record for a summary row, resolving through the legacy
        /// mirror maps when the resolved data predates the record.
        /// </summary>
        private static AchievementOverride ResolveSummaryOverride(ResolvedGameCustomData resolved, string apiName)
        {
            var overrides = resolved?.ResolveAchievementOverrides();
            if (overrides == null || overrides.Count == 0)
            {
                return null;
            }

            return overrides.TryGetValue(apiName, out var entry) ? entry : null;
        }

        private static string ResolveCustomIconOverridePath(string value, Guid playniteGameId)
        {
            var normalized = NormalizeText(value);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return normalized;
            }

            return PlayniteAchievementsPlugin.Instance?.ManagedCustomIconService?
                .ResolveManagedDisplayPath(normalized, playniteGameId.ToString("D"))
                ?? normalized;
        }

        private static string NormalizeText(string value)
        {
            var normalized = (value ?? string.Empty).Trim();
            return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
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

        private void SubscribeOverviewProjectionInvalidation()
        {
            _cacheService.CacheInvalidated += OnCacheInvalidatedForOverview;
            _cacheService.CacheDeltaUpdated += OnOverviewProjectionSourceChanged;

            if (_gameCustomDataStore != null)
            {
                _gameCustomDataStore.CustomDataChanged += OnCustomDataChangedForOverview;
            }

            // Tracks the current Persisted instance so the invalidation keeps working
            // after a settings cancel replaces it. The swap itself invalidates, since it
            // can revert any of the projection-affecting settings in one step.
            _persistedSubscription = new PersistedSettingsSubscription(
                _settings,
                OnPersistedSettingsChanged,
                // Wholesale: a settings swap can revert any projection-affecting setting at
                // once, which is not expressible as a per-game patch.
                () => InvalidateOverviewProjectionCaches());
        }

        private void OnPersistedSettingsChanged(object sender, PropertyChangedEventArgs e)
        {
            if (ShouldInvalidateOverviewProjectionCaches(e?.PropertyName))
            {
                InvalidateOverviewProjectionCaches();
            }
        }

        private void OnOverviewProjectionSourceChanged(object sender, EventArgs e)
        {
            InvalidateOverviewProjectionCaches();
        }

        // Ordering invariant: the filter mirror is written BEFORE the summary memo is cleared,
        // so no reader can cache a summary built against stale mirror rows. This relies on the
        // handler being synchronous and on AchievementDataService subscribing to
        // CustomDataChanged before every other summary consumer (it is constructed before
        // LibraryProjectionService in the plugin ctor, and multicast handlers run in
        // subscription order).
        private void OnCustomDataChangedForOverview(object sender, GameCustomDataChangedEventArgs e)
        {
            // A reorder-only change (goals) cannot move the filter mirror or any summary, and
            // resyncing costs a store load plus a mirror write on every toggle.
            if (e != null && !e.AffectsSummaryData)
            {
                return;
            }

            // Still before the memo is cleared, preserving the ordering invariant above. A writer
            // that cannot have moved the mirror says so, and for that case the invariant is
            // vacuous rather than bypassed: there are no stale mirror rows to guard against.
            if (e == null || e.AffectsOverrideMirror)
            {
                SyncAchievementFiltersForGame(e?.PlayniteGameId ?? Guid.Empty);
            }

            // Names the one game that moved, so the next read patches its contribution instead
            // of re-running five unfiltered whole-library queries.
            var changedGameId = e?.PlayniteGameId ?? Guid.Empty;
            InvalidateOverviewProjectionCaches(
                changedGameId == Guid.Empty ? null : new[] { changedGameId });
        }

        // A full cache invalidation may follow ClearCache(), which deletes the whole database
        // file including the filter mirror; resync heals it (a cheap no-op when unchanged).
        private void OnCacheInvalidatedForOverview(object sender, CacheInvalidatedEventArgs e)
        {
            if (e?.IsFull != false)
            {
                SyncAllAchievementFiltersFromCustomData();
            }

            // The editor raises a scoped invalidation for its own game on top of the store's
            // CustomDataChanged, and it is the only route by which an edit that does not affect
            // summary data reaches the overview. Honouring the scope here is what stops that
            // second raise from costing a whole-library rebuild.
            InvalidateOverviewProjectionCaches(
                e?.IsFull == false && e.ChangedGameIds.Count > 0 ? e.ChangedGameIds : null);
        }

        private void SyncAchievementFiltersForGame(Guid playniteGameId)
        {
            if (playniteGameId == Guid.Empty || _overrideMirror == null || _gameCustomDataStore == null)
            {
                return;
            }

            // Runs on the caller's thread inside the synchronous CustomDataChanged, which for an
            // editor write is the UI thread, and its cost scales with how many overrides the
            // game has -- it re-reads them all, diffs, then deletes and re-inserts the set. A
            // reset of every row is the largest case there is, so it carries the count.
            using var scope = Common.PerfScope.Start(
                _logger,
                "Filters.SyncAchievementFiltersForGame",
                thresholdMs: 10);

            // A missing custom-data row (deleted) maps to an empty entry list, which removes
            // the game's mirror rows.
            _gameCustomDataStore.TryLoad(playniteGameId, out var customData);
            var entries = BuildOverrideMirrorEntries(customData);
            scope?.SetContext("entries=" + (entries?.Count ?? 0));
            _overrideMirror.ReplaceAchievementOverrides(playniteGameId, entries);
        }

        /// <summary>
        /// Reconciles the whole AchievementFilters mirror against the custom-data store and
        /// clears the summary memo. Called at startup (covers legacy migrations that bypass
        /// CustomDataChanged) and after full cache invalidations.
        /// </summary>
        internal void SyncAllAchievementFiltersFromCustomData()
        {
            if (_overrideMirror == null)
            {
                return;
            }

            // Built straight off the cached records. Going through LoadCustomDataByGameId
            // deep-cloned every stored record first, and the mirror entries are fresh objects
            // holding scalars, so the copies were read once and dropped -- a full copy of the
            // library's custom data on every full invalidation, for a user who has customized
            // all of it.
            var entriesByGameId = _gameCustomDataStore?.QueryAll(rows =>
            {
                var map = new Dictionary<Guid, IReadOnlyList<AchievementOverrideMirrorEntry>>();
                foreach (var row in rows)
                {
                    if (row == null || row.PlayniteGameId == Guid.Empty)
                    {
                        continue;
                    }

                    var built = BuildOverrideMirrorEntries(row);
                    if (built.Count > 0)
                    {
                        map[row.PlayniteGameId] = built;
                    }
                }

                return map;
            }) ?? new Dictionary<Guid, IReadOnlyList<AchievementOverrideMirrorEntry>>();

            _overrideMirror.ResyncAllAchievementOverrides(entriesByGameId);
            InvalidateOverviewProjectionCaches();
        }

        /// <summary>
        /// Builds a game's desired mirror rows from its custom data: the two filter flags plus the
        /// user-editable fields summary aggregates resolve in a join.
        /// </summary>
        private static List<AchievementOverrideMirrorEntry> BuildOverrideMirrorEntries(GameCustomDataFile customData)
        {
            var entries = new Dictionary<string, AchievementOverrideMirrorEntry>(StringComparer.OrdinalIgnoreCase);
            MarkFilterEntries(entries, customData?.FilteredAchievementApiNames, (entry) => entry.IsFiltered = true);
            MarkFilterEntries(entries, customData?.SummaryFilteredAchievementApiNames, (entry) => entry.IsSummaryFiltered = true);

            // The user-editable fields aggregates read. Everything else on the record is applied
            // during hydration and deliberately not mirrored.
            foreach (var pair in customData?.AchievementOverrides ??
                new Dictionary<string, AchievementOverride>(StringComparer.OrdinalIgnoreCase))
            {
                var apiName = NormalizeText(pair.Key);
                if (apiName == null || pair.Value == null)
                {
                    continue;
                }

                if (!pair.Value.Points.HasValue && string.IsNullOrWhiteSpace(pair.Value.TrophyType))
                {
                    continue;
                }

                var entry = ResolveMirrorEntry(entries, apiName);
                entry.Points = pair.Value.Points;
                entry.TrophyType = NormalizeText(pair.Value.TrophyType);
            }

            return entries.Values.Where(entry => !entry.IsEmpty).ToList();
        }

        private static void MarkFilterEntries(
            Dictionary<string, AchievementOverrideMirrorEntry> entries,
            IEnumerable<string> apiNames,
            Action<AchievementOverrideMirrorEntry> mark)
        {
            foreach (var apiName in apiNames ?? Enumerable.Empty<string>())
            {
                var normalized = NormalizeText(apiName);
                if (normalized != null)
                {
                    mark(ResolveMirrorEntry(entries, normalized));
                }
            }
        }

        private static AchievementOverrideMirrorEntry ResolveMirrorEntry(
            Dictionary<string, AchievementOverrideMirrorEntry> entries,
            string apiName)
        {
            if (!entries.TryGetValue(apiName, out var entry) || entry == null)
            {
                entry = new AchievementOverrideMirrorEntry { ApiName = apiName };
                entries[apiName] = entry;
            }

            return entry;
        }

        /// <summary>
        /// Drops the memoized overview summaries. With <paramref name="changedGameIds"/> the
        /// unbounded entry is instead marked dirty for those games, so the next read patches
        /// their contribution rather than re-running five whole-library queries. Passing null --
        /// a settings change, a full cache invalidation, a library-wide filter resync -- keeps
        /// the wholesale behaviour.
        /// </summary>
        private void InvalidateOverviewProjectionCaches(IReadOnlyList<Guid> changedGameIds = null)
        {
            var scoped = changedGameIds?.Where(id => id != Guid.Empty).ToList();
            if (scoped == null ||
                scoped.Count == 0 ||
                scoped.Count > Models.CacheInvalidatedEventArgs.MaxScopedGames)
            {
                lock (_overviewProjectionCacheSync)
                {
                    _overviewProjectionGeneration++;
                    _overviewSummaryCacheByLimit.Clear();
                }

                return;
            }

            lock (_overviewProjectionCacheSync)
            {
                // Still bumped for every invalidation, so a summary loaded before this point is
                // never memoized after it. The generation guards the install, not the dirty set.
                _overviewProjectionGeneration++;

                // Bounded-limit entries cannot be patched (a trimmed row cannot be recovered),
                // so they are dropped as before. Only the unbounded entry carries dirty games.
                var boundedLimits = _overviewSummaryCacheByLimit.Keys.Where(limit => limit != 0).ToList();
                foreach (var limit in boundedLimits)
                {
                    _overviewSummaryCacheByLimit.Remove(limit);
                }

                if (!_overviewSummaryCacheByLimit.TryGetValue(0, out var entry))
                {
                    // Nothing memoized to patch; the next read takes the full path anyway.
                    return;
                }

                foreach (var gameId in scoped)
                {
                    entry.DirtyGameIds.Add(gameId);
                }

                // Past the cap the patch stops paying for itself against a rebuild.
                if (entry.DirtyGameIds.Count > Models.CacheInvalidatedEventArgs.MaxScopedGames)
                {
                    _overviewSummaryCacheByLimit.Remove(0);
                }
            }
        }

        /// <summary>
        /// Memoized overview summaries retained right now (one per requested limit) and the
        /// achievement rows they hold, for memory diagnostics.
        /// </summary>
        internal void GetOverviewMemoStats(out int entries, out int achievementRows)
        {
            lock (_overviewProjectionCacheSync)
            {
                entries = _overviewSummaryCacheByLimit.Count;
                achievementRows = 0;
                foreach (var cached in _overviewSummaryCacheByLimit.Values)
                {
                    // Counts rows held, not distinct rows: a patched envelope shares most of its
                    // rows with the one it replaced, so this over-reports retention slightly.
                    achievementRows += cached?.Data?.Achievements?.Count ?? 0;
                    achievementRows += cached?.Data?.RecentUnlocks?.Count ?? 0;
                }
            }
        }

        private static bool ShouldInvalidateOverviewProjectionCaches(string propertyName)
        {
            return string.IsNullOrWhiteSpace(propertyName) ||
                   OverviewProjectionAffectingSettings.Contains(propertyName);
        }

        private List<GameAchievementData> LoadAllCachedGameData()
        {
            List<GameAchievementData> result;
            if (_cacheReadOptimizations != null)
            {
                result = _cacheReadOptimizations.LoadAllGameDataFast() ?? new List<GameAchievementData>();
                AppendSyntheticCustomOnlyGameData(result);
                return result;
            }

            var gameIds = _cacheService.GetCachedGameIds();
            result = new List<GameAchievementData>();
            foreach (var gameId in gameIds)
            {
                var gameData = _cacheService.LoadGameData(gameId);
                if (gameData != null)
                {
                    result.Add(gameData);
                }
            }

            AppendSyntheticCustomOnlyGameData(result);
            return result;
        }

        private GameAchievementData GetMergedGameAchievementData(
            string playniteGameId,
            bool includeAchievementOverlays)
        {
            var data = _cacheService.LoadGameData(playniteGameId);
            if (data == null && Guid.TryParse(playniteGameId, out var parsedGameId))
            {
                data = CreateSyntheticCustomGameData(parsedGameId, LoadCustomData(parsedGameId));
            }

            if (includeAchievementOverlays)
            {
                _hydrator.Hydrate(data);
            }
            else
            {
                _hydrator.HydrateForOverview(data);
            }

            return data;
        }

        private void AppendSyntheticCustomOnlyGameData(ICollection<GameAchievementData> result)
        {
            if (result == null || _gameCustomDataStore == null)
            {
                return;
            }

            var existingIds = new HashSet<Guid>(
                result
                    .Where(data => data?.PlayniteGameId.HasValue == true && data.PlayniteGameId.Value != Guid.Empty)
                    .Select(data => data.PlayniteGameId.Value));

            foreach (var customData in LoadCustomDataByGameId().Values)
            {
                if (customData == null ||
                    customData.PlayniteGameId == Guid.Empty ||
                    existingIds.Contains(customData.PlayniteGameId) ||
                    !CustomAchievementProjectionService.HasCustomAchievements(customData))
                {
                    continue;
                }

                var synthetic = CreateSyntheticCustomGameData(customData.PlayniteGameId, customData);
                if (synthetic == null)
                {
                    continue;
                }

                result.Add(synthetic);
                existingIds.Add(customData.PlayniteGameId);
            }
        }

        private GameCustomDataFile LoadCustomData(Guid playniteGameId)
        {
            if (playniteGameId == Guid.Empty || _gameCustomDataStore == null)
            {
                return null;
            }

            return _gameCustomDataStore.TryLoad(playniteGameId, out var data)
                ? data
                : null;
        }

        private GameAchievementData CreateSyntheticCustomGameData(
            Guid playniteGameId,
            GameCustomDataFile customData)
        {
            if (!CustomAchievementProjectionService.HasCustomAchievements(customData))
            {
                return null;
            }

            return CustomAchievementProjectionService.CreateSyntheticGameData(
                playniteGameId,
                GetGame(playniteGameId),
                customData.CustomAchievements,
                PlayniteAchievementsPlugin.Instance?.ManagedCustomIconService,
                ResolveCustomProviderPlatformKey(customData.CustomProviderId));
        }

        /// <summary>
        /// The display key for an assigned custom provider, or null when the id is blank or no
        /// longer names a stored provider (the game then displays as plain Custom).
        /// </summary>
        private static string ResolveCustomProviderPlatformKey(string customProviderId)
        {
            if (string.IsNullOrWhiteSpace(customProviderId))
            {
                return null;
            }

            return PlayniteAchievementsPlugin.Instance?.CustomProviderStore?.ResolveDisplayKey(customProviderId);
        }

        private Playnite.SDK.Models.Game GetGame(Guid playniteGameId)
        {
            try
            {
                return _api?.Database?.Games?.Get(playniteGameId);
            }
            catch
            {
                return null;
            }
        }

        private void HydrateAll(IEnumerable<GameAchievementData> games, bool includeAchievementOverlays)
        {
            if (includeAchievementOverlays)
            {
                _hydrator.HydrateAll(games);
            }
            else
            {
                _hydrator.HydrateAllForOverview(games);
            }
        }
    }
}
