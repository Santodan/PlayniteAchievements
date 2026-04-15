using Playnite.SDK;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Hydration;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;

namespace PlayniteAchievements.Services
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
        private readonly GameDataHydrator _hydrator;
        private readonly ILogger _logger;
        private readonly GameCustomDataStore _gameCustomDataStore;
        private readonly PersistedSettings _persistedSettings;
        private readonly object _sidebarProjectionCacheSync = new object();
        private readonly Dictionary<int, CachedSummaryData> _sidebarSummaryCacheByLimit =
            new Dictionary<int, CachedSummaryData>();
        private List<CachedRecentUnlockData> _sidebarRecentUnlocksCache;

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
            _gameCustomDataStore = gameCustomDataStore;
            _persistedSettings = settings.Persisted;
            _cacheReadOptimizations = cacheService as ICacheReadOptimizations;
            _hydrator = new GameDataHydrator(api, settings.Persisted, _gameCustomDataStore);
            SubscribeSidebarProjectionInvalidation();
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

        public GameAchievementData GetGameAchievementDataForSidebar(Guid playniteGameId)
        {
            if (playniteGameId == Guid.Empty)
            {
                return null;
            }

            try
            {
                return GetMergedGameAchievementData(playniteGameId.ToString(), includeAchievementOverlays: false);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, string.Format(
                    "Failed to get sidebar achievement data for gameId={0}",
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

        public List<GameAchievementData> GetAllGameAchievementDataForSidebar()
        {
            try
            {
                var result = LoadAllCachedGameData();
                HydrateAll(result, includeAchievementOverlays: false);
                return result;
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Failed to get all sidebar achievement data");
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

        internal CachedSummaryData GetCachedSummaryDataForSidebar(int recentAchievementDetailLimit = 0)
        {
            var normalizedLimit = Math.Max(0, recentAchievementDetailLimit);
            lock (_sidebarProjectionCacheSync)
            {
                if (_sidebarSummaryCacheByLimit.TryGetValue(normalizedLimit, out var cachedSummary))
                {
                    return cachedSummary;
                }
            }

            var summaryData = GetCachedSummaryData(normalizedLimit);
            if (summaryData == null)
            {
                return null;
            }

            CachedSummaryData hydratedSummary;
            try
            {
                hydratedSummary = ApplySidebarSummaryHydration(summaryData);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Failed to hydrate cached summary data for sidebar");
                hydratedSummary = summaryData;
            }

            lock (_sidebarProjectionCacheSync)
            {
                _sidebarSummaryCacheByLimit[normalizedLimit] = hydratedSummary;
                return hydratedSummary;
            }
        }

        internal List<CachedRecentUnlockData> GetCachedRecentUnlocksForSidebar()
        {
            lock (_sidebarProjectionCacheSync)
            {
                if (_sidebarRecentUnlocksCache != null)
                {
                    return _sidebarRecentUnlocksCache;
                }
            }

            var summaryData = GetCachedSummaryData();
            if (summaryData == null)
            {
                return null;
            }

            List<CachedRecentUnlockData> hydratedRecentUnlocks;
            try
            {
                hydratedRecentUnlocks = ApplySidebarRecentUnlockHydration(summaryData);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Failed to hydrate cached recent unlock data for sidebar");
                hydratedRecentUnlocks = summaryData.RecentUnlocks ?? new List<CachedRecentUnlockData>();
            }

            lock (_sidebarProjectionCacheSync)
            {
                _sidebarRecentUnlocksCache = hydratedRecentUnlocks;
                return _sidebarRecentUnlocksCache;
            }
        }

        private CachedSummaryData ApplySidebarSummaryHydration(CachedSummaryData summaryData)
        {
            summaryData ??= new CachedSummaryData();
            summaryData.Games ??= new List<CachedGameSummaryData>();
            summaryData.RecentUnlocks ??= new List<CachedRecentUnlockData>();
            summaryData.GlobalUnlockCountsByDate ??= new Dictionary<DateTime, int>();
            summaryData.UnlockCountsByDateByGame ??= new Dictionary<Guid, Dictionary<DateTime, int>>();

            var (customDataByGameId, excludedSummaryIds) = BuildSidebarCustomDataContext();
            if (excludedSummaryIds != null && excludedSummaryIds.Count > 0)
            {
                summaryData.Games = summaryData.Games
                    .Where(game => game?.PlayniteGameId.HasValue != true || !excludedSummaryIds.Contains(game.PlayniteGameId.Value))
                    .ToList();

                summaryData.RecentUnlocks = summaryData.RecentUnlocks
                    .Where(recent => recent?.PlayniteGameId.HasValue != true || !excludedSummaryIds.Contains(recent.PlayniteGameId.Value))
                    .ToList();

                RemoveExcludedTimelineCounts(
                    summaryData.GlobalUnlockCountsByDate,
                    summaryData.UnlockCountsByDateByGame,
                    excludedSummaryIds);
            }

            var gameIdsNeedingCompletionOverrides = new HashSet<Guid>(
                summaryData.Games
                    .Where(game => game?.PlayniteGameId.HasValue == true && !game.IsCompleted)
                    .Select(game => game.PlayniteGameId.Value)
                    .Where(gameId => gameId != Guid.Empty));

            var recentGameIds = new HashSet<Guid>(
                summaryData.RecentUnlocks
                    .Where(recent => recent?.PlayniteGameId.HasValue == true)
                    .Select(recent => recent.PlayniteGameId.Value)
                    .Where(gameId => gameId != Guid.Empty));
            gameIdsNeedingCompletionOverrides.UnionWith(recentGameIds);

            var customizationByGameId = BuildSummaryCustomizationByGameId(
                gameIdsNeedingCompletionOverrides,
                recentGameIds,
                customDataByGameId);
            ApplyGameSummaryCustomization(summaryData.Games, customizationByGameId);
            ApplyRecentSummaryCustomization(summaryData.RecentUnlocks, customizationByGameId);

            return summaryData;
        }

        private List<CachedRecentUnlockData> ApplySidebarRecentUnlockHydration(CachedSummaryData summaryData)
        {
            summaryData ??= new CachedSummaryData();
            summaryData.RecentUnlocks ??= new List<CachedRecentUnlockData>();

            var recentUnlocks = summaryData.RecentUnlocks;
            var (customDataByGameId, excludedSummaryIds) = BuildSidebarCustomDataContext();
            if (excludedSummaryIds != null && excludedSummaryIds.Count > 0)
            {
                recentUnlocks = recentUnlocks
                    .Where(recent => recent?.PlayniteGameId.HasValue != true || !excludedSummaryIds.Contains(recent.PlayniteGameId.Value))
                    .ToList();
            }

            var recentGameIds = new HashSet<Guid>(
                recentUnlocks
                    .Where(recent => recent?.PlayniteGameId.HasValue == true)
                    .Select(recent => recent.PlayniteGameId.Value)
                    .Where(gameId => gameId != Guid.Empty));
            var customizationByGameId = BuildSummaryCustomizationByGameId(
                recentGameIds,
                recentGameIds,
                customDataByGameId);

            ApplyRecentSummaryCustomization(recentUnlocks, customizationByGameId);
            return recentUnlocks;
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
            if (_gameCustomDataStore == null)
            {
                return new Dictionary<Guid, GameCustomDataFile>();
            }

            try
            {
                var rows = _gameCustomDataStore.LoadAll();
                var map = new Dictionary<Guid, GameCustomDataFile>();
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
                _logger?.Error(ex, "Failed to load per-game custom data for sidebar summary hydration");
                return new Dictionary<Guid, GameCustomDataFile>();
            }
        }

        private HashSet<Guid> BuildExcludedSummaryGameIds(IReadOnlyDictionary<Guid, GameCustomDataFile> customDataByGameId)
        {
            var result = _persistedSettings?.ExcludedFromSummariesGameIds != null
                ? new HashSet<Guid>(_persistedSettings.ExcludedFromSummariesGameIds)
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
            var useSeparateLockedIconsDefault = _persistedSettings?.UseSeparateLockedIconsWhenAvailable == true;
            return new ResolvedGameCustomData
            {
                ExcludedFromRefreshes = hasCustomData
                    ? customData.ExcludedFromRefreshes == true
                    : _persistedSettings?.ExcludedGameIds?.Contains(gameId) == true,
                ExcludedFromSummaries = hasCustomData
                    ? customData.ExcludedFromSummaries == true
                    : _persistedSettings?.ExcludedFromSummariesGameIds?.Contains(gameId) == true,
                UseSeparateLockedIcons = useSeparateLockedIconsDefault ||
                    (hasCustomData
                        ? customData.UseSeparateLockedIconsOverride == true
                        : _persistedSettings?.SeparateLockedIconEnabledGameIds?.Contains(gameId) == true),
                ManualCapstoneApiName = hasCustomData
                    ? NormalizeText(customData.ManualCapstoneApiName)
                    : ResolveFallbackManualCapstone(gameId),
                AchievementCategoryOverrides = hasCustomData
                    ? CloneStringMap(customData.AchievementCategoryOverrides)
                    : ResolveFallbackOverrides(_persistedSettings?.AchievementCategoryOverrides, gameId),
                AchievementCategoryTypeOverrides = hasCustomData
                    ? CloneStringMap(customData.AchievementCategoryTypeOverrides)
                    : ResolveFallbackOverrides(_persistedSettings?.AchievementCategoryTypeOverrides, gameId)
            };
        }

        private string ResolveFallbackManualCapstone(Guid gameId)
        {
            if (_persistedSettings?.ManualCapstones == null ||
                !_persistedSettings.ManualCapstones.TryGetValue(gameId, out var manualCapstoneApiName))
            {
                return null;
            }

            return NormalizeText(manualCapstoneApiName);
        }

        private (Dictionary<Guid, GameCustomDataFile> customDataByGameId, HashSet<Guid> excludedSummaryIds)
            BuildSidebarCustomDataContext()
        {
            var customDataByGameId = LoadCustomDataByGameId();
            return (customDataByGameId, BuildExcludedSummaryGameIds(customDataByGameId));
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

        private List<GameAchievementData> ExcludeSummaryGames(List<GameAchievementData> allData)
        {
            allData ??= new List<GameAchievementData>();

            var excludedSummaryIds = BuildExcludedSummaryGameIds(LoadCustomDataByGameId());
            if (excludedSummaryIds == null || excludedSummaryIds.Count == 0)
            {
                return allData;
            }

            return allData
                .Where(data => data?.PlayniteGameId == null || !excludedSummaryIds.Contains(data.PlayniteGameId.Value))
                .ToList();
        }

        private void ApplyGameSummaryCustomization(
            IList<CachedGameSummaryData> games,
            IReadOnlyDictionary<Guid, SummaryCustomizationData> customizationByGameId)
        {
            if (games == null || games.Count == 0 || customizationByGameId == null || customizationByGameId.Count == 0)
            {
                return;
            }

            foreach (var game in games)
            {
                if (game == null || game.IsCompleted || !game.PlayniteGameId.HasValue)
                {
                    continue;
                }

                if (!customizationByGameId.TryGetValue(game.PlayniteGameId.Value, out var customization) ||
                    customization == null)
                {
                    continue;
                }

                var manualCapstoneApiName = NormalizeText(customization.Resolved?.ManualCapstoneApiName);
                if (string.IsNullOrWhiteSpace(manualCapstoneApiName))
                {
                    continue;
                }

                if (IsManualCapstoneUnlocked(game.PlayniteGameId.Value, manualCapstoneApiName))
                {
                    game.IsCompleted = true;
                }
            }
        }

        private void ApplyRecentSummaryCustomization(
            IList<CachedRecentUnlockData> recentUnlocks,
            IReadOnlyDictionary<Guid, SummaryCustomizationData> customizationByGameId)
        {
            if (recentUnlocks == null || recentUnlocks.Count == 0)
            {
                return;
            }

            var defaultUseSeparateLockedIcons = _persistedSettings?.UseSeparateLockedIconsWhenAvailable == true;
            foreach (var recent in recentUnlocks)
            {
                if (recent == null)
                {
                    continue;
                }

                recent.UseSeparateLockedIconsWhenAvailable = defaultUseSeparateLockedIcons;

                if (!recent.PlayniteGameId.HasValue ||
                    !customizationByGameId.TryGetValue(recent.PlayniteGameId.Value, out var customization) ||
                    customization == null)
                {
                    continue;
                }

                var resolved = customization.Resolved ?? ResolvedGameCustomData.Empty;
                recent.UseSeparateLockedIconsWhenAvailable = resolved.UseSeparateLockedIcons;

                var apiName = NormalizeText(recent.ApiName);
                if (string.IsNullOrWhiteSpace(apiName))
                {
                    continue;
                }

                var manualCapstoneApiName = NormalizeText(resolved.ManualCapstoneApiName);
                if (!string.IsNullOrWhiteSpace(manualCapstoneApiName))
                {
                    recent.IsCapstone = string.Equals(apiName, manualCapstoneApiName, StringComparison.OrdinalIgnoreCase);
                }

                if (resolved.AchievementCategoryOverrides != null &&
                    resolved.AchievementCategoryOverrides.TryGetValue(apiName, out var categoryOverride) &&
                    !string.IsNullOrWhiteSpace(categoryOverride))
                {
                    recent.Category = AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(categoryOverride);
                }

                if (resolved.AchievementCategoryTypeOverrides != null &&
                    resolved.AchievementCategoryTypeOverrides.TryGetValue(apiName, out var categoryTypeOverride) &&
                    !string.IsNullOrWhiteSpace(categoryTypeOverride))
                {
                    recent.CategoryType = AchievementCategoryTypeHelper.NormalizeOrDefault(categoryTypeOverride);
                }

                var unlockedOverride = AchievementIconOverrideHelper.GetOverrideValue(customization.UnlockedIconOverrides, apiName);
                if (!string.IsNullOrWhiteSpace(unlockedOverride))
                {
                    recent.UnlockedIconPath = ResolveCustomIconOverridePath(unlockedOverride, recent.PlayniteGameId.Value);
                }

                var lockedOverride = AchievementIconOverrideHelper.GetOverrideValue(customization.LockedIconOverrides, apiName);
                if (!string.IsNullOrWhiteSpace(lockedOverride))
                {
                    recent.LockedIconPath = ResolveCustomIconOverridePath(lockedOverride, recent.PlayniteGameId.Value);
                }
            }
        }

        private bool IsManualCapstoneUnlocked(Guid playniteGameId, string manualCapstoneApiName)
        {
            if (playniteGameId == Guid.Empty || string.IsNullOrWhiteSpace(manualCapstoneApiName))
            {
                return false;
            }

            var gameData = GetRawGameAchievementData(playniteGameId);
            return gameData?.Achievements != null &&
                gameData.Achievements.Any(achievement =>
                    achievement != null &&
                    achievement.Unlocked &&
                    string.Equals(achievement.ApiName, manualCapstoneApiName, StringComparison.OrdinalIgnoreCase));
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

        private void SubscribeSidebarProjectionInvalidation()
        {
            _cacheService.CacheInvalidated += OnSidebarProjectionSourceChanged;
            _cacheService.CacheDeltaUpdated += OnSidebarProjectionSourceChanged;

            if (_gameCustomDataStore != null)
            {
                _gameCustomDataStore.CustomDataChanged += OnSidebarProjectionSourceChanged;
            }

            if (_persistedSettings != null)
            {
                _persistedSettings.PropertyChanged += OnPersistedSettingsChanged;
            }
        }

        private void OnPersistedSettingsChanged(object sender, PropertyChangedEventArgs e)
        {
            InvalidateSidebarProjectionCaches();
        }

        private void OnSidebarProjectionSourceChanged(object sender, EventArgs e)
        {
            InvalidateSidebarProjectionCaches();
        }

        private void InvalidateSidebarProjectionCaches()
        {
            lock (_sidebarProjectionCacheSync)
            {
                _sidebarSummaryCacheByLimit.Clear();
                _sidebarRecentUnlocksCache = null;
            }
        }

        private List<GameAchievementData> LoadAllCachedGameData()
        {
            if (_cacheReadOptimizations != null)
            {
                return _cacheReadOptimizations.LoadAllGameDataFast() ?? new List<GameAchievementData>();
            }

            var gameIds = _cacheService.GetCachedGameIds();
            var result = new List<GameAchievementData>();
            foreach (var gameId in gameIds)
            {
                var gameData = _cacheService.LoadGameData(gameId);
                if (gameData != null)
                {
                    result.Add(gameData);
                }
            }

            return result;
        }

        private GameAchievementData GetMergedGameAchievementData(
            string playniteGameId,
            bool includeAchievementOverlays)
        {
            var data = _cacheService.LoadGameData(playniteGameId);
            if (includeAchievementOverlays)
            {
                _hydrator.Hydrate(data);
            }
            else
            {
                _hydrator.HydrateForSidebar(data);
            }

            return data;
        }

        private void HydrateAll(IEnumerable<GameAchievementData> games, bool includeAchievementOverlays)
        {
            if (includeAchievementOverlays)
            {
                _hydrator.HydrateAll(games);
            }
            else
            {
                _hydrator.HydrateAllForSidebar(games);
            }
        }
    }
}
