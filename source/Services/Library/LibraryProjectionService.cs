using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Playnite.SDK;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Models.ThemeIntegration;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.Cache;
using PlayniteAchievements.Services.Database;
using PlayniteAchievements.Services.GameCustomData;
using PlayniteAchievements.Services.ThemeIntegration;

namespace PlayniteAchievements.Services.Library
{
    /// <summary>
    /// The themes' library states, built on demand and kept until the data under them changes.
    /// </summary>
    internal sealed class LibraryProjectionService : IDisposable
    {
        private readonly object _sync = new object();
        private readonly AchievementDataService _achievementDataService;
        private readonly IPlayniteAPI _api;
        private readonly ICacheManager _cacheManager;
        private readonly AchievementDataFeed _feed;
        private readonly GameCustomDataStore _customDataStore;
        private PersistedSettingsSubscription _persistedSubscription;
        private readonly Dictionary<string, LibraryProjectionSnapshot> _cache =
            new Dictionary<string, LibraryProjectionSnapshot>(StringComparer.Ordinal);
        private readonly Dictionary<string, InFlightBuild> _inFlight =
            new Dictionary<string, InFlightBuild>(StringComparer.Ordinal);
        private int _cacheGeneration;
        private bool _disposed;

        public LibraryProjectionService(
            AchievementDataService achievementDataService,
            IPlayniteAPI api,
            PlayniteAchievementsSettings settings,
            ICacheManager cacheManager,
            GameCustomDataStore customDataStore)
        {
            _achievementDataService = achievementDataService ?? throw new ArgumentNullException(nameof(achievementDataService));
            _api = api;
            _cacheManager = cacheManager;
            _customDataStore = customDataStore;

            // The feed covers every write the resolved views show; a refresh's own per-game saves
            // arrive as deltas.
            _feed = (cacheManager as ICacheReadOptimizations)?.Feed;
            if (_feed != null)
            {
                _feed.AchievementDataChanged += OnAchievementDataChanged;
            }

            if (_cacheManager != null)
            {
                _cacheManager.CacheDeltaUpdated += OnCacheDeltaForProjection;
            }

            if (_customDataStore != null)
            {
                _customDataStore.CustomDataChanged += OnCustomDataChangedForProjection;
            }

            if (settings != null)
            {
                // Tracks the current Persisted instance so projections keep invalidating
                // after a settings cancel replaces it; the swap itself invalidates, since
                // it can revert any projection-affecting setting in one step.
                _persistedSubscription = new PersistedSettingsSubscription(
                    settings,
                    OnPersistedSettingsChanged,
                    Invalidate);
            }
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
                () => BuildThemeLight(normalizedLimit, token));

            usedCachedSummary = snapshot?.UsedCachedSummary == true;
            hydratedGameCount = snapshot?.HydratedGameCount;
            return snapshot?.LibraryState ?? new LibraryRuntimeState();
        }

        public LibraryRuntimeState GetThemeFullState(
            CancellationToken token,
            out int? hydratedGameCount)
        {
            var snapshot = GetOrBuild("theme-full", () => BuildThemeFull(token));
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
        }

        /// <summary>Cached projection keys retained right now, for memory diagnostics.</summary>
        public string DescribeCachedProjections()
        {
            lock (_sync)
            {
                return _cache.Count == 0 ? "none" : string.Join("+", _cache.Keys);
            }
        }

        public void Dispose()
        {
            _disposed = true;

            lock (_sync)
            {
                _inFlight.Clear();
            }

            if (_feed != null)
            {
                _feed.AchievementDataChanged -= OnAchievementDataChanged;
            }

            if (_cacheManager != null)
            {
                _cacheManager.CacheDeltaUpdated -= OnCacheDeltaForProjection;
            }

            if (_customDataStore != null)
            {
                _customDataStore.CustomDataChanged -= OnCustomDataChangedForProjection;
            }

            _persistedSubscription?.Dispose();
            _persistedSubscription = null;
        }

        private LibraryProjectionSnapshot GetOrBuild(string key, Func<LibraryProjectionSnapshot> build)
        {
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
                        // before the latest Invalidate(). A superseded build still finishes for
                        // its own callers but fails the ReferenceEquals check below, so it is not
                        // stored as current. The fresh build starts once it has finished: each
                        // build holds the whole library in memory, and two at once is what ran a
                        // 32-bit Playnite out of address space.
                        flight = new InFlightBuild
                        {
                            Generation = generation,
                            Task = existing == null
                                ? Task.Run(build)
                                : existing.Task.ContinueWith(
                                    _ => build(),
                                    CancellationToken.None,
                                    TaskContinuationOptions.None,
                                    TaskScheduler.Default)
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

        // Shares one build per cache key across concurrent callers, and runs a key's builds one
        // after another.
        private sealed class InFlightBuild
        {
            public Task<LibraryProjectionSnapshot> Task;
            public int Generation;
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

        private void OnAchievementDataChanged(object sender, AchievementDataChange e)
        {
            if (e == null || e.Touches(AchievementQuery.CurrentUser))
            {
                Invalidate();
            }
        }

        private void OnCacheDeltaForProjection(object sender, CacheDeltaEventArgs e) => Invalidate();

        // A reorder-only change (goals) cannot move anything the library projection derives.
        private void OnCustomDataChangedForProjection(object sender, GameCustomDataChangedEventArgs e)
        {
            if (e != null && !e.AffectsSummaryData)
            {
                return;
            }

            Invalidate();
        }

        private void OnPersistedSettingsChanged(object sender, PropertyChangedEventArgs e)
        {
            Invalidate();
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
