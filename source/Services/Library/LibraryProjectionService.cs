using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Playnite.SDK;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.ThemeIntegration;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.Cache;
using PlayniteAchievements.Services.GameCustomData;
using PlayniteAchievements.Services.Overview;
using PlayniteAchievements.Services.ThemeIntegration;

namespace PlayniteAchievements.Services.Library
{
    internal sealed class LibraryProjectionService : IDisposable
    {
        private const int WarmDebounceMs = 1500;
        private static readonly TimeSpan MinEagerWarmInterval = TimeSpan.FromSeconds(30);

        private readonly object _sync = new object();
        private readonly AchievementDataService _achievementDataService;
        private readonly IReadOnlyList<IDataProvider> _providers;
        private readonly IPlayniteAPI _api;
        private readonly ICacheManager _cacheManager;
        private readonly GameCustomDataStore _customDataStore;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly Func<bool> _isRefreshActive;
        private readonly ILogger _logger;
        private readonly Dictionary<string, LibraryProjectionSnapshot> _cache =
            new Dictionary<string, LibraryProjectionSnapshot>(StringComparer.Ordinal);
        private readonly Dictionary<string, InFlightBuild> _inFlight =
            new Dictionary<string, InFlightBuild>(StringComparer.Ordinal);
        private int _cacheGeneration;
        private int _warmGeneration;
        private DateTime _lastWarmStartedUtc;
        private bool _warmSuppressed;
        private bool _disposed;

        public LibraryProjectionService(
            AchievementDataService achievementDataService,
            IReadOnlyList<IDataProvider> providers,
            IPlayniteAPI api,
            PlayniteAchievementsSettings settings,
            ICacheManager cacheManager,
            GameCustomDataStore customDataStore,
            ILogger logger,
            Func<bool> isRefreshActive = null)
        {
            _achievementDataService = achievementDataService ?? throw new ArgumentNullException(nameof(achievementDataService));
            _providers = providers ?? new List<IDataProvider>();
            _api = api;
            _cacheManager = cacheManager;
            _customDataStore = customDataStore;
            _settings = settings;
            _isRefreshActive = isRefreshActive;
            _logger = logger;

            if (_cacheManager != null)
            {
                _cacheManager.CacheInvalidated += OnProjectionSourceChanged;
                _cacheManager.CacheDeltaUpdated += OnProjectionSourceChanged;
            }

            if (_customDataStore != null)
            {
                _customDataStore.CustomDataChanged += OnProjectionSourceChanged;
            }

            if (_settings?.Persisted != null)
            {
                _settings.Persisted.PropertyChanged += OnPersistedSettingsChanged;
            }
        }

        public OverviewDataSnapshot GetOverviewSnapshot(
            PlayniteAchievementsSettings settings,
            CancellationToken token)
        {
            var snapshot = GetOrBuild(
                "overview",
                useCache: true,
                build: () => BuildOverview(settings ?? _settings, token));
            return snapshot?.OverviewSnapshot ?? new OverviewDataSnapshot();
        }

        public LibraryRuntimeState GetThemeLightState(
            int recentUnlockLimit,
            CancellationToken token,
            out bool usedCachedSummary,
            out int? hydratedGameCount)
        {
            var normalizedLimit = Math.Max(0, recentUnlockLimit);
            var snapshot = GetOrBuild(
                "theme-light:" + normalizedLimit,
                useCache: true,
                build: () => BuildThemeLight(normalizedLimit, token));

            usedCachedSummary = snapshot?.UsedCachedSummary == true;
            hydratedGameCount = snapshot?.HydratedGameCount;
            return snapshot?.LibraryState ?? new LibraryRuntimeState();
        }

        public LibraryRuntimeState GetThemeFullState(
            CancellationToken token,
            out int? hydratedGameCount)
        {
            var snapshot = GetOrBuild(
                "theme-full",
                useCache: true,
                build: () => BuildThemeFull(token));

            hydratedGameCount = snapshot?.HydratedGameCount;
            return snapshot?.LibraryState ?? new LibraryRuntimeState();
        }

        public void Invalidate()
        {
            lock (_sync)
            {
                _cacheGeneration++;
                _cache.Clear();
            }

            ScheduleWarm();
        }

        // Triggers the first background warm. Called once Playnite has finished starting so the
        // warmed snapshot resolves game presentation (cover, icon, playtime, last played) against
        // a populated game database rather than baking in blank values during early startup.
        public void Warm()
        {
            ScheduleWarm();
        }

        // While a game session is active the background warm is skipped: the in-game poller's
        // periodic saves would otherwise rebuild the whole-library projection every tick, and that
        // rebuild holds the store lock long enough to stall UI-thread cache reads. Invalidate()
        // still clears the snapshot cache on every delta, so on-demand consumers always rebuild
        // with fresh data; only the precompute is dropped. The post-session warm comes from the
        // stopped-game refresh's own cache delta (or an explicit Warm() when that refresh is
        // skipped), so deactivation itself schedules nothing.
        public void SetGameSessionActive(bool active)
        {
            lock (_sync)
            {
                _warmSuppressed = active;
            }
        }

        public void Dispose()
        {
            _disposed = true;

            lock (_sync)
            {
                _inFlight.Clear();
            }

            if (_cacheManager != null)
            {
                _cacheManager.CacheInvalidated -= OnProjectionSourceChanged;
                _cacheManager.CacheDeltaUpdated -= OnProjectionSourceChanged;
            }

            if (_customDataStore != null)
            {
                _customDataStore.CustomDataChanged -= OnProjectionSourceChanged;
            }

            if (_settings?.Persisted != null)
            {
                _settings.Persisted.PropertyChanged -= OnPersistedSettingsChanged;
            }
        }

        private LibraryProjectionSnapshot GetOrBuild(
            string key,
            bool useCache,
            Func<LibraryProjectionSnapshot> build)
        {
            if (!useCache)
            {
                // Caller-specific builds (e.g. overview with revealed spoiler keys) capture
                // per-caller state and must never be shared or cached.
                ThrowIfDisposed();
                return build() ?? new LibraryProjectionSnapshot();
            }

            while (true)
            {
                InFlightBuild flight;
                bool owner = false;

                lock (_sync)
                {
                    ThrowIfDisposed();
                    if (_cache.TryGetValue(key, out var cached))
                    {
                        return cached;
                    }

                    var generation = _cacheGeneration;
                    if (_inFlight.TryGetValue(key, out var existing) && existing.Generation == generation)
                    {
                        flight = existing;
                    }
                    else
                    {
                        // Either no build is running for this key, or the running one started
                        // before the latest Invalidate(). Start a fresh build; a superseded
                        // build keeps running but fails the ReferenceEquals check below, so its
                        // result is returned to its own callers without being stored as current.
                        flight = new InFlightBuild
                        {
                            Generation = generation,
                            Task = Task.Run(build)
                        };
                        _inFlight[key] = flight;
                        owner = true;
                    }
                }

                LibraryProjectionSnapshot snapshot = null;
                try
                {
                    try
                    {
                        snapshot = flight.Task.GetAwaiter().GetResult() ?? new LibraryProjectionSnapshot();
                    }
                    catch when (!owner)
                    {
                        // A joined build faulted or was canceled via its owner's token. Retry
                        // instead of propagating another caller's exception; the next iteration
                        // finds a cached snapshot, joins a newer build, or becomes the owner.
                        snapshot = null;
                    }
                }
                finally
                {
                    lock (_sync)
                    {
                        if (_inFlight.TryGetValue(key, out var current) && ReferenceEquals(current, flight))
                        {
                            _inFlight.Remove(key);
                            if (snapshot != null && flight.Generation == _cacheGeneration)
                            {
                                _cache[key] = snapshot;
                            }
                        }
                    }
                }

                if (snapshot != null)
                {
                    return snapshot;
                }
            }
        }

        // Shares one build per cache key across concurrent callers. Without this, a burst of
        // invalidations (e.g. per-game saves during a bulk refresh) spawns overlapping
        // whole-library builds whose combined allocations can exhaust memory.
        private sealed class InFlightBuild
        {
            public Task<LibraryProjectionSnapshot> Task;
            public int Generation;
        }

        private LibraryProjectionSnapshot BuildOverview(
            PlayniteAchievementsSettings settings,
            CancellationToken token)
        {
            var builder = new OverviewDataBuilder(
                _achievementDataService,
                _providers,
                _api,
                _logger);

            return new LibraryProjectionSnapshot
            {
                OverviewSnapshot = builder.Build(settings, token)
            };
        }

        private LibraryProjectionSnapshot BuildThemeLight(int recentUnlockLimit, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            var summaryData = _achievementDataService.GetCachedSummaryDataForTheme(recentUnlockLimit);
            if (summaryData != null)
            {
                return new LibraryProjectionSnapshot
                {
                    UsedCachedSummary = true,
                    LibraryState = LibraryRuntimeStateBuilder.BuildFromCachedSummary(summaryData, _api, token, _customDataStore)
                };
            }

            var allData = _achievementDataService.GetAllVisibleGameAchievementDataForTheme() ??
                          new List<GameAchievementData>();
            token.ThrowIfCancellationRequested();

            return new LibraryProjectionSnapshot
            {
                UsedCachedSummary = false,
                HydratedGameCount = allData.Count,
                LibraryState = LibraryRuntimeStateBuilder.Build(
                    allData,
                    _api,
                    token,
                    includeHeavyAchievementLists: false)
            };
        }

        private LibraryProjectionSnapshot BuildThemeFull(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            var allData = _achievementDataService.GetAllVisibleGameAchievementDataForTheme() ??
                          new List<GameAchievementData>();
            token.ThrowIfCancellationRequested();

            return new LibraryProjectionSnapshot
            {
                UsedCachedSummary = false,
                HydratedGameCount = allData.Count,
                LibraryState = LibraryRuntimeStateBuilder.Build(
                    allData,
                    _api,
                    token,
                    includeHeavyAchievementLists: true)
            };
        }

        private void OnProjectionSourceChanged(object sender, EventArgs e)
        {
            Invalidate();
        }

        private void OnPersistedSettingsChanged(object sender, PropertyChangedEventArgs e)
        {
            Invalidate();
        }

        private void ScheduleWarm()
        {
            lock (_sync)
            {
                if (_warmSuppressed)
                {
                    return;
                }
            }

            // During a bulk refresh every per-game save invalidates the projection; eagerly
            // rebuilding the whole-library snapshot after each one is wasted work (and the
            // rebuild storm can exhaust memory). Invalidate() has already cleared the cache, so
            // on-demand consumers still rebuild with fresh data mid-refresh; only the precompute
            // is skipped. No end-of-refresh warm is needed here: RefreshRuntime ends the run
            // (making this predicate false) before raising CacheInvalidated, so the final
            // invalidation schedules the one post-refresh warm. Invoked outside _sync because
            // the predicate takes the refresh state manager's own lock.
            if (_isRefreshActive?.Invoke() == true)
            {
                return;
            }

            var generation = Interlocked.Increment(ref _warmGeneration);
            _ = WarmAfterDelayAsync(generation);
        }

        private async Task WarmAfterDelayAsync(int generation)
        {
            try
            {
                // Rate-limit eager warms in addition to the debounce. A trickle of
                // invalidations spaced wider than the debounce (e.g. per-game Playnite
                // ItemUpdated events while post-refresh tag sync drains) would otherwise
                // trigger a whole-library rebuild per event, and each rebuild holds the
                // cache lock long enough to stall concurrent per-game reads. Invalidate()
                // has already cleared the cache, so on-demand consumers stay fresh; only
                // the precompute is deferred until the interval elapses, and the trailing
                // warm still runs after the last invalidation.
                var delay = TimeSpan.FromMilliseconds(WarmDebounceMs);
                lock (_sync)
                {
                    var untilNextWarm = MinEagerWarmInterval - (DateTime.UtcNow - _lastWarmStartedUtc);
                    if (untilNextWarm > delay)
                    {
                        delay = untilNextWarm;
                    }
                }

                await Task.Delay(delay).ConfigureAwait(false);
                if (_disposed || generation != Volatile.Read(ref _warmGeneration))
                {
                    return;
                }

                lock (_sync)
                {
                    _lastWarmStartedUtc = DateTime.UtcNow;
                }

                using (PerfScope.StartStartup(_logger, "Warm.OverviewProjection", thresholdMs: 250))
                {
                    GetOverviewSnapshot(_settings, CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Failed to warm library projection cache.");
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(LibraryProjectionService));
            }
        }
    }
}
