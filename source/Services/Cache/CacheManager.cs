using System;
using System.Collections.Generic;
using System.IO;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Friends;
using PlayniteAchievements.Services.Database;
using PlayniteAchievements.Services.Friends;
using PlayniteAchievements.Services.Images;
using Playnite.SDK;
using System.Windows;

namespace PlayniteAchievements.Services.Cache
{
    internal interface ICacheReadOptimizations
    {
        List<GameAchievementData> LoadAllGameDataFast();

        CachedSummaryData LoadCachedSummaryDataFast(int recentAchievementDetailLimit = 0);
    }

    // Write access to the AchievementFilters mirror table — the summary queries' SQL-side view
    // of the per-game achievement filter lists that live in the separate custom-data database.
    internal interface IAchievementFilterMirror
    {
        void ReplaceAchievementFilters(Guid playniteGameId, IReadOnlyList<(string ApiName, string Kind)> entries);

        void ResyncAllAchievementFilters(IReadOnlyDictionary<Guid, IReadOnlyList<(string ApiName, string Kind)>> entriesByGameId);
    }

    public sealed class CacheManager : ICacheManager, ICacheReadOptimizations, IAchievementFilterMirror, IFriendCacheManager, IDisposable
    {
        private const int MaxInMemoryGames = 256;

        private sealed class CacheEntry
        {
            public GameAchievementData Data { get; set; }
            public LinkedListNode<string> Node { get; set; }
        }

        private sealed class FriendCacheInvalidationBatch : IFriendCacheInvalidationBatch
        {
            private CacheManager _owner;
            private bool _disposed;

            public FriendCacheInvalidationBatch(CacheManager owner)
            {
                _owner = owner;
            }

            public void Flush()
            {
                if (_disposed)
                {
                    return;
                }

                _owner?.FlushFriendCacheInvalidationBatch();
            }

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                var owner = _owner;
                _owner = null;
                owner?.EndFriendCacheInvalidationBatch();
            }
        }

        private readonly IPlayniteAPI _api;
        private readonly ILogger _logger;
        private readonly PlayniteAchievementsPlugin _plugin;
        private readonly CacheStorage _storage;
        private readonly SqlNadoCacheStore _store;
        private readonly LegacyJsonCacheImporter _importer;
        private readonly DiskImageService _diskImageService;

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
        private int _friendCacheInvalidationBatchDepth;
        private bool _friendCacheInvalidationBatchDirty;
        // Scope accumulated across deferred raises within a batch window (guarded by _sync);
        // drained on every flush so scope travels with the event, not the batch lifetime.
        private readonly FriendCacheInvalidationScopeAccumulator _friendCacheInvalidationScope =
            new FriendCacheInvalidationScopeAccumulator();

        // Bumped under _sync on every in-memory mutation. LoadAllGameDataFast reads the
        // whole-library snapshot off _sync, so it uses this to detect writes that landed during
        // that read and skip stale-entry pruning (which could otherwise drop a freshly written
        // game absent from the snapshot).
        private long _writeGeneration;

        public event EventHandler<GameCacheUpdatedEventArgs> GameCacheUpdated;
        public event EventHandler<CacheDeltaEventArgs> CacheDeltaUpdated;
        public event EventHandler<CacheInvalidatedEventArgs> CacheInvalidated;
        public event EventHandler<FriendCacheInvalidatedEventArgs> FriendCacheInvalidated;

        event EventHandler<FriendCacheInvalidatedEventArgs> IFriendCacheManager.FriendCacheInvalidated
        {
            add => FriendCacheInvalidated += value;
            remove => FriendCacheInvalidated -= value;
        }

        public CacheManager(IPlayniteAPI api, ILogger logger, PlayniteAchievementsPlugin plugin, DiskImageService diskImageService)
        {
            _api = api;
            _logger = logger;
            _plugin = plugin;
            _storage = new CacheStorage(plugin, logger);
            _store = new SqlNadoCacheStore(plugin, logger, _storage.BaseDir);
            _importer = new LegacyJsonCacheImporter(_storage, _store, logger);
            _diskImageService = diskImageService ?? throw new ArgumentNullException(nameof(diskImageService));

            InitializeCacheStartup();
        }

        // Applies definition ApiName renames to the game's custom data (notes, order, filters,
        // overrides) so user-authored data follows renamed/rekeyed achievements. Resolved lazily
        // from the plugin because the custom-data store is wired during plugin initialization.
        private void ApplyDefinitionRenamesToCustomData(Guid? playniteGameId, Dictionary<string, string> renamedApiNames)
        {
            if (renamedApiNames == null || renamedApiNames.Count == 0 ||
                playniteGameId == null || playniteGameId.Value == Guid.Empty)
            {
                return;
            }

            try
            {
                _plugin?.GameCustomDataStore?.RenameAchievementApiNames(playniteGameId.Value, renamedApiNames);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed to rewrite custom-data achievement keys for game {playniteGameId}.");
            }
        }

        List<GameAchievementData> ICacheReadOptimizations.LoadAllGameDataFast()
        {
            return LoadAllGameDataFast();
        }

        CachedSummaryData ICacheReadOptimizations.LoadCachedSummaryDataFast(int recentAchievementDetailLimit)
        {
            return LoadCachedSummaryDataFast(recentAchievementDetailLimit);
        }

        // Mirror writes never throw: when the store failed to initialize, summaries are
        // unavailable anyway and the summary reader fails open (empty table = unfiltered).
        void IAchievementFilterMirror.ReplaceAchievementFilters(
            Guid playniteGameId,
            IReadOnlyList<(string ApiName, string Kind)> entries)
        {
            try
            {
                _store.ReplaceAchievementFilters(playniteGameId, entries);
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"Failed to sync achievement filter mirror for game {playniteGameId}.");
            }
        }

        void IAchievementFilterMirror.ResyncAllAchievementFilters(
            IReadOnlyDictionary<Guid, IReadOnlyList<(string ApiName, string Kind)>> entriesByGameId)
        {
            try
            {
                var changedGames = _store.ResyncAllAchievementFilters(entriesByGameId);
                _logger?.Debug(changedGames > 0
                    ? $"[Filters] Achievement filter mirror resynced for {changedGames} game(s)."
                    : "[Filters] Achievement filter mirror unchanged.");
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, "Failed to resync achievement filter mirror.");
            }
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
            catch (Exception dialogEx)
            {
                _logger?.Debug(dialogEx, "[Cache] Failed to show cache startup-failure dialog.");
            }
        }

        private void RaiseGameCacheUpdatedEvent(string gameId)
        {
            try { GameCacheUpdated?.Invoke(this, new GameCacheUpdatedEventArgs(gameId)); }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Failed to notify cache subscribers.");
            }
        }

        private void RaiseCacheDeltaUpdatedEvent(string key, CacheDeltaOperationType operationType)
        {
            try { CacheDeltaUpdated?.Invoke(this, new CacheDeltaEventArgs(key, operationType, DateTime.UtcNow)); }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Failed to notify cache subscribers.");
            }
        }

        private void RaiseCacheInvalidatedEvent(CacheInvalidatedEventArgs args = null)
        {
            try { CacheInvalidated?.Invoke(this, args ?? CacheInvalidatedEventArgs.FullInvalidation); }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Failed to notify cache subscribers.");
            }
        }

        private void RaiseFriendCacheInvalidatedEvent(FriendCacheInvalidatedEventArgs args)
        {
            args = args ?? FriendCacheInvalidatedEventArgs.FullInvalidation;
            _logger?.Debug(
                $"[RefreshPerf] kind=friends phase=invalidation.raise full={args.IsFull} changes={args.Changes.Count}");
            try { FriendCacheInvalidated?.Invoke(this, args); }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Failed to notify cache subscribers.");
            }
        }

        private void RaiseOrDeferFriendCacheInvalidatedEvent(FriendCacheChange change)
        {
            if (_friendCacheInvalidationBatchDepth > 0)
            {
                _friendCacheInvalidationBatchDirty = true;
                _friendCacheInvalidationScope.Add(change);
                return;
            }

            RaiseFriendCacheInvalidatedEvent(
                change == null
                    ? FriendCacheInvalidatedEventArgs.FullInvalidation
                    : FriendCacheInvalidatedEventArgs.Scoped(new[] { change }));
        }

        private void FlushFriendCacheInvalidationBatch()
        {
            FriendCacheInvalidatedEventArgs args = null;
            lock (_sync)
            {
                if (_friendCacheInvalidationBatchDirty)
                {
                    _friendCacheInvalidationBatchDirty = false;
                    args = _friendCacheInvalidationScope.Drain();
                }
            }

            if (args != null)
            {
                RaiseFriendCacheInvalidatedEvent(args);
            }
        }

        private void EndFriendCacheInvalidationBatch()
        {
            FriendCacheInvalidatedEventArgs args = null;
            lock (_sync)
            {
                if (_friendCacheInvalidationBatchDepth > 0)
                {
                    _friendCacheInvalidationBatchDepth--;
                }

                if (_friendCacheInvalidationBatchDepth == 0 && _friendCacheInvalidationBatchDirty)
                {
                    _friendCacheInvalidationBatchDirty = false;
                    args = _friendCacheInvalidationScope.Drain();
                }
            }

            if (args != null)
            {
                RaiseFriendCacheInvalidatedEvent(args);
            }
        }

        IFriendCacheInvalidationBatch IFriendCacheManager.BeginFriendCacheInvalidationBatch()
        {
            lock (_sync)
            {
                _friendCacheInvalidationBatchDepth++;
            }

            return new FriendCacheInvalidationBatch(this);
        }

        private void ClearMemoryState_Locked()
        {
            _userAchievements = new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);
            _lruOrder = new LinkedList<string>();
            _writeGeneration++;
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
            _writeGeneration++;
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
                _writeGeneration++;
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

        // Briefly takes _sync to initialize the store and surface any initialization failure,
        // then releases it so the caller can run a long read-only query on the store's read
        // connection without blocking short per-game reads.
        private void EnsureReadyForRead(string operationPhase)
        {
            lock (_sync)
            {
                EnsureReady_Locked(operationPhase);
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
                    long startGeneration;
                    lock (_sync)
                    {
                        EnsureReady_Locked("LoadAllGameDataFast");
                        scopeChanged = RefreshScopeToken_Locked(clearMemoryOnChange: true);
                        startGeneration = _writeGeneration;
                    }

                    // Read the whole-library snapshot off _sync (on the store's read connection)
                    // so this multi-second scan does not stall short per-game grid reads.
                    var records = _store.LoadAllCurrentUserGameDataByCacheKey() ??
                                  new List<KeyValuePair<string, GameAchievementData>>();

                    lock (_sync)
                    {
                        // A write that landed while the snapshot was read may have added a game
                        // absent from it; skip pruning in that case so the freshly written game
                        // is kept. The per-entry timestamp check below still prevents clobbering
                        // newer in-memory data with the older snapshot.
                        var pruneStaleEntries = _writeGeneration == startGeneration;

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

                        if (pruneStaleEntries)
                        {
                            RemoveStaleMemoryEntries_Locked(validKeys);
                        }

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

        internal CachedSummaryData LoadCachedSummaryDataFast(int recentAchievementDetailLimit = 0)
        {
            using (PerfScope.Start(_logger, "Cache.LoadCachedSummaryDataFast", thresholdMs: 25))
            {
                var scopeChanged = false;

                try
                {
                    lock (_sync)
                    {
                        EnsureReady_Locked("LoadCachedSummaryDataFast");
                        scopeChanged = RefreshScopeToken_Locked(clearMemoryOnChange: true);
                    }

                    // Read off _sync (on the store's read connection) so a concurrent grid read
                    // is not blocked while this summary query runs.
                    return _store.LoadCachedSummaryData(recentAchievementDetailLimit) ?? new CachedSummaryData();
                }
                catch (Exception ex)
                {
                    _logger?.Error(ex, "Failed loading cached summary data from cache.");
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

                        // Cheap freshness probe: when the in-memory copy is at least as new as
                        // the store's row, skip the full two-query read (the same comparison the
                        // fallthrough performs after materializing the row). A null probe (no
                        // row or unparsable timestamp) falls through to the full read, which
                        // stays authoritative for existence and eviction.
                        if (TryGetMemoryGameData_Locked(normalizedKey, out var cachedData) && cachedData != null)
                        {
                            var probedDbUpdated = _store.GetCurrentUserGameLastUpdatedUtc(normalizedKey);
                            if (probedDbUpdated.HasValue &&
                                DateTimeUtilities.AsUtcKind(cachedData.LastUpdatedUtc) >=
                                DateTimeUtilities.AsUtcKind(probedDbUpdated.Value))
                            {
                                return CloneGameData(cachedData);
                            }
                        }

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
                var providerKey = data?.ProviderKey;
                var scopeChanged = false;
                Dictionary<string, string> renamedApiNames = null;
                Guid? renamedPlayniteGameId = null;

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

                        renamedApiNames = _store.SaveCurrentUserGameData(normalizedKey, toWrite);
                        renamedPlayniteGameId = toWrite.PlayniteGameId;

                        scopeChanged = RefreshScopeToken_Locked(clearMemoryOnChange: true);
                        SetMemoryGameData_Locked(normalizedKey, toWrite);
                    }
                }
                catch (Exception ex)
                {
                    _logger?.Error(
                        ex,
                        $"Cache write failed. key={normalizedKey}, provider={providerKey ?? "unknown"}, " +
                        "phase=SaveGameData, operation=SaveCurrentUserGameData");

                    return CacheWriteResult.CreateFailure(
                        normalizedKey,
                        "sql_write_failed",
                        ex.Message,
                        ex);
                }

                // Outside the cache lock: CustomDataChanged handlers are synchronous and may call
                // back into cache invalidation.
                ApplyDefinitionRenamesToCustomData(renamedPlayniteGameId, renamedApiNames);

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

        public void RemoveGameCache(Guid playniteGameId)
        {
            if (playniteGameId == Guid.Empty)
            {
                return;
            }

            RemoveGameData(playniteGameId);

            try
            {
                _diskImageService.ClearGameCache(playniteGameId.ToString());
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"Failed to remove icon cache for game '{playniteGameId}'.");
            }
        }

        public void NotifyCacheInvalidated()
        {
            RaiseCacheInvalidatedEvent(CacheInvalidatedEventArgs.FullInvalidation);
        }

        public void NotifyCacheInvalidated(IReadOnlyList<Guid> changedGameIds)
        {
            RaiseCacheInvalidatedEvent(CacheInvalidatedEventArgs.Scoped(changedGameIds));
        }

        public string ExportDatabaseToCsv(string exportDirectory)
        {
            lock (_sync)
            {
                EnsureReady_Locked("ExportDatabaseToCsv");
                return _store.ExportToCsv(exportDirectory);
            }
        }

        FriendCacheWriteResult IFriendCacheManager.SaveFriendList(
            string providerKey,
            IReadOnlyList<FriendIdentity> friends)
        {
            lock (_sync)
            {
                EnsureReady_Locked("SaveFriendList");
                var result = _store.SaveFriendList(providerKey, friends);
                if (result?.Success == true)
                {
                    RaiseOrDeferFriendCacheInvalidatedEvent(FriendCacheChange.ForRoster(providerKey));
                }

                return result;
            }
        }

        FriendCacheWriteResult IFriendCacheManager.SaveFriendOwnership(
            string providerKey,
            string externalUserId,
            IReadOnlyList<FriendGameOwnership> ownership,
            FriendOwnershipSaveOptions options)
        {
            lock (_sync)
            {
                EnsureReady_Locked("SaveFriendOwnership");
                var result = _store.SaveFriendOwnership(providerKey, externalUserId, ownership, options);
                if (result?.Success == true)
                {
                    RaiseOrDeferFriendCacheInvalidatedEvent(FriendCacheChange.ForFriendOwnership(providerKey, externalUserId));
                }

                return result;
            }
        }

        FriendCacheWriteResult IFriendCacheManager.SaveFriendGameDefinition(
            string providerKey,
            FriendGameDefinition definition)
        {
            FriendCacheWriteResult result;
            lock (_sync)
            {
                EnsureReady_Locked("SaveFriendGameDefinition");
                result = _store.SaveFriendGameDefinition(providerKey, definition);
                if (result?.Success == true)
                {
                    RaiseOrDeferFriendCacheInvalidatedEvent(
                        FriendCacheChange.ForGameDefinition(providerKey, definition?.AppId ?? 0, definition?.ProviderGameKey));
                }
            }

            if (result?.Success == true)
            {
                // Outside the cache lock: CustomDataChanged handlers are synchronous and may call
                // back into cache invalidation.
                ApplyDefinitionRenamesToCustomData(result.RenamedPlayniteGameId, result.RenamedApiNames);
            }

            return result;
        }

        FriendCacheWriteResult IFriendCacheManager.SaveProviderGameImagePaths(
            string providerKey,
            string providerGameKey,
            int appId,
            string iconAbsolutePath,
            string coverAbsolutePath)
        {
            lock (_sync)
            {
                EnsureReady_Locked("SaveProviderGameImagePaths");
                var result = _store.SaveProviderGameImagePaths(providerKey, providerGameKey, appId, iconAbsolutePath, coverAbsolutePath);
                if (result?.Success == true)
                {
                    RaiseOrDeferFriendCacheInvalidatedEvent(
                        FriendCacheChange.ForGameDefinition(providerKey, appId, providerGameKey));
                }

                return result;
            }
        }

        Dictionary<string, FriendGameDefinitionState> IFriendCacheManager.LoadFriendGameDefinitionStates(
            string providerKey,
            IReadOnlyCollection<string> providerGameKeys)
        {
            lock (_sync)
            {
                EnsureReady_Locked("LoadFriendGameDefinitionStates");
                return _store.LoadFriendGameDefinitionStates(providerKey, providerGameKeys) ??
                       new Dictionary<string, FriendGameDefinitionState>(StringComparer.OrdinalIgnoreCase);
            }
        }

        List<string> IFriendCacheManager.LoadLegacyKeyedDefinitionGameKeys(
            string providerKey,
            IReadOnlyCollection<string> providerGameKeys)
        {
            lock (_sync)
            {
                EnsureReady_Locked("LoadLegacyKeyedDefinitionGameKeys");
                return _store.LoadLegacyKeyedDefinitionGameKeys(providerKey, providerGameKeys) ?? new List<string>();
            }
        }

        FriendUnownedCacheStats IFriendCacheManager.GetUnownedFriendGameCacheStats()
        {
            lock (_sync)
            {
                EnsureReady_Locked("GetUnownedFriendGameCacheStats");
                return _store.GetUnownedFriendGameCacheStats() ?? new FriendUnownedCacheStats();
            }
        }

        FriendUnownedCacheClearResult IFriendCacheManager.ClearUnownedFriendGameData()
        {
            lock (_sync)
            {
                EnsureReady_Locked("ClearUnownedFriendGameData");
                var result = _store.ClearUnownedFriendGameData();
                if (result?.Success == true)
                {
                    // Bulk clear has no per-game scope; forces a full invalidation.
                    RaiseOrDeferFriendCacheInvalidatedEvent(null);
                }

                return result;
            }
        }

        FriendCacheWriteResult IFriendCacheManager.ClearUnownedFriendGame(
            string providerKey,
            int appId,
            string providerGameKey)
        {
            lock (_sync)
            {
                EnsureReady_Locked("ClearUnownedFriendGame");
                var result = _store.ClearUnownedFriendGame(providerKey, appId, providerGameKey);
                if (result?.Success == true)
                {
                    RaiseOrDeferFriendCacheInvalidatedEvent(
                        FriendCacheChange.ForGameDefinition(providerKey, appId, providerGameKey));
                }

                return result;
            }
        }

        bool IFriendCacheManager.IsProviderGameMappedToPlayniteLibrary(string providerKey, int appId, string providerGameKey)
        {
            lock (_sync)
            {
                EnsureReady_Locked("IsProviderGameMappedToPlayniteLibrary");
                return _store.IsProviderGameMappedToPlayniteLibrary(providerKey, appId, providerGameKey);
            }
        }

        IReadOnlyList<FriendGameMapping> IFriendCacheManager.LoadFriendGameMappings(string providerKey)
        {
            lock (_sync)
            {
                EnsureReady_Locked("LoadFriendGameMappings");
                return _store.LoadFriendGameMappings(providerKey) ?? new List<FriendGameMapping>();
            }
        }

        FriendCacheWriteResult IFriendCacheManager.PromoteProviderOnlyGameToPlayniteBacked(
            string providerKey,
            int appId,
            string providerGameKey,
            Guid playniteGameId)
        {
            lock (_sync)
            {
                EnsureReady_Locked("PromoteProviderOnlyGameToPlayniteBacked");
                var result = _store.PromoteProviderOnlyGameToPlayniteBacked(providerKey, appId, providerGameKey, playniteGameId);
                if (result?.Success == true && result.WrittenCount > 0)
                {
                    RaiseOrDeferFriendCacheInvalidatedEvent(
                        FriendCacheChange.ForGameDefinition(providerKey, appId, providerGameKey));
                }

                return result;
            }
        }

        FriendCacheWriteResult IFriendCacheManager.SaveFriendGameAchievements(
            string providerKey,
            string externalUserId,
            string providerGameKey,
            int appId,
            FriendGameAchievements achievements)
        {
            FriendCacheWriteResult result;
            lock (_sync)
            {
                EnsureReady_Locked("SaveFriendGameAchievements");
                result = _store.SaveFriendGameAchievements(providerKey, externalUserId, providerGameKey, appId, achievements);
                if (result?.Success == true)
                {
                    RaiseOrDeferFriendCacheInvalidatedEvent(
                        FriendCacheChange.ForFriendGameAchievements(providerKey, externalUserId, appId, providerGameKey));
                }
            }

            if (result?.Success == true)
            {
                // Outside the cache lock: CustomDataChanged handlers are synchronous and may call
                // back into cache invalidation.
                ApplyDefinitionRenamesToCustomData(result.RenamedPlayniteGameId, result.RenamedApiNames);
            }

            return result;
        }

        List<FriendAchievementRow> IFriendCacheManager.LoadFriendGameAchievements(
            string providerKey,
            string externalUserId,
            int appId,
            string providerGameKey)
        {
            lock (_sync)
            {
                EnsureReady_Locked("LoadFriendGameAchievements");
                return _store.LoadFriendGameAchievements(providerKey, externalUserId, appId, providerGameKey) ??
                       new List<FriendAchievementRow>();
            }
        }

        FriendCacheWriteResult IFriendCacheManager.DeleteFriendData(
            string providerKey,
            string externalUserId,
            bool preserveFriendRecord)
        {
            lock (_sync)
            {
                EnsureReady_Locked("DeleteFriendData");
                var result = _store.DeleteFriendData(providerKey, externalUserId, preserveFriendRecord);
                if (result?.Success == true)
                {
                    RaiseOrDeferFriendCacheInvalidatedEvent(
                        FriendCacheChange.ForFriendRemoved(providerKey, externalUserId));
                }

                return result;
            }
        }

        List<FriendIdentity> IFriendCacheManager.LoadFriendIdentities(string providerKey)
        {
            lock (_sync)
            {
                EnsureReady_Locked("LoadFriendIdentities");
                return _store.LoadFriendIdentities(providerKey) ?? new List<FriendIdentity>();
            }
        }

        DateTime? IFriendCacheManager.GetMostRecentFriendLastRefreshedUtc()
        {
            lock (_sync)
            {
                EnsureReady_Locked("GetMostRecentFriendLastRefreshedUtc");
                return _store.GetMostRecentFriendLastRefreshedUtc();
            }
        }

        List<FriendRefreshCandidate> IFriendCacheManager.LoadFriendRefreshCandidates(
            string providerKey,
            FriendRefreshOptions options)
        {
            lock (_sync)
            {
                EnsureReady_Locked("LoadFriendRefreshCandidates");
                return _store.LoadFriendRefreshCandidates(providerKey, options) ??
                       new List<FriendRefreshCandidate>();
            }
        }

        IReadOnlyDictionary<string, FriendOwnershipRecency> IFriendCacheManager.LoadFriendOwnershipRecency(
            string providerKey,
            string externalUserId)
        {
            lock (_sync)
            {
                EnsureReady_Locked("LoadFriendOwnershipRecency");
                return _store.LoadFriendOwnershipRecency(providerKey, externalUserId) ??
                       new Dictionary<string, FriendOwnershipRecency>(StringComparer.OrdinalIgnoreCase);
            }
        }

        FriendsOverviewData IFriendCacheManager.LoadFriendsOverviewData(int recentLimit)
        {
            EnsureReadyForRead("LoadFriendsOverviewData");
            return _store.LoadFriendsOverviewData(recentLimit) ??
                   new FriendsOverviewData();
        }

        FriendsOverviewData IFriendCacheManager.LoadFriendsOverviewPatchData(IReadOnlyList<FriendCacheChange> reloadScopes)
        {
            EnsureReadyForRead("LoadFriendsOverviewPatchData");
            return _store.LoadFriendsOverviewPatchData(reloadScopes) ??
                   new FriendsOverviewData();
        }

        FriendsOverviewData IFriendCacheManager.LoadFriendGameAchievementData(Guid playniteGameId)
        {
            EnsureReadyForRead("LoadFriendGameAchievementData");
            return _store.LoadFriendGameAchievementData(playniteGameId) ??
                   new FriendsOverviewData();
        }

        FriendsOverviewData IFriendCacheManager.LoadFriendGameAchievementData(FriendCacheChange gameScope)
        {
            EnsureReadyForRead("LoadFriendGameAchievementData");
            return _store.LoadFriendGameAchievementData(gameScope) ??
                   new FriendsOverviewData();
        }

        FriendsOverviewData IFriendCacheManager.LoadFriendRecentUnlocksData(int recentLimit)
        {
            EnsureReadyForRead("LoadFriendRecentUnlocksData");
            return _store.LoadFriendRecentUnlocksData(recentLimit) ??
                   new FriendsOverviewData();
        }

        IReadOnlyList<CurrentUserGameLabel> IFriendCacheManager.LoadCurrentUserGameLabels()
        {
            EnsureReadyForRead("LoadCurrentUserGameLabels");
            var records = _store.LoadAllCurrentUserGameDataByCacheKey() ??
                          new List<KeyValuePair<string, GameAchievementData>>();

            var labels = new List<CurrentUserGameLabel>(records.Count);
            for (var i = 0; i < records.Count; i++)
            {
                var data = records[i].Value;
                if (data?.PlayniteGameId == null || data.PlayniteGameId == Guid.Empty)
                {
                    continue;
                }

                labels.Add(new CurrentUserGameLabel
                {
                    PlayniteGameId = data.PlayniteGameId.Value,
                    GameName = data.GameName,
                    ProviderKey = data.ProviderKey,
                    ProviderPlatformKey = data.ProviderPlatformKey,
                    AppId = data.AppId,
                    ProviderGameKey = data.ProviderGameKey
                });
            }

            return labels;
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
                ProviderKey = source.ProviderKey,
                ProviderPlatformKey = source.ProviderPlatformKey,
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
                    ProviderKey = achievement.ProviderKey,
                    UnlockTimeUtc = achievement.UnlockTimeUtc.HasValue
                        ? DateTimeUtilities.AsUtcKind(achievement.UnlockTimeUtc.Value)
                        : (DateTime?)null,
                    Unlocked = achievement.Unlocked,
                    GlobalPercentUnlocked = achievement.GlobalPercentUnlocked,
                    Rarity = achievement.Rarity,
                    ProgressNum = achievement.ProgressNum,
                    ProgressDenom = achievement.ProgressDenom,
                    AchievementNote = achievement.AchievementNote
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

