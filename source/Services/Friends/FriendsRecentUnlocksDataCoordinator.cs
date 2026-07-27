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
    internal sealed class FriendsRecentUnlocksDataCoordinator : IDisposable
    {
        private readonly object _syncRoot = new object();
        private readonly IFriendCacheManager _friendCache;
        private readonly FriendsOverviewDataCoordinator _overviewCoordinator;
        private readonly Func<PersistedSettings> _persistedSettingsFactory;
        private readonly ILogger _logger;
        private readonly Dictionary<int, CacheEntry> _snapshots = new Dictionary<int, CacheEntry>();
        private readonly Dictionary<int, BuildEntry> _buildTasks = new Dictionary<int, BuildEntry>();
        private int _invalidationVersion = 1;
        private bool _disposed;

        public FriendsRecentUnlocksDataCoordinator(
            IFriendCacheManager friendCache,
            FriendsOverviewDataCoordinator overviewCoordinator,
            Func<PersistedSettings> persistedSettingsFactory,
            ILogger logger = null)
        {
            _friendCache = friendCache;
            _overviewCoordinator = overviewCoordinator;
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

        public async Task<FriendsOverviewSnapshot> GetSnapshotAsync(int recentLimit, CancellationToken cancel)
        {
            var key = Math.Max(0, recentLimit);
            cancel.ThrowIfCancellationRequested();

            Task<FriendsOverviewSnapshot> task;
            lock (_syncRoot)
            {
                ThrowIfDisposed();
                if (_snapshots.TryGetValue(key, out var cached) &&
                    cached.Version == _invalidationVersion)
                {
                    return cached.Snapshot ?? new FriendsOverviewSnapshot();
                }

                if (_buildTasks.TryGetValue(key, out var running))
                {
                    task = running.Task;
                }
                else
                {
                    var version = _invalidationVersion;
                    task = Task.Run(() => BuildSnapshot(key));
                    _buildTasks[key] = new BuildEntry
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
                if (_buildTasks.TryGetValue(key, out var running) &&
                    ReferenceEquals(running.Task, task))
                {
                    if (running.Version == _invalidationVersion)
                    {
                        _snapshots[key] = new CacheEntry
                        {
                            Version = running.Version,
                            Snapshot = snapshot ?? new FriendsOverviewSnapshot()
                        };
                    }

                    _buildTasks.Remove(key);
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

        private FriendsOverviewSnapshot BuildSnapshot(int recentLimit)
        {
            try
            {
                if (_overviewCoordinator?.TryGetCurrentSnapshot(out var overviewSnapshot) == true)
                {
                    using (PerfScope.Start(_logger, "FriendsRecentUnlocks.DeriveWarmSnapshot", thresholdMs: 10))
                    {
                        return SliceWarmSnapshot(overviewSnapshot, recentLimit);
                    }
                }

                var persisted = _persistedSettingsFactory();
                FriendsOverviewData data;
                using (PerfScope.Start(_logger, "FriendsRecentUnlocks.LoadCache", thresholdMs: 25))
                {
                    data = _friendCache?.LoadFriendRecentUnlocksData(recentLimit) ??
                           new FriendsOverviewData();
                }

                using (PerfScope.Start(_logger, "FriendsRecentUnlocks.Project", thresholdMs: 10))
                {
                    return FriendsOverviewDataCoordinator.CreateSnapshot(data, persisted);
                }
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Failed to build friends recent unlocks snapshot.");
                return new FriendsOverviewSnapshot();
            }
        }

        private static FriendsOverviewSnapshot SliceWarmSnapshot(
            FriendsOverviewSnapshot source,
            int recentLimit)
        {
            var recent = (source?.RecentUnlocks ?? new List<FriendAchievementDisplayItem>())
                .Where(achievement => achievement != null)
                .OrderByDescending(achievement => achievement.UnlockTimeUtc ?? DateTime.MinValue)
                .ToList();
            if (recentLimit > 0)
            {
                recent = recent.Take(recentLimit).ToList();
            }

            var friendKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var achievement in recent)
            {
                var key = FriendOverviewProjection.GetFriendScopeKey(achievement);
                if (!FriendOverviewProjection.IsAllScope(key))
                {
                    friendKeys.Add(key);
                }
            }

            var friends = (source?.Friends ?? new List<FriendSummaryItem>())
                .Where(friend => friendKeys.Contains(FriendOverviewProjection.GetFriendScopeKey(friend)))
                .ToList();
            var data = new FriendsOverviewData
            {
                Friends = friends,
                RecentUnlocks = recent,
                AllAchievements = recent,
                AllUnlockedAchievements = recent
            };

            return new FriendsOverviewSnapshot
            {
                Data = data,
                Projection = source?.Projection ?? new FriendOverviewProjection(null),
                Friends = friends,
                RecentUnlocks = recent,
                AllAchievements = recent,
                AllUnlockedAchievements = recent
            };
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(FriendsRecentUnlocksDataCoordinator));
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
