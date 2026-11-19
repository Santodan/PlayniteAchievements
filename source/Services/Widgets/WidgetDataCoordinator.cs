using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Playnite.SDK;
#if !TEST
using PlayniteAchievements.Models;
using PlayniteAchievements.Services.Achievements;
#endif
using PlayniteAchievements.Services.Overview;

namespace PlayniteAchievements.Services.Widgets
{
    /// <summary>
    /// Owns the cached aggregate achievement snapshot shared by widget hosts and the Overview.
    /// It is kept for the session, across Overview closes, until a data change marks it stale.
    /// Layout, profile, and pin changes do not invalidate this cache.
    /// </summary>
    public class WidgetDataCoordinator : IDisposable
    {
        private readonly object _syncRoot = new object();
        private readonly Func<OverviewDataSnapshot> _snapshotFactory;
        private readonly ILogger _logger;
        private OverviewDataSnapshot _snapshot;
        private Task<OverviewDataSnapshot> _buildTask;
        private int _generation;
        private int _buildGeneration;
        private int _changes;
        private int _buildChanges;
        private int _publisherCount;
        private bool _invalidated = true;
        private bool _disposed;

#if !TEST
        internal WidgetDataCoordinator(
            AchievementDataService achievementDataService,
            IPlayniteAPI playniteApi,
            ILogger logger,
            PlayniteAchievementsSettings settings,
            Func<List<PlayniteAchievements.Models.Friends.FriendIdentity>> currentUserIdentityLoader = null)
            : this(
                () => new OverviewDataBuilder(achievementDataService, null, playniteApi, logger, currentUserIdentityLoader)
                    .Build(settings, CancellationToken.None),
                logger)
        {
        }
#endif

        public WidgetDataCoordinator(Func<OverviewDataSnapshot> snapshotFactory, ILogger logger = null)
        {
            _snapshotFactory = snapshotFactory ?? throw new ArgumentNullException(nameof(snapshotFactory));
            _logger = logger;
        }

        public event EventHandler SnapshotInvalidated;

        public void AttachPublisher()
        {
            lock (_syncRoot)
            {
                ThrowIfDisposed();
                _publisherCount++;
            }
        }

        public void DetachPublisher()
        {
            lock (_syncRoot)
            {
                if (_publisherCount > 0)
                {
                    _publisherCount--;
                }
            }
        }

        /// <summary>The retained snapshot while no data change has marked it stale; otherwise null.</summary>
        public OverviewDataSnapshot CurrentSnapshot()
        {
            lock (_syncRoot)
            {
                return _disposed || _invalidated ? null : _snapshot;
            }
        }

        /// <summary>
        /// Marks the snapshot stale at once for a change whose <see cref="Invalidate"/> is
        /// coalesced to later, so nothing adopts it in between. Builds already running still land
        /// for their waiting callers, but leave the snapshot stale.
        /// </summary>
        public void MarkStale(string reason = null)
        {
            lock (_syncRoot)
            {
                LogStale(reason);
                _invalidated = true;
                _changes++;
            }
        }

        // Only the change that turns a kept snapshot stale is logged, so a run shows why the next
        // Overview open read the library again; later changes before the rebuild add nothing.
        private void LogStale(string reason)
        {
            if (!_invalidated && _snapshot != null)
            {
                _logger?.Debug("[Snapshot] stale reason=" + (reason ?? "unspecified"));
            }
        }

        /// <summary>
        /// Adopts a snapshot built elsewhere (the overview's full or delta-built snapshot)
        /// as the shared snapshot, superseding any in-flight self-build via the generation
        /// check, and notifies listeners the same way <see cref="Invalidate"/> does.
        /// </summary>
        public void Publish(OverviewDataSnapshot snapshot)
        {
            if (snapshot == null)
            {
                return;
            }

            lock (_syncRoot)
            {
                if (_disposed)
                {
                    return;
                }

                _generation++;
                _snapshot = snapshot;
                _invalidated = false;
            }

            SnapshotInvalidated?.Invoke(this, EventArgs.Empty);
        }

        public void Invalidate(string reason = null)
        {
            lock (_syncRoot)
            {
                LogStale(reason);
                _invalidated = true;
                _changes++;
                _generation++;
            }

            SnapshotInvalidated?.Invoke(this, EventArgs.Empty);
        }

        public Task<OverviewDataSnapshot> GetSnapshotAsync(CancellationToken cancel)
        {
            return GetSnapshotAsync(forceRefresh: false, cancel);
        }

        public async Task<OverviewDataSnapshot> GetSnapshotAsync(bool forceRefresh, CancellationToken cancel)
        {
            while (true)
            {
                cancel.ThrowIfCancellationRequested();

                Task<OverviewDataSnapshot> task;
                int taskGeneration;
                int taskChanges;
                lock (_syncRoot)
                {
                    ThrowIfDisposed();

                    if (!forceRefresh && !_invalidated && _snapshot != null)
                    {
                        return _snapshot;
                    }

                    // Publisher-fed: an active overview pushes snapshots via Publish, so a
                    // self-build here would be a second copy. The stale flag from Invalidate
                    // survives, making the first pull after the publisher detaches rebuild once.
                    // Covers forceRefresh too.
                    if (_publisherCount > 0 && _snapshot != null)
                    {
                        return _snapshot;
                    }

                    if (_buildTask != null)
                    {
                        task = _buildTask;
                        taskGeneration = _buildGeneration;
                        taskChanges = _buildChanges;
                    }
                    else
                    {
                        taskGeneration = _generation;
                        taskChanges = _changes;
                        task = Task.Run(BuildSnapshot);
                        _buildTask = task;
                        _buildGeneration = taskGeneration;
                        _buildChanges = taskChanges;
                    }
                }

                var snapshot = await task.ConfigureAwait(false);
                cancel.ThrowIfCancellationRequested();

                lock (_syncRoot)
                {
                    ThrowIfDisposed();
                    var isCurrentGeneration = taskGeneration == _generation;
                    if (ReferenceEquals(_buildTask, task))
                    {
                        _buildTask = null;
                        if (isCurrentGeneration)
                        {
                            _snapshot = snapshot ?? new OverviewDataSnapshot();
                            _invalidated = taskChanges != _changes;
                        }
                    }

                    if (isCurrentGeneration)
                    {
                        return _snapshot ?? snapshot ?? new OverviewDataSnapshot();
                    }
                }

                // A source invalidated while this build was running. Do not publish the stale
                // result or strand listeners on it; loop once more and join/build the new
                // generation instead.
                forceRefresh = false;
            }
        }

        public virtual void Dispose()
        {
            lock (_syncRoot)
            {
                _disposed = true;
                _snapshot = null;
                _buildTask = null;
            }
        }

        private OverviewDataSnapshot BuildSnapshot()
        {
            try
            {
                return _snapshotFactory() ?? new OverviewDataSnapshot();
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Failed to build shared achievement widget snapshot.");
                return new OverviewDataSnapshot();
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(WidgetDataCoordinator));
            }
        }
    }
}
