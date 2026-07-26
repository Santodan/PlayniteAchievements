using Playnite.SDK;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.ViewModels.Items;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Services.Friends
{
    internal sealed class FriendsOverviewSnapshot
    {
        public FriendsOverviewData Data { get; set; } = new FriendsOverviewData();

        public FriendOverviewProjection Projection { get; set; } = new FriendOverviewProjection(null);

        public List<FriendSummaryItem> Friends { get; set; } = new List<FriendSummaryItem>();

        public List<FriendGameSummaryItem> Games { get; set; } = new List<FriendGameSummaryItem>();

        public List<FriendAchievementDisplayItem> RecentUnlocks { get; set; } = new List<FriendAchievementDisplayItem>();

        // Unlocked rows only: the overview surfaces and projection derivations never read
        // locked rows, and loading the full definition-driven set dominated build cost. The
        // friend+game pair comparison loads its one game's locked rows on demand via
        // IFriendCacheManager.LoadFriendGameAchievementData(FriendCacheChange).
        public List<FriendAchievementDisplayItem> AllAchievements { get; set; } = new List<FriendAchievementDisplayItem>();

        public List<FriendAchievementDisplayItem> AllUnlockedAchievements { get; set; } = new List<FriendAchievementDisplayItem>();
    }

    internal sealed class FriendsOverviewDataCoordinator : IDisposable
    {
        private static readonly TimeSpan DefaultWarmDebounceInterval = TimeSpan.FromMilliseconds(1500);

        // Invalidation bursts (a friend scan flushes cache invalidations every ~2s) previously
        // fanned out one SnapshotInvalidated event each, and every consumer rebuild is a full
        // multi-second cache load materializing the whole friend display graph (~50k+ display
        // items). Coalescing to a leading fire plus one trailing fire per window caps rebuild
        // cadence while keeping isolated invalidations (settings toggles) instant. When every
        // pending change is patchable the rebuild is a cheap incremental patch, so a much
        // shorter window keeps friends surfaces near-live during scans.
        private static readonly TimeSpan DefaultInvalidationEventThrottleInterval = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan DefaultScopedInvalidationEventThrottleInterval = TimeSpan.FromSeconds(3);

        // Above this many distinct pending changes an incremental patch stops paying for itself
        // (the scoped SQL grows and a full reload amortizes better), so the window collapses to
        // a full rebuild. Also bounds the scoped query's parameter count.
        private const int MaxPatchableChanges = 64;

        private readonly object _syncRoot = new object();
        private readonly IFriendCacheManager _friendCache;
        private readonly Func<PersistedSettings> _persistedSettingsFactory;
        private readonly ILogger _logger;
        private readonly TimeSpan _warmDebounceInterval;
        private readonly TimeSpan _invalidationEventThrottleInterval;
        private readonly TimeSpan _scopedInvalidationEventThrottleInterval;
        private readonly Stopwatch _eventClock = Stopwatch.StartNew();
        private readonly FriendCacheInvalidationScopeAccumulator _pendingScope =
            new FriendCacheInvalidationScopeAccumulator(MaxPatchableChanges);
        private FriendsOverviewSnapshot _snapshot;
        // AllAchievements of the last successful build, kept as the splice base for incremental
        // patches. Item instances are shared with the published snapshot, so this pins list
        // overhead only, not a second copy of the display items.
        private List<FriendAchievementDisplayItem> _patchBaseAchievements;
        private Task<SnapshotBuildResult> _buildTask;
        private Func<bool> _externalConsumerProbe;
        private int _viewConsumerCount;
        private int _invalidationVersion = 1;
        private int _snapshotVersion;
        private int _buildTaskVersion;
        private int _warmGeneration;
        private TimeSpan? _lastInvalidationEventAt;
        private bool _trailingInvalidationScheduled;
        private bool _disposed;

        public FriendsOverviewDataCoordinator(
            IFriendCacheManager friendCache,
            Func<PersistedSettings> persistedSettingsFactory,
            ILogger logger = null,
            TimeSpan? warmDebounceInterval = null,
            TimeSpan? invalidationEventThrottleInterval = null,
            TimeSpan? scopedInvalidationEventThrottleInterval = null)
        {
            _friendCache = friendCache;
            _persistedSettingsFactory = persistedSettingsFactory ?? (() => null);
            _logger = logger;
            _warmDebounceInterval = warmDebounceInterval ?? DefaultWarmDebounceInterval;
            _invalidationEventThrottleInterval =
                invalidationEventThrottleInterval ?? DefaultInvalidationEventThrottleInterval;
            _scopedInvalidationEventThrottleInterval =
                scopedInvalidationEventThrottleInterval ?? DefaultScopedInvalidationEventThrottleInterval;
        }

        private sealed class SnapshotBuildResult
        {
            public FriendsOverviewSnapshot Snapshot { get; set; }
            public List<FriendAchievementDisplayItem> BaseAchievements { get; set; }
        }

        public event EventHandler SnapshotInvalidated;

        /// <summary>
        /// Raised after the retained snapshot and patch base have been dropped because the last
        /// view consumer detached and no external consumer holds the data. Consumers caching
        /// slices of the snapshot (start-page coordinators) should invalidate on this event so
        /// their slices stop pinning the released projection.
        /// </summary>
        public event EventHandler SnapshotReleased;

        /// <summary>
        /// Registers a probe consulted when the last view consumer detaches. When it returns
        /// true (e.g. the active theme reads friend bindings), the snapshot is kept alive for
        /// that consumer instead of being released.
        /// </summary>
        public void SetExternalConsumerProbe(Func<bool> probe)
        {
            lock (_syncRoot)
            {
                _externalConsumerProbe = probe;
            }
        }

        /// <summary>
        /// Counts an attached view (friends overview view model). While at least one view is
        /// attached, the snapshot and patch base are retained across invalidations.
        /// </summary>
        public void AddViewConsumer()
        {
            lock (_syncRoot)
            {
                if (_disposed)
                {
                    return;
                }

                _viewConsumerCount++;
            }
        }

        /// <summary>
        /// Detaches a view consumer. When the count reaches zero and no external consumer
        /// (theme) needs the data, drops the snapshot, patch base, and any in-flight build's
        /// publish slot so the full friend row set becomes collectable. The next
        /// <see cref="GetSnapshotAsync(CancellationToken)"/> performs a full rebuild.
        /// </summary>
        public void RemoveViewConsumer()
        {
            Func<bool> probe;
            lock (_syncRoot)
            {
                if (_disposed)
                {
                    return;
                }

                if (_viewConsumerCount <= 0)
                {
                    _logger?.Debug("Friends overview view-consumer count underflow; ignoring.");
                    return;
                }

                _viewConsumerCount--;
                if (_viewConsumerCount > 0)
                {
                    return;
                }

                probe = _externalConsumerProbe;
            }

            if (probe?.Invoke() == true)
            {
                return;
            }

            bool released;
            lock (_syncRoot)
            {
                released = ReleaseSnapshot_Locked();
            }

            if (released)
            {
                MemoryDiagnostics.Log(_logger, "friendsOverview.release");
                SnapshotReleased?.Invoke(this, EventArgs.Empty);
            }
        }

        private bool ReleaseSnapshot_Locked()
        {
            // Re-checked because the probe runs outside the lock: a view may have re-attached
            // in between, in which case the data stays.
            if (_disposed || _viewConsumerCount > 0)
            {
                return false;
            }

            if (_snapshot == null && _patchBaseAchievements == null && _buildTask == null)
            {
                return false;
            }

            // Bumping the version marks any published snapshot stale for direct readers;
            // nulling _buildTask makes an in-flight build fail the ReferenceEquals publish
            // guard in GetSnapshotAsync, so its completion cannot re-pin the row set (the
            // awaiting caller still receives its result).
            _invalidationVersion++;
            _snapshot = null;
            _patchBaseAchievements = null;
            _buildTask = null;
            return true;
        }

        public bool TryGetCurrentSnapshot(out FriendsOverviewSnapshot snapshot)
        {
            lock (_syncRoot)
            {
                snapshot = !_disposed &&
                           _snapshot != null &&
                           _snapshotVersion == _invalidationVersion
                    ? _snapshot
                    : null;
                return snapshot != null;
            }
        }

        public void Invalidate()
        {
            Invalidate(null);
        }

        public void Invalidate(FriendCacheInvalidatedEventArgs args)
        {
            bool fireNow;
            var trailingDelay = TimeSpan.Zero;
            lock (_syncRoot)
            {
                if (_disposed)
                {
                    return;
                }

                _invalidationVersion++;
                // A version-mismatched snapshot can never be returned to a reader, so drop the
                // reference immediately instead of pinning the full friend row set until the
                // next successful build replaces it. (_patchBaseAchievements stays: it is the
                // splice base that makes the next build cheap.)
                _snapshot = null;

                if (args == null || args.IsFull)
                {
                    _pendingScope.Add(null);
                }
                else
                {
                    foreach (var change in args.Changes)
                    {
                        _pendingScope.Add(change);
                    }
                }

                // A pending window that is fully scoped rebuilds via a cheap patch, so it may
                // fire much more often than one that forces a full reload.
                var window = _pendingScope.PendingIsFull || _patchBaseAchievements == null
                    ? _invalidationEventThrottleInterval
                    : _scopedInvalidationEventThrottleInterval;

                var now = _eventClock.Elapsed;
                if (_lastInvalidationEventAt == null ||
                    now - _lastInvalidationEventAt.Value >= window)
                {
                    _lastInvalidationEventAt = now;
                    fireNow = true;
                }
                else if (!_trailingInvalidationScheduled)
                {
                    _trailingInvalidationScheduled = true;
                    trailingDelay = _lastInvalidationEventAt.Value + window - now;
                    fireNow = false;
                }
                else
                {
                    // A trailing fire is already scheduled; this invalidation folds into it. The
                    // version bump above still marks the snapshot stale for direct requests.
                    return;
                }
            }

            if (fireNow)
            {
                SnapshotInvalidated?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                _ = FireTrailingInvalidationAsync(trailingDelay);
            }
        }

        private async Task FireTrailingInvalidationAsync(TimeSpan delay)
        {
            try
            {
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay).ConfigureAwait(false);
                }

                lock (_syncRoot)
                {
                    if (_disposed)
                    {
                        return;
                    }

                    _trailingInvalidationScheduled = false;
                    _lastInvalidationEventAt = _eventClock.Elapsed;
                }

                SnapshotInvalidated?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Trailing snapshot invalidation fire failed.");
            }
        }

        // Debounced eager build. The snapshot is otherwise built on demand by the first
        // consumer (friends view or friend-consuming theme binding); a build may already be in
        // flight, so this coalesces instead of always rebuilding — see WarmAfterDelayAsync for
        // the per-state behavior.
        public void Warm()
        {
            var generation = Interlocked.Increment(ref _warmGeneration);
            _ = WarmAfterDelayAsync(generation);
        }

        public Task<FriendsOverviewSnapshot> GetSnapshotAsync(CancellationToken cancel)
        {
            return GetSnapshotAsync(forceRefresh: false, cancel);
        }

        public async Task<FriendsOverviewSnapshot> GetSnapshotAsync(bool forceRefresh, CancellationToken cancel)
        {
            cancel.ThrowIfCancellationRequested();

            Task<SnapshotBuildResult> task;
            lock (_syncRoot)
            {
                ThrowIfDisposed();

                if (!forceRefresh &&
                    _snapshot != null &&
                    _snapshotVersion == _invalidationVersion)
                {
                    return _snapshot;
                }

                if (!forceRefresh && _buildTask != null)
                {
                    task = _buildTask;
                }
                else
                {
                    _buildTaskVersion = _invalidationVersion;
                    // The pending scope travels with this build. If the build fails or is
                    // superseded, the drained accumulator leaves the next window empty, which
                    // drains to a full invalidation - a safe fallback, never a lost change.
                    var scope = forceRefresh
                        ? DiscardPendingScopeAndForceFull_Locked()
                        : _pendingScope.Drain();
                    var baseAchievements = _patchBaseAchievements;
                    task = Task.Run(() => BuildSnapshot(scope, baseAchievements));
                    _buildTask = task;
                }
            }

            var result = await task.ConfigureAwait(false);
            cancel.ThrowIfCancellationRequested();

            lock (_syncRoot)
            {
                if (ReferenceEquals(_buildTask, task))
                {
                    _snapshot = result?.Snapshot ?? new FriendsOverviewSnapshot();
                    _patchBaseAchievements = result?.BaseAchievements;
                    _snapshotVersion = _buildTaskVersion;
                    _buildTask = null;
                }
            }

            // Bounded staleness: the result reflects the cache as of build start. When an
            // invalidation lands mid-build, consumers converge through the next (throttled)
            // SnapshotInvalidated fire instead of looping here — during scans that loop rebuilt
            // the full friend display graph every few seconds.
            return result?.Snapshot ?? new FriendsOverviewSnapshot();
        }

        private FriendCacheInvalidatedEventArgs DiscardPendingScopeAndForceFull_Locked()
        {
            _pendingScope.Drain();
            return FriendCacheInvalidatedEventArgs.FullInvalidation;
        }

        public void Dispose()
        {
            lock (_syncRoot)
            {
                _disposed = true;
                _snapshot = null;
                _patchBaseAchievements = null;
                _buildTask = null;
            }
        }

        private async Task WarmAfterDelayAsync(int generation)
        {
            try
            {
                if (_warmDebounceInterval > TimeSpan.Zero)
                {
                    await Task.Delay(_warmDebounceInterval).ConfigureAwait(false);
                }

                if (generation != Volatile.Read(ref _warmGeneration) || IsDisposed())
                {
                    return;
                }

                // A snapshot build may already have completed or be in flight (a friends
                // surface or a friend-consuming theme requests one on demand). Rebuilding here
                // would repeat the multi-second friend cache load and hold the cache lock while
                // the main view is coming up, so coalesce instead:
                // - build in flight: leave it untouched. Invalidating now would make the
                //   awaiting consumer rebuild immediately, recreating the duplicate load.
                // - completed build: mark it stale (it may have resolved game presentation
                //   before Playnite's database was populated) and let the next consumer rebuild
                //   on demand.
                // - no snapshot and no build: eager warm, as before, so friend surfaces do not
                //   depend on another UI surface to trigger the first load.
                bool hasCurrentSnapshot;
                bool hasBuildInFlight;
                lock (_syncRoot)
                {
                    hasCurrentSnapshot = _snapshot != null && _snapshotVersion == _invalidationVersion;
                    hasBuildInFlight = _buildTask != null;
                }

                if (hasBuildInFlight)
                {
                    return;
                }

                Invalidate();
                if (hasCurrentSnapshot)
                {
                    return;
                }

                using (PerfScope.StartStartup(_logger, "Warm.FriendsOverviewSnapshot", thresholdMs: 250))
                {
                    await GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Failed to warm friends overview snapshot.");
            }
        }

        private SnapshotBuildResult BuildSnapshot(
            FriendCacheInvalidatedEventArgs scope,
            List<FriendAchievementDisplayItem> baseAchievements)
        {
            try
            {
                var memBaseline = MemoryDiagnostics.Capture();
                var persisted = _persistedSettingsFactory();
                var buildKind = "full";
                FriendsOverviewData data = null;

                if (baseAchievements != null && CanPatch(scope))
                {
                    try
                    {
                        using (PerfScope.Start(_logger, "FriendsOverview.LoadPatch", thresholdMs: 25))
                        {
                            var reloadScopes = SelectAchievementReloadScopes(scope.Changes);
                            var patch = _friendCache?.LoadFriendsOverviewPatchData(reloadScopes);
                            if (patch != null)
                            {
                                data = BuildPatchedData(baseAchievements, patch, scope.Changes);
                                buildKind = $"incremental changes={scope.Changes.Count}";
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        // Any patch failure falls back to the full load below.
                        _logger?.Debug(ex, "Incremental friends overview patch failed; falling back to full build.");
                        data = null;
                    }
                }

                if (data == null)
                {
                    using (PerfScope.Start(_logger, "FriendsOverview.LoadCache", thresholdMs: 25))
                    {
                        data = _friendCache?.LoadFriendsOverviewData(0) ?? new FriendsOverviewData();
                    }
                }

                var snapshot = CreateSnapshot(data, persisted);
                MemoryDiagnostics.Log(
                    _logger,
                    "friendsOverview.build",
                    memBaseline,
                    $"kind={buildKind} friends={snapshot.Friends.Count} games={snapshot.Games.Count} recentUnlocks={snapshot.RecentUnlocks.Count} allAchievements={snapshot.AllAchievements.Count} allUnlocked={snapshot.AllUnlockedAchievements.Count}");
                return new SnapshotBuildResult
                {
                    Snapshot = snapshot,
                    BaseAchievements = data.AllAchievements
                };
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Failed to build friends overview snapshot.");
                // Null base forces the next build to be full.
                return new SnapshotBuildResult { Snapshot = new FriendsOverviewSnapshot() };
            }
        }

        // Roster changes alter which friends exist (the shape of every list), so they take the
        // full path. Everything else is expressible as a splice plus wholesale reloads of the
        // three cheap lists.
        internal static bool CanPatch(FriendCacheInvalidatedEventArgs scope)
        {
            return scope != null &&
                   !scope.IsFull &&
                   scope.Changes.Count > 0 &&
                   scope.Changes.All(change =>
                       change != null && change.Kind != FriendCacheChangeKind.Roster);
        }

        // Only these kinds change achievement rows; ownership and removal changes are covered by
        // the wholesale reloads and the removal splice respectively.
        internal static List<FriendCacheChange> SelectAchievementReloadScopes(
            IReadOnlyCollection<FriendCacheChange> changes)
        {
            return (changes ?? Array.Empty<FriendCacheChange>())
                .Where(change => change != null &&
                                 (change.Kind == FriendCacheChangeKind.FriendGameAchievements ||
                                  change.Kind == FriendCacheChangeKind.GameDefinition))
                .ToList();
        }

        /// <summary>
        /// Splices freshly loaded scoped achievement rows into the retained base list and
        /// re-runs the shared derivations, producing a data set equivalent to a full reload for
        /// the given changes. All lists are new instances (copy-on-write); the untouched
        /// achievement items are reused by reference.
        /// </summary>
        internal static FriendsOverviewData BuildPatchedData(
            List<FriendAchievementDisplayItem> baseAchievements,
            FriendsOverviewData patch,
            IReadOnlyCollection<FriendCacheChange> changes)
        {
            var changeList = (changes ?? Array.Empty<FriendCacheChange>())
                .Where(change => change != null)
                .ToList();

            var merged = (baseAchievements ?? new List<FriendAchievementDisplayItem>())
                .Where(item => item != null && !changeList.Any(change => MatchesRemovalScope(item, change)))
                .Concat(patch.AllAchievements ?? new List<FriendAchievementDisplayItem>())
                // Best-effort restoration of the full load's (friend, game) ordering; OrderBy is
                // stable, so each friend+game pair keeps its canonical definition order.
                .OrderBy(item => item.FriendName ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.GameName ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var data = new FriendsOverviewData
            {
                Friends = patch.Friends ?? new List<FriendSummaryItem>(),
                Games = patch.Games ?? new List<FriendGameSummaryItem>(),
                FriendGameLinks = patch.FriendGameLinks ?? new List<FriendGameLinkItem>(),
                AllAchievements = merged
            };

            FriendsOverviewDerivations.Apply(data, recentLimit: 0);
            return data;
        }

        // Which retained achievement rows a change supersedes: fresh rows replace them (or, for
        // FriendRemoved, nothing does).
        internal static bool MatchesRemovalScope(FriendAchievementDisplayItem item, FriendCacheChange change)
        {
            switch (change.Kind)
            {
                case FriendCacheChangeKind.FriendGameAchievements:
                    return MatchesProvider(item, change) && MatchesFriend(item, change) && MatchesGame(item, change);
                case FriendCacheChangeKind.GameDefinition:
                    return MatchesProvider(item, change) && MatchesGame(item, change);
                case FriendCacheChangeKind.FriendRemoved:
                    return MatchesProvider(item, change) && MatchesFriend(item, change);
                default:
                    // FriendOwnership changes no achievement rows; Roster never reaches the
                    // patch path.
                    return false;
            }
        }

        private static bool MatchesProvider(FriendAchievementDisplayItem item, FriendCacheChange change) =>
            string.Equals(item.ProviderKey, change.ProviderKey, StringComparison.OrdinalIgnoreCase);

        private static bool MatchesFriend(FriendAchievementDisplayItem item, FriendCacheChange change) =>
            string.Equals(item.FriendExternalUserId, change.ExternalUserId, StringComparison.OrdinalIgnoreCase);

        private static bool MatchesGame(FriendAchievementDisplayItem item, FriendCacheChange change)
        {
            if (!string.IsNullOrWhiteSpace(change.ProviderGameKey) &&
                string.Equals(item.ProviderGameKey, change.ProviderGameKey, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return change.AppId > 0 && item.AppId == change.AppId;
        }

        internal static FriendsOverviewSnapshot CreateSnapshot(FriendsOverviewData data, PersistedSettings persisted)
        {
            var projection = new FriendOverviewProjection(data, persisted);
            return new FriendsOverviewSnapshot
            {
                Data = data ?? new FriendsOverviewData(),
                Projection = projection,
                Friends = projection.Friends.ToList(),
                Games = projection.AggregateGames.ToList(),
                RecentUnlocks = projection.RecentUnlocks.ToList(),
                AllAchievements = projection.AllAchievements.ToList(),
                AllUnlockedAchievements = projection.AllUnlockedAchievements.ToList()
            };
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(FriendsOverviewDataCoordinator));
            }
        }

        private bool IsDisposed()
        {
            lock (_syncRoot)
            {
                return _disposed;
            }
        }
    }
}
