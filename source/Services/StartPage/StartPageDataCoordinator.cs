using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Playnite.SDK;
#if !TEST
using PlayniteAchievements.Models;
using PlayniteAchievements.Providers;
#endif
using PlayniteAchievements.Services.Overview;

namespace PlayniteAchievements.Services.StartPage
{
    public sealed class StartPageDataCoordinator : IDisposable
    {
        private readonly object _syncRoot = new object();
        private readonly Func<OverviewDataSnapshot> _snapshotFactory;
        private readonly ILogger _logger;
        private OverviewDataSnapshot _snapshot;
        private Task<OverviewDataSnapshot> _buildTask;
        private bool _invalidated = true;
        private bool _disposed;

#if !TEST
        public StartPageDataCoordinator(
            AchievementDataService achievementDataService,
            IReadOnlyList<IDataProvider> providers,
            IPlayniteAPI playniteApi,
            ILogger logger,
            PlayniteAchievementsSettings settings)
            : this(
                () =>
                {
                    var builder = new OverviewDataBuilder(
                        achievementDataService,
                        providers,
                        playniteApi,
                        logger);
                    return builder.Build(
                        settings,
                        new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                        CancellationToken.None);
                },
                logger)
        {
        }
#endif

        public StartPageDataCoordinator(Func<OverviewDataSnapshot> snapshotFactory, ILogger logger = null)
        {
            _snapshotFactory = snapshotFactory ?? throw new ArgumentNullException(nameof(snapshotFactory));
            _logger = logger;
        }

        public event EventHandler SnapshotInvalidated;

        public void Invalidate()
        {
            lock (_syncRoot)
            {
                _invalidated = true;
            }

            SnapshotInvalidated?.Invoke(this, EventArgs.Empty);
        }

        public Task<OverviewDataSnapshot> GetSnapshotAsync(CancellationToken cancel)
        {
            return GetSnapshotAsync(forceRefresh: false, cancel);
        }

        public async Task<OverviewDataSnapshot> GetSnapshotAsync(bool forceRefresh, CancellationToken cancel)
        {
            cancel.ThrowIfCancellationRequested();

            Task<OverviewDataSnapshot> task;
            lock (_syncRoot)
            {
                ThrowIfDisposed();

                if (!forceRefresh && !_invalidated && _snapshot != null)
                {
                    return _snapshot;
                }

                if (!forceRefresh && _buildTask != null)
                {
                    task = _buildTask;
                }
                else
                {
                    task = Task.Run(BuildSnapshot);
                    _buildTask = task;
                }
            }

            var snapshot = await task.ConfigureAwait(false);
            cancel.ThrowIfCancellationRequested();

            lock (_syncRoot)
            {
                if (ReferenceEquals(_buildTask, task))
                {
                    _snapshot = snapshot ?? new OverviewDataSnapshot();
                    _invalidated = false;
                    _buildTask = null;
                }

                return _snapshot ?? snapshot ?? new OverviewDataSnapshot();
            }
        }

        public void Dispose()
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
                _logger?.Error(ex, "Failed to build StartPage achievement snapshot.");
                return new OverviewDataSnapshot();
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(StartPageDataCoordinator));
            }
        }
    }
}
