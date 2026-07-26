using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Friends;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Services.Cache;
using PlayniteAchievements.Services.Friends;
using PlayniteAchievements.Services.Images;
using PlayniteAchievements.Services.ProgressReporting;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Services.Refresh
{
    public partial class RefreshRuntime
    {
        public List<GameAchievementData> AllGameData { get; set; } = new List<GameAchievementData>();
        public Dictionary<Guid, GameAchievementData> GameDataById { get; } = new Dictionary<Guid, GameAchievementData>();

        public RefreshRuntime()
            : this(new TestRuntimeCache(), new PlayniteAchievementsSettings(), null, null)
        {
        }

        internal RefreshRuntime(
            ICacheManager cache,
            PlayniteAchievementsSettings settings,
            DiskImageService diskImageService = null,
            ILogger logger = null)
        {
            _settings = settings ?? new PlayniteAchievementsSettings();
            _logger = logger ?? TestLogger.Instance;
            _cacheService = cache ?? new TestRuntimeCache();
            _diskImageService = diskImageService;
            _progressReportingService = new ProgressReportingService(_logger, action => action?.Invoke());
            _refreshStateManager = new RefreshStateManager();
            _refreshProgressReporter = new RefreshProgressReporter((report, prioritizePending) => Report(report, prioritizePending));
            _providers = Array.Empty<IDataProvider>();
        }

        internal RefreshRuntime(
            ICacheManager cache,
            PlayniteAchievementsSettings settings,
            IPlayniteAPI api,
            IEnumerable<IDataProvider> providers,
            IEnumerable<string> refreshOrder = null,
            DiskImageService diskImageService = null,
            ILogger logger = null)
        {
            var effectiveLogger = logger ?? TestLogger.Instance;
            _api = api;
            _settings = settings ?? new PlayniteAchievementsSettings();
            _logger = effectiveLogger;
            _cacheService = cache ?? new TestRuntimeCache();
            _diskImageService = diskImageService;
            _progressReportingService = new ProgressReportingService(effectiveLogger, action => action?.Invoke());
            _refreshStateManager = new RefreshStateManager();
            _refreshProgressReporter = new RefreshProgressReporter((report, prioritizePending) => Report(report, prioritizePending));
            _providers = providers?.Where(provider => provider != null).ToList() ?? new List<IDataProvider>();
            _targetSelectionResolver = new TargetSelectionResolver(
                api,
                _settings,
                _cacheService,
                effectiveLogger,
                refreshOrder ?? _providers.Select(provider => provider.ProviderKey));
            _refreshRequestPlanner = new RefreshRequestPlanner(
                api,
                _settings,
                effectiveLogger,
                _targetSelectionResolver);
            _providerRegistry = new PlayniteAchievements.Providers.ProviderRegistry(
                _settings,
                refreshOrder ?? _providers.Select(provider => provider.ProviderKey),
                effectiveLogger);
        }

        public virtual bool ValidateCanStartRefresh()
        {
            return true;
        }

        public virtual GameAchievementData GetGameAchievementData(Guid playniteGameId)
        {
            return GameDataById.TryGetValue(playniteGameId, out var data) ? data : null;
        }

        public virtual List<GameAchievementData> GetAllGameAchievementData()
        {
            return AllGameData ?? new List<GameAchievementData>();
        }

        public void RaiseCacheInvalidated()
        {
            _cacheService?.NotifyCacheInvalidated();
        }

        public void RaiseFriendCacheInvalidated()
        {
            if (_cacheService is TestRuntimeCache cache)
            {
                cache.NotifyFriendCacheInvalidated();
            }
        }

        public void RaiseRebuildProgress(ProgressReport report)
        {
            _progressReportingService.Report(this, RebuildProgress, report, prioritizePending: true);
        }

        public void BeginTestRefresh(ProgressReport progress)
        {
            var mode = progress?.Mode ?? RefreshModeType.FriendsFull;
            var operationId = progress?.OperationId ?? Guid.NewGuid();
            _refreshStateManager.TryBeginRun(
                operationId,
                mode,
                progress?.CurrentGameId,
                CancellationToken.None,
                out _,
                out _);

            if (progress != null)
            {
                RaiseRebuildProgress(progress);
            }
        }

        public void EndTestRefresh()
        {
            _refreshStateManager.EndRun();
        }

        internal class TestRuntimeCache : ICacheManager, IFriendCacheManager
        {
#pragma warning disable CS0067
            public event EventHandler<GameCacheUpdatedEventArgs> GameCacheUpdated;
            public event EventHandler<CacheDeltaEventArgs> CacheDeltaUpdated;
            public event EventHandler<CacheInvalidatedEventArgs> CacheInvalidated;
            public event EventHandler<FriendCacheInvalidatedEventArgs> FriendCacheInvalidated;
#pragma warning restore CS0067

            public void EnsureDiskCacheOrClearMemory() { }
            public bool CacheFileExists() => false;
            public bool IsCacheValid() => true;
            public DateTime? GetMostRecentLastUpdatedUtc() => null;
            public List<string> GetCachedGameIds() => new List<string>();
            public GameAchievementData LoadGameData(string key) => null;
            public CacheWriteResult SaveGameData(string key, GameAchievementData data) => null;
            public void RemoveGameData(Guid playniteGameId) { }
            public void RemoveGameCache(Guid playniteGameId) { }
            public void NotifyCacheInvalidated() =>
                CacheInvalidated?.Invoke(this, CacheInvalidatedEventArgs.FullInvalidation);
            public void NotifyCacheInvalidated(IReadOnlyList<Guid> changedGameIds) =>
                CacheInvalidated?.Invoke(this, CacheInvalidatedEventArgs.Scoped(changedGameIds));
            public void NotifyFriendCacheInvalidated() =>
                FriendCacheInvalidated?.Invoke(this, FriendCacheInvalidatedEventArgs.FullInvalidation);
            public IFriendCacheInvalidationBatch BeginFriendCacheInvalidationBatch() => NullFriendCacheInvalidationBatch.Instance;
            public void ClearCache() { }
            public string ExportDatabaseToCsv(string exportDirectory) => null;

            public FriendCacheWriteResult SaveFriendList(string providerKey, IReadOnlyList<FriendIdentity> friends) =>
                FriendCacheWriteResult.Ok(friends?.Count ?? 0, friends?.Count ?? 0, 0);

            public FriendCacheWriteResult SaveFriendOwnership(
                string providerKey,
                string externalUserId,
                IReadOnlyList<FriendGameOwnership> ownership,
                FriendOwnershipSaveOptions options = null) =>
                FriendCacheWriteResult.Ok(ownership?.Count ?? 0, ownership?.Count ?? 0, 0);

            public FriendCacheWriteResult SaveFriendGameDefinition(string providerKey, FriendGameDefinition definition) =>
                FriendCacheWriteResult.Ok();

            public FriendCacheWriteResult SaveProviderGameImagePaths(
                string providerKey,
                string providerGameKey,
                int appId,
                string iconAbsolutePath,
                string coverAbsolutePath) =>
                FriendCacheWriteResult.Ok();

            public Dictionary<string, FriendGameDefinitionState> LoadFriendGameDefinitionStates(
                string providerKey,
                IReadOnlyCollection<string> providerGameKeys) =>
                new Dictionary<string, FriendGameDefinitionState>(StringComparer.OrdinalIgnoreCase);

            public List<string> LoadLegacyKeyedDefinitionGameKeys(
                string providerKey,
                IReadOnlyCollection<string> providerGameKeys) =>
                new List<string>();

            public FriendUnownedCacheStats GetUnownedFriendGameCacheStats() => new FriendUnownedCacheStats();
            public FriendUnownedCacheClearResult ClearUnownedFriendGameData() => new FriendUnownedCacheClearResult { Success = true };
            public FriendCacheWriteResult ClearUnownedFriendGame(string providerKey, int appId, string providerGameKey) => FriendCacheWriteResult.Ok();

            public bool IsProviderGameMappedToPlayniteLibrary(string providerKey, int appId, string providerGameKey) => true;

            public System.Collections.Generic.IReadOnlyList<FriendGameMapping> LoadFriendGameMappings(string providerKey) =>
                new System.Collections.Generic.List<FriendGameMapping>();

            public FriendCacheWriteResult PromoteProviderOnlyGameToPlayniteBacked(
                string providerKey,
                int appId,
                string providerGameKey,
                Guid playniteGameId) =>
                FriendCacheWriteResult.Ok();

            public FriendCacheWriteResult SaveFriendGameAchievements(
                string providerKey,
                string externalUserId,
                string providerGameKey,
                int appId,
                FriendGameAchievements achievements) =>
                FriendCacheWriteResult.Ok();

            public List<FriendAchievementRow> LoadFriendGameAchievements(
                string providerKey,
                string externalUserId,
                int appId,
                string providerGameKey) =>
                new List<FriendAchievementRow>();

            public FriendCacheWriteResult DeleteFriendData(string providerKey, string externalUserId, bool preserveFriendRecord = false) =>
                FriendCacheWriteResult.Ok();

            public List<FriendIdentity> LoadFriendIdentities(string providerKey) => new List<FriendIdentity>();

            public DateTime? GetMostRecentFriendLastRefreshedUtc() => null;

            public List<FriendRefreshCandidate> LoadFriendRefreshCandidates(string providerKey, FriendRefreshOptions options) =>
                new List<FriendRefreshCandidate>();

            public IReadOnlyDictionary<string, FriendOwnershipRecency> LoadFriendOwnershipRecency(string providerKey, string externalUserId) =>
                new Dictionary<string, FriendOwnershipRecency>();

            public FriendsOverviewData LoadFriendsOverviewData(int recentLimit) => new FriendsOverviewData();
            public FriendsOverviewData LoadFriendsOverviewPatchData(IReadOnlyList<FriendCacheChange> reloadScopes) => new FriendsOverviewData();
            public FriendsOverviewData LoadFriendGameAchievementData(Guid playniteGameId) => new FriendsOverviewData();
            public FriendsOverviewData LoadFriendGameAchievementData(FriendCacheChange gameScope) => new FriendsOverviewData();
            public FriendsOverviewData LoadFriendRecentUnlocksData(int recentLimit) => new FriendsOverviewData();
            public IReadOnlyList<CurrentUserGameLabel> LoadCurrentUserGameLabels() => new List<CurrentUserGameLabel>();
        }
    }

    internal sealed class CacheManager : RefreshRuntime.TestRuntimeCache, IDisposable
    {
        public CacheManager(
            IPlayniteAPI api,
            ILogger logger,
            PlayniteAchievementsPlugin plugin,
            DiskImageService diskImageService)
        {
        }

        public void Dispose()
        {
        }
    }

    internal sealed class TestLogger : ILogger
    {
        public static readonly TestLogger Instance = new TestLogger();

        private TestLogger()
        {
        }

        public void Debug(string message) { }
        public void Debug(Exception exception, string message) { }
        public void Trace(string message) { }
        public void Trace(Exception exception, string message) { }
        public void Info(string message) { }
        public void Info(Exception exception, string message) { }
        public void Warn(string message) { }
        public void Warn(Exception exception, string message) { }
        public void Error(string message) { }
        public void Error(Exception exception, string message) { }
    }
}
