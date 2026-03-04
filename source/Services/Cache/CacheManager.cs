using System;
using System.Collections.Generic;
using System.IO;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Services.Database;
using Playnite.SDK;
using System.Windows;

namespace PlayniteAchievements.Services
{
    public sealed class CacheManager : ICacheManager, IDisposable
    {
        private const int MaxInMemoryGames = 256;

        private sealed class CacheEntry
        {
            public GameAchievementData Data { get; set; }
            public LinkedListNode<string> Node { get; set; }
        }

        private readonly IPlayniteAPI _api;
        private readonly ILogger _logger;
        private readonly CacheStorage _storage;
        private readonly SqlNadoCacheStore _store;
        private readonly LegacyJsonCacheImporter _importer;

        private readonly object _sync = new object();

        // In-memory state (current-user achievements only)
        // key = "{playniteGameId}" OR "app:{appId}"
        private Dictionary<string, CacheEntry> _userAchievements =
            new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);
        private LinkedList<string> _lruOrder = new LinkedList<string>();

        private string _scopeToken = "none";
        private bool _scopeInitialized;
        private Exception _initializationFailure;
        private bool _startupFailureDialogShown;

        public event EventHandler<GameCacheUpdatedEventArgs> GameCacheUpdated;
        public event EventHandler<CacheDeltaEventArgs> CacheDeltaUpdated;
        public event EventHandler CacheInvalidated;

        public CacheManager(IPlayniteAPI api, ILogger logger, PlayniteAchievementsPlugin plugin)
        {
            _api = api;
            _logger = logger;
            _storage = new CacheStorage(plugin, logger);
            _store = new SqlNadoCacheStore(plugin, logger, _storage.BaseDir);
            _importer = new LegacyJsonCacheImporter(_storage, _store, logger);

            InitializeCacheStartup();
        }

        private void InitializeCacheStartup()
        {
            using (PerfScope.Start(_logger, "Cache.InitializeCacheStartup", thresholdMs: 25))
            {
                try
                {
                    _store.EnsureInitialized();
                    _importer.ImportIfNeeded();

                    lock (_sync)
                    {
                        _initializationFailure = null;
                        RefreshScopeToken_Locked(clearMemoryOnChange: true);
                    }
                }
                catch (Exception ex)
                {
                    HandleInitializationFailure(ex);
                }
            }
        }

        private void HandleInitializationFailure(Exception ex)
        {
            lock (_sync)
            {
                _initializationFailure = ex;
                ClearMemoryState_Locked();
            }

            _logger?.Error(ex, "Failed to initialize SQLNado achievement cache.");

            if (_startupFailureDialogShown)
            {
                return;
            }

            _startupFailureDialogShown = true;
            try
            {
                _api?.Dialogs?.ShowMessage(
                    "Achievement cache migration/repair failed. Refreshes are blocked to prevent silent data loss. " +
                    "Check logs for details and restore from migration backups if needed.",
                    ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            catch
            {
            }
        }

        private void RaiseGameCacheUpdatedEvent(string gameId)
        {
            try { GameCacheUpdated?.Invoke(this, new GameCacheUpdatedEventArgs(gameId)); }
            catch (Exception ex)
            {
                _logger?.Error(ex, ResourceProvider.GetString("LOCPlayAch_Error_NotifySubscribers"));
            }
        }

        private void RaiseCacheDeltaUpdatedEvent(string key, CacheDeltaOperationType operationType)
        {
            try { CacheDeltaUpdated?.Invoke(this, new CacheDeltaEventArgs(key, operationType, DateTime.UtcNow)); }
            catch (Exception ex)
            {
                _logger?.Error(ex, ResourceProvider.GetString("LOCPlayAch_Error_NotifySubscribers"));
            }
        }

        private void RaiseCacheInvalidatedEvent()
        {
            try { CacheInvalidated?.Invoke(this, EventArgs.Empty); }
            catch (Exception ex)
            {
                _logger?.Error(ex, ResourceProvider.GetString("LOCPlayAch_Error_NotifySubscribers"));
            }
        }

        private void ClearMemoryState_Locked()
        {
            _userAchievements = new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);
            _lruOrder = new LinkedList<string>();
        }

        private bool TryGetMemoryGameData_Locked(string key, out GameAchievementData data)
        {
            data = null;
            if (string.IsNullOrWhiteSpace(key))
            {
                return false;
            }

            var normalized = UserKey(key);
            if (!_userAchievements.TryGetValue(normalized, out var entry) || entry?.Data == null)
            {
                return false;
            }

            if (entry.Node != null)
            {
                _lruOrder.Remove(entry.Node);
                _lruOrder.AddFirst(entry.Node);
            }

            data = entry.Data;
            return true;
        }

        private void SetMemoryGameData_Locked(string key, GameAchievementData data)
        {
            if (string.IsNullOrWhiteSpace(key) || data == null)
            {
                return;
            }

            var normalized = UserKey(key);
            var copy = CloneGameData(data);
            if (_userAchievements.TryGetValue(normalized, out var existing) && existing != null)
            {
                existing.Data = copy;
                if (existing.Node != null)
                {
                    _lruOrder.Remove(existing.Node);
                    _lruOrder.AddFirst(existing.Node);
                }
                return;
            }

            var node = new LinkedListNode<string>(normalized);
            _lruOrder.AddFirst(node);
            _userAchievements[normalized] = new CacheEntry
            {
                Data = copy,
                Node = node
            };

            while (_userAchievements.Count > MaxInMemoryGames)
            {
                var lru = _lruOrder.Last;
                if (lru == null)
                {
                    break;
                }

                _lruOrder.RemoveLast();
                _userAchievements.Remove(lru.Value);
            }
        }

        private void RemoveMemoryGameData_Locked(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return;
            }

            var normalized = UserKey(key);
            if (_userAchievements.TryGetValue(normalized, out var entry))
            {
                if (entry?.Node != null)
                {
                    _lruOrder.Remove(entry.Node);
                }

                _userAchievements.Remove(normalized);
            }
        }

        private void RemoveStaleMemoryEntries_Locked(HashSet<string> validKeys)
        {
            if (validKeys == null)
            {
                return;
            }

            var stale = new List<string>();
            foreach (var key in _userAchievements.Keys)
            {
                if (!validKeys.Contains(key))
                {
                    stale.Add(key);
                }
            }

            for (var i = 0; i < stale.Count; i++)
            {
                RemoveMemoryGameData_Locked(stale[i]);
            }
        }

        // ---------------------------
        // Disk presence policy
        // ---------------------------

        private bool CoreArtifactsPresent()
        {
            try
            {
                if (_initializationFailure != null)
                {
                    return false;
                }

                return _store.HasAnyCurrentUserCacheRows();
            }
            catch
            {
                return false;
            }
        }

        private void EnsureReady_Locked(string operationPhase)
        {
            if (_initializationFailure != null)
            {
                throw new InvalidOperationException(
                    $"Cache manager is unavailable (phase={operationPhase}).",
                    _initializationFailure);
            }

            try
            {
                _store.EnsureInitialized();
            }
            catch (Exception ex)
            {
                using (PerfScope.Start(
                    _logger,
                    "Cache.EnsureReady.Exception",
                    thresholdMs: 25,
                    context: $"phase={operationPhase}"))
                {
                    HandleInitializationFailure(ex);
                }

                throw new InvalidOperationException(
                    $"Cache manager failed to initialize (phase={operationPhase}).",
                    ex);
            }
        }

        private bool RefreshScopeToken_Locked(bool clearMemoryOnChange)
        {
            var token = _store.GetCurrentUserScopeToken() ?? "none";
            if (!_scopeInitialized)
            {
                _scopeToken = token;
                _scopeInitialized = true;
                return false;
            }

            if (string.Equals(_scopeToken, token, StringComparison.Ordinal))
            {
                return false;
            }

            var previous = _scopeToken;
            _scopeToken = token;
            if (clearMemoryOnChange)
            {
                ClearMemoryState_Locked();
            }

            _logger?.Info($"[Cache] Scope token changed '{previous}' -> '{token}'.");
            return true;
        }

        public void EnsureDiskCacheOrClearMemory()
        {
            var emitFullReset = false;
            lock (_sync)
            {
                if (_initializationFailure != null)
                {
                    ClearMemoryState_Locked();
                    emitFullReset = true;
                }
                else
                {
                    var hadDbFile = File.Exists(_store.DatabasePath);
                    EnsureReady_Locked("EnsureDiskCacheOrClearMemory");
                    var scopeChanged = RefreshScopeToken_Locked(clearMemoryOnChange: true);
                    if (!hadDbFile || scopeChanged)
                    {
                        ClearMemoryState_Locked();
                        emitFullReset = true;
                    }
                }
            }

            if (emitFullReset)
            {
                RaiseCacheDeltaUpdatedEvent(string.Empty, CacheDeltaOperationType.FullReset);
                RaiseCacheInvalidatedEvent();
            }
        }

        public bool CacheFileExists() => CoreArtifactsPresent();

        public bool IsCacheValid()
        {
            return CoreArtifactsPresent();
        }

        public DateTime? GetMostRecentLastUpdatedUtc()
        {
            try
            {
                lock (_sync)
                {
                    EnsureReady_Locked("GetMostRecentLastUpdatedUtc");
                    return _store.GetMostRecentLastUpdatedUtc();
                }
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Failed to get oldest last updated time.");
                return null;
            }
        }

        public List<string> GetCachedGameIds()
        {
            try
            {
                lock (_sync)
                {
                    EnsureReady_Locked("GetCachedGameIds");
                    RefreshScopeToken_Locked(clearMemoryOnChange: true);
                    return _store.GetCachedGameIdsForCurrentUsers() ?? new List<string>();
                }
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Failed to load cached game IDs.");
                return new List<string>();
            }
        }

        public void ClearCache()
        {
            lock (_sync)
            {
                ClearMemoryState_Locked();

                try
                {
                    _store.ClearCacheData();
                    _initializationFailure = null;
                    _scopeInitialized = false;
                    _scopeToken = "none";

                    _store.EnsureInitialized();
                    _importer.ImportIfNeeded();
                    RefreshScopeToken_Locked(clearMemoryOnChange: true);
                }
                catch (Exception ex)
                {
                    HandleInitializationFailure(ex);
                }
            }

            RaiseCacheDeltaUpdatedEvent(string.Empty, CacheDeltaOperationType.FullReset);
            RaiseCacheInvalidatedEvent();
        }

        // ---------------------------
        // User achievements
        // ---------------------------

        private string UserKey(string key) => key?.Trim() ?? string.Empty;

        internal List<GameAchievementData> LoadAllGameDataFast()
        {
            using (PerfScope.Start(_logger, "Cache.LoadAllGameDataFast", thresholdMs: 25))
            {
                var scopeChanged = false;

                try
                {
                    lock (_sync)
                    {
                        EnsureReady_Locked("LoadAllGameDataFast");
                        scopeChanged = RefreshScopeToken_Locked(clearMemoryOnChange: true);

                        var records = _store.LoadAllCurrentUserGameDataByCacheKey() ??
                                      new List<KeyValuePair<string, GameAchievementData>>();
                        var result = new List<GameAchievementData>(records.Count);
                        var validKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                        for (var i = 0; i < records.Count; i++)
                        {
                            var record = records[i];
                            var cacheKey = UserKey(record.Key);
                            if (string.IsNullOrWhiteSpace(cacheKey))
                            {
                                continue;
                            }

                            validKeys.Add(cacheKey);

                            var dbData = record.Value;
                            if (dbData == null)
                            {
                                RemoveMemoryGameData_Locked(cacheKey);
                                continue;
                            }

                            NormalizeLoadedData(cacheKey, dbData);

                            if (TryGetMemoryGameData_Locked(cacheKey, out var memoryData) && memoryData != null)
                            {
                                var memoryUpdated = DateTimeUtilities.AsUtcKind(memoryData.LastUpdatedUtc);
                                var dbUpdated = DateTimeUtilities.AsUtcKind(dbData.LastUpdatedUtc);
                                if (memoryUpdated >= dbUpdated)
                                {
                                    result.Add(CloneGameData(memoryData));
                                    continue;
                                }
                            }

                            SetMemoryGameData_Locked(cacheKey, dbData);
                            result.Add(CloneGameData(dbData));
                        }

                        RemoveStaleMemoryEntries_Locked(validKeys);
                        return result;
                    }
                }
                catch (Exception ex)
                {
                    _logger?.Error(ex, "Failed loading all game data from cache.");
                    return new List<GameAchievementData>();
                }
                finally
                {
                    if (scopeChanged)
                    {
                        RaiseCacheDeltaUpdatedEvent(string.Empty, CacheDeltaOperationType.FullReset);
                    }
                }
            }
        }

        public GameAchievementData LoadGameData(string key)
        {
            using (PerfScope.Start(_logger, "Cache.LoadGameData", thresholdMs: 25, context: key))
            {
                var scopeChanged = false;
                var normalizedKey = UserKey(key);

                try
                {
                    if (string.IsNullOrWhiteSpace(normalizedKey))
                    {
                        return null;
                    }

                    lock (_sync)
                    {
                        EnsureReady_Locked("LoadGameData");
                        scopeChanged = RefreshScopeToken_Locked(clearMemoryOnChange: true);

                        var dbData = _store.LoadCurrentUserGameData(normalizedKey);
                        if (dbData == null)
                        {
                            RemoveMemoryGameData_Locked(normalizedKey);
                            return null;
                        }

                        NormalizeLoadedData(normalizedKey, dbData);

                        if (TryGetMemoryGameData_Locked(normalizedKey, out var memoryData) && memoryData != null)
                        {
                            var memoryUpdated = DateTimeUtilities.AsUtcKind(memoryData.LastUpdatedUtc);
                            var dbUpdated = DateTimeUtilities.AsUtcKind(dbData.LastUpdatedUtc);
                            if (memoryUpdated >= dbUpdated)
                            {
                                return CloneGameData(memoryData);
                            }
                        }

                        SetMemoryGameData_Locked(normalizedKey, dbData);
                        return CloneGameData(dbData);
                    }
                }
                catch (Exception ex)
                {
                    _logger?.Error(ex, $"Cache read failed. key={normalizedKey}, phase=LoadGameData");
                    return null;
                }
                finally
                {
                    if (scopeChanged)
                    {
                        RaiseCacheDeltaUpdatedEvent(string.Empty, CacheDeltaOperationType.FullReset);
                    }
                }
            }
        }

        public CacheWriteResult SaveGameData(string key, GameAchievementData data)
        {
            using (PerfScope.Start(_logger, "Cache.SaveGameData", thresholdMs: 25, context: key))
            {
                if (string.IsNullOrWhiteSpace(key))
                {
                    return CacheWriteResult.CreateFailure(
                        key,
                        "invalid_key",
                        "Cache key is missing.");
                }

                var normalizedKey = UserKey(key);
                var writeTime = DateTime.UtcNow;
                var providerName = data?.ProviderName;
                var scopeChanged = false;

                if (_initializationFailure != null)
                {
                    return CacheWriteResult.CreateFailure(
                        normalizedKey,
                        "cache_unavailable",
                        "Cache initialization failed; writes are disabled to prevent data loss.",
                        _initializationFailure);
                }

                try
                {
                    lock (_sync)
                    {
                        EnsureReady_Locked("SaveGameData");

                        var toWrite = data != null ? CloneGameData(data) : new GameAchievementData();
                        if (toWrite.LastUpdatedUtc == default(DateTime))
                        {
                            toWrite.LastUpdatedUtc = writeTime;
                        }
                        toWrite.LastUpdatedUtc = DateTimeUtilities.AsUtcKind(toWrite.LastUpdatedUtc);

                        if (toWrite.PlayniteGameId == null && Guid.TryParse(normalizedKey, out var parsedId))
                        {
                            toWrite.PlayniteGameId = parsedId;
                        }

                        _store.SaveCurrentUserGameData(normalizedKey, toWrite);

                        scopeChanged = RefreshScopeToken_Locked(clearMemoryOnChange: true);
                        SetMemoryGameData_Locked(normalizedKey, toWrite);
                    }
                }
                catch (Exception ex)
                {
                    _logger?.Error(
                        ex,
                        $"Cache write failed. key={normalizedKey}, provider={providerName ?? "unknown"}, " +
                        "phase=SaveGameData, operation=SaveCurrentUserGameData");

                    return CacheWriteResult.CreateFailure(
                        normalizedKey,
                        "sql_write_failed",
                        ex.Message,
                        ex);
                }

                if (scopeChanged)
                {
                    RaiseCacheDeltaUpdatedEvent(string.Empty, CacheDeltaOperationType.FullReset);
                }

                RaiseGameCacheUpdatedEvent(normalizedKey);
                RaiseCacheDeltaUpdatedEvent(normalizedKey, CacheDeltaOperationType.Upsert);

                return CacheWriteResult.CreateSuccess(normalizedKey, writeTime);
            }
        }

        public void RemoveGameData(Guid playniteGameId)
        {
            if (playniteGameId == Guid.Empty)
            {
                return;
            }

            var cacheKey = playniteGameId.ToString();
            try
            {
                lock (_sync)
                {
                    EnsureReady_Locked("RemoveGameData");

                    var normalized = UserKey(cacheKey);
                    RemoveMemoryGameData_Locked(normalized);

                    _store.RemoveGameData(playniteGameId);
                }

                RaiseGameCacheUpdatedEvent(cacheKey);
                RaiseCacheDeltaUpdatedEvent(cacheKey, CacheDeltaOperationType.Remove);
                RaiseCacheInvalidatedEvent();
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed removing cached data for gameId={playniteGameId}");
            }
        }

        public void NotifyCacheInvalidated()
        {
            RaiseCacheInvalidatedEvent();
        }

        public string ExportDatabaseToCsv(string exportDirectory)
        {
            lock (_sync)
            {
                EnsureReady_Locked("ExportDatabaseToCsv");
                return _store.ExportToCsv(exportDirectory);
            }
        }

        public void Dispose()
        {
            try
            {
                _store?.Dispose();
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Failed to dispose SQLNado cache store.");
            }
        }

        private static void NormalizeLoadedData(string cacheKey, GameAchievementData data)
        {
            if (data == null)
            {
                return;
            }

            data.LastUpdatedUtc = DateTimeUtilities.AsUtcKind(data.LastUpdatedUtc);

            if (data.PlayniteGameId == null && Guid.TryParse(cacheKey, out var parsedId))
            {
                data.PlayniteGameId = parsedId;
            }
        }

        private static GameAchievementData CloneGameData(GameAchievementData source)
        {
            if (source == null)
            {
                return null;
            }

            var copy = new GameAchievementData
            {
                LastUpdatedUtc = DateTimeUtilities.AsUtcKind(source.LastUpdatedUtc),
                ProviderName = source.ProviderName,
                LibrarySourceName = source.LibrarySourceName,
                HasAchievements = source.HasAchievements,
                ExcludedByUser = source.ExcludedByUser,
                GameName = source.GameName,
                AppId = source.AppId,
                PlayniteGameId = source.PlayniteGameId,
                Game = source.Game,
                Achievements = new List<AchievementDetail>()
            };

            if (source.Achievements == null || source.Achievements.Count == 0)
            {
                return copy;
            }

            for (var i = 0; i < source.Achievements.Count; i++)
            {
                var achievement = source.Achievements[i];
                if (achievement == null)
                {
                    continue;
                }

                copy.Achievements.Add(new AchievementDetail
                {
                    ApiName = achievement.ApiName,
                    DisplayName = achievement.DisplayName,
                    Description = achievement.Description,
                    UnlockedIconPath = achievement.UnlockedIconPath,
                    LockedIconPath = achievement.LockedIconPath,
                    Points = achievement.Points,
                    ScaledPoints = achievement.ScaledPoints,
                    CategoryType = achievement.CategoryType,
                    Category = achievement.Category,
                    TrophyType = achievement.TrophyType,
                    Hidden = achievement.Hidden,
                    IsCapstone = achievement.IsCapstone,
                    UnlockTimeUtc = achievement.UnlockTimeUtc.HasValue
                        ? DateTimeUtilities.AsUtcKind(achievement.UnlockTimeUtc.Value)
                        : (DateTime?)null,
                    Unlocked = achievement.Unlocked,
                    GlobalPercentUnlocked = achievement.GlobalPercentUnlocked,
                    ProgressNum = achievement.ProgressNum,
                    ProgressDenom = achievement.ProgressDenom
                });
            }

            return copy;
        }
    }

    public class GameCacheUpdatedEventArgs : EventArgs
    {
        public string GameId { get; }
        public GameCacheUpdatedEventArgs(string gameId)
        {
            GameId = gameId;
        }
    }
}

