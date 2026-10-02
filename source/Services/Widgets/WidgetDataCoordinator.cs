using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Playnite.SDK;
#if !TEST
using PlayniteAchievements.Models;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.Library;
#endif
using PlayniteAchievements.Services.Overview;

namespace PlayniteAchievements.Services.Widgets
{
    /// <summary>
    /// Owns the cached aggregate achievement snapshot shared by widget hosts.
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
        private int _publisherCount;
        private bool _invalidated = true;
        private bool _disposed;

#if !TEST
        internal WidgetDataCoordinator(
            AchievementDataService achievementDataService,
            LibraryProjectionService libraryProjectionService,
            IReadOnlyList<IDataProvider> providers,
            IPlayniteAPI playniteApi,
            ILogger logger,
            PlayniteAchievementsSettings settings,
            Func<List<PlayniteAchievements.Models.Friends.FriendIdentity>> currentUserIdentityLoader = null)
            : this(
                () =>
                {
                    if (libraryProjectionService != null)
                    {
                        return libraryProjectionService.GetOverviewSnapshot(
                            settings,
                            CancellationToken.None);
                    }

                    var builder = new OverviewDataBuilder(
                        achievementDataService,
                        providers,
                        playniteApi,
                        logger,
                        currentUserIdentityLoader);
                    return builder.Build(settings, CancellationToken.None);
                },
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

        /// <summary>
        /// True while at least one snapshot publisher (an active overview view model) is
        /// attached. While a publisher is attached the coordinator does not self-build;
        /// it serves whatever the publisher last pushed via <see cref="Publish"/>.
        /// </summary>
        public bool HasActivePublisher
        {
            get
            {
                lock (_syncRoot)
                {
                    return _publisherCount > 0;
                }
            }
        }

        /// <summary>Retained snapshot shape, for memory diagnostics.</summary>
        public void GetRetentionStats(out bool hasSnapshot, out int achievementRows, out int gameRows)
        {
            lock (_syncRoot)
            {
                hasSnapshot = _snapshot != null;
                achievementRows = _snapshot?.Achievements?.Count ?? 0;
                gameRows = _snapshot?.GameSummaries?.Count ?? 0;
            }
        }

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

                // The retained snapshot exists to serve widget hosts. With the last publisher
                // gone (the overview closing) and nothing subscribed, holding a full-library
                // snapshot keeps the overview's whole row set alive for no consumer, so the
                // close would not return memory. Drop it; the stale flag makes the next pull
                // rebuild on demand.
                if (_publisherCount == 0 && SnapshotInvalidated == null)
                {
                    _snapshot = null;
                    _invalidated = true;
                }
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

        public void Invalidate()
        {
            lock (_syncRoot)
            {
                _invalidated = true;
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
                lock (_syncRoot)
                {
                    ThrowIfDisposed();

                    if (!forceRefresh && !_invalidated && _snapshot != null)
                    {
                        return _snapshot;
                    }

                    // Publisher-fed: an active overview pushes snapshots via Publish, so a
                    // self-build here would just retain a redundant full-library copy. The
                    // stale flag from Invalidate survives, making the first pull after the
                    // publisher detaches rebuild once. Covers forceRefresh too.
                    if (_publisherCount > 0 && _snapshot != null)
                    {
                        return _snapshot;
                    }

                    if (_buildTask != null)
                    {
                        task = _buildTask;
                        taskGeneration = _buildGeneration;
                    }
                    else
                    {
                        taskGeneration = _generation;
                        task = Task.Run(BuildSnapshot);
                        _buildTask = task;
                        _buildGeneration = taskGeneration;
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
                            _invalidated = false;
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
