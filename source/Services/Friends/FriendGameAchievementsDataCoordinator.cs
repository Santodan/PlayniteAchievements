using Playnite.SDK;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Cache;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.ViewModels.Items;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Services.Friends
{
    internal sealed class FriendGameAchievementsDataCoordinator : IDisposable
    {
        private readonly object _syncRoot = new object();
        private readonly IFriendCacheManager _friendCache;
        private readonly Func<PersistedSettings> _persistedSettingsFactory;
        private readonly ILogger _logger;
        private readonly Dictionary<Guid, CacheEntry> _snapshots = new Dictionary<Guid, CacheEntry>();
        private readonly Dictionary<Guid, BuildEntry> _buildTasks = new Dictionary<Guid, BuildEntry>();
        private int _invalidationVersion = 1;
        private bool _disposed;

        public FriendGameAchievementsDataCoordinator(
            IFriendCacheManager friendCache,
            Func<PersistedSettings> persistedSettingsFactory,
            ILogger logger = null)
        {
            _friendCache = friendCache;
            _persistedSettingsFactory = persistedSettingsFactory ?? (() => null);
            _logger = logger;
        }

        public event EventHandler SnapshotInvalidated;

        public void Invalidate()
        {
            lock (_syncRoot)
            {
                if (_disposed)
                {
                    return;
                }

                _invalidationVersion++;
                _snapshots.Clear();
            }

            SnapshotInvalidated?.Invoke(this, EventArgs.Empty);
        }

        public async Task<FriendsOverviewSnapshot> GetSnapshotAsync(Guid playniteGameId, CancellationToken cancel)
        {
            if (playniteGameId == Guid.Empty)
            {
                return new FriendsOverviewSnapshot();
            }

            cancel.ThrowIfCancellationRequested();

            Task<FriendsOverviewSnapshot> task;
            lock (_syncRoot)
            {
                ThrowIfDisposed();
                if (_snapshots.TryGetValue(playniteGameId, out var cached) &&
                    cached.Version == _invalidationVersion)
                {
                    return cached.Snapshot ?? new FriendsOverviewSnapshot();
                }

                if (_buildTasks.TryGetValue(playniteGameId, out var running))
                {
                    task = running.Task;
                }
                else
                {
                    var version = _invalidationVersion;
                    task = Task.Run(() => BuildSnapshot(playniteGameId));
                    _buildTasks[playniteGameId] = new BuildEntry
                    {
                        Version = version,
                        Task = task
                    };
                }
            }

            var snapshot = await task.ConfigureAwait(false);
            cancel.ThrowIfCancellationRequested();

            lock (_syncRoot)
            {
                if (_buildTasks.TryGetValue(playniteGameId, out var running) &&
                    ReferenceEquals(running.Task, task))
                {
                    if (running.Version == _invalidationVersion)
                    {
                        _snapshots[playniteGameId] = new CacheEntry
                        {
                            Version = running.Version,
                            Snapshot = snapshot ?? new FriendsOverviewSnapshot()
                        };
                    }

                    _buildTasks.Remove(playniteGameId);
                }
            }

            // Bounded staleness: reflect the cache as of build start rather than rebuilding in a
            // loop when invalidations land mid-build; the next SnapshotInvalidated fire converges.
            return snapshot ?? new FriendsOverviewSnapshot();
        }

        public void Dispose()
        {
            lock (_syncRoot)
            {
                _disposed = true;
                _snapshots.Clear();
                _buildTasks.Clear();
            }
        }

        private FriendsOverviewSnapshot BuildSnapshot(Guid playniteGameId)
        {
            try
            {
                // Always the scoped SQL load: this surface compares LOCKED and unlocked rows,
                // and the warm overview snapshot carries unlocked rows only, so slicing it
                // would silently drop the locked half. The per-game query is cheap and the
                // result is cached per invalidation version.
                var persisted = _persistedSettingsFactory();
                FriendsOverviewData data;
                using (PerfScope.Start(_logger, "FriendsGameAchievements.LoadCache", thresholdMs: 25))
                {
                    data = _friendCache?.LoadFriendGameAchievementData(playniteGameId) ??
                           new FriendsOverviewData();
                }

                using (PerfScope.Start(_logger, "FriendsGameAchievements.Project", thresholdMs: 10))
                {
                    return FriendsOverviewDataCoordinator.CreateSnapshot(data, persisted);
                }
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed to build friend game achievements snapshot for {playniteGameId}.");
                return new FriendsOverviewSnapshot();
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(FriendGameAchievementsDataCoordinator));
            }
        }

        private sealed class CacheEntry
        {
            public int Version { get; set; }
            public FriendsOverviewSnapshot Snapshot { get; set; }
        }

        private sealed class BuildEntry
        {
            public int Version { get; set; }
            public Task<FriendsOverviewSnapshot> Task { get; set; }
        }
    }
}
