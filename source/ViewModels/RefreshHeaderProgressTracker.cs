using System;
using System.Windows.Threading;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Services.Refresh;
using Playnite.SDK;

namespace PlayniteAchievements.ViewModels
{
    /// <summary>
    /// Shared refresh-header progress state for the Overview and Friends Overview view models.
    /// Subscribes to <see cref="RefreshRuntime.RebuildProgress"/> and exposes the display
    /// properties both headers bind to, so the shared header reflects the live progress of
    /// whatever refresh is running regardless of which sub-view is active.
    /// </summary>
    internal sealed class RefreshHeaderProgressTracker : ObservableObject, IDisposable
    {
        private static readonly TimeSpan ProgressMinInterval = TimeSpan.FromMilliseconds(50);
        private static readonly TimeSpan ProgressHideDelay = TimeSpan.FromSeconds(3);

        private readonly RefreshRuntime _refreshService;
        private readonly ILogger _logger;
        private readonly DispatcherTimer _progressHideTimer;
        private readonly object _progressLock = new object();

        private DateTime _lastProgressUpdate = DateTime.MinValue;
        private double _progressPercent;
        private string _progressMessage;
        private bool _showCompletedProgress;
        private bool _refreshInitiated;
        private bool _disposed;

        public RefreshHeaderProgressTracker(RefreshRuntime refreshService, ILogger logger = null)
        {
            _refreshService = refreshService ?? throw new ArgumentNullException(nameof(refreshService));
            _logger = logger;

            _progressHideTimer = new DispatcherTimer { Interval = ProgressHideDelay };
            _progressHideTimer.Tick += OnProgressHideTimerTick;

            _refreshService.RebuildProgress += OnRebuildProgress;
        }

        public bool IsRefreshing => _refreshService.IsRebuilding;

        public double ProgressPercent
        {
            get => _progressPercent;
            private set => SetValue(ref _progressPercent, value);
        }

        public string ProgressMessage
        {
            get => _progressMessage;
            private set => SetValue(ref _progressMessage, value);
        }

        public bool ShowProgress => _refreshInitiated || IsRefreshing || _showCompletedProgress;

        /// <summary>Prime the header for a refresh this view initiated.</summary>
        public void NotifyRefreshStarting()
        {
            CancelProgressHideTimer(clearCompletedProgress: false);
            _refreshInitiated = true;
            ApplyRefreshStatus(_refreshService.GetStartingRefreshStatusSnapshot());
        }

        /// <summary>Re-sync the header to the runtime's current state (activation or refresh settle).</summary>
        public void SyncToCurrentState()
        {
            ApplyRefreshStatus(_refreshService.GetRefreshStatusSnapshot());
        }

        /// <summary>Clear any lingering completed-progress state when the header is deactivated.</summary>
        public void NotifyDeactivated()
        {
            CancelProgressHideTimer(clearCompletedProgress: true);
        }

        private void OnRebuildProgress(object sender, ProgressReport report)
        {
            if (report == null)
            {
                return;
            }

            var now = DateTime.UtcNow;

            // Centralized progress/status state from RefreshRuntime.
            var status = _refreshService.GetRefreshStatusSnapshot(report);

            lock (_progressLock)
            {
                if (!status.IsFinal)
                {
                    // Only throttle non-final updates
                    if ((now - _lastProgressUpdate) < ProgressMinInterval)
                    {
                        return;
                    }
                }

                _lastProgressUpdate = now;
            }

            System.Windows.Application.Current?.Dispatcher?.BeginInvoke(new Action(() =>
            {
                try
                {
                    ApplyRefreshStatus(status);
                }
                catch (Exception ex)
                {
                    _logger?.Debug($"Progress UI update error: {ex.Message}");
                }
            }));
        }

        private void ApplyRefreshStatus(RefreshStatusSnapshot status)
        {
            if (status == null)
            {
                return;
            }

            ProgressPercent = status.ProgressPercent;
            ProgressMessage = status.Message ?? string.Empty;

            if (status.IsRefreshing)
            {
                _refreshInitiated = true;
                CancelProgressHideTimer(clearCompletedProgress: false);
                _showCompletedProgress = false;
            }
            else if (_refreshInitiated)
            {
                _showCompletedProgress = true;
                StartProgressHideTimer();
            }
            else
            {
                _showCompletedProgress = false;
            }

            OnPropertyChanged(nameof(IsRefreshing));
            OnPropertyChanged(nameof(ShowProgress));
        }

        private void StartProgressHideTimer()
        {
            _progressHideTimer.Stop();
            _progressHideTimer.Start();
        }

        private void CancelProgressHideTimer(bool clearCompletedProgress)
        {
            _progressHideTimer.Stop();

            if (clearCompletedProgress)
            {
                _refreshInitiated = false;
                if (_showCompletedProgress)
                {
                    _showCompletedProgress = false;
                    OnPropertyChanged(nameof(ShowProgress));
                }
            }
        }

        private void OnProgressHideTimerTick(object sender, EventArgs e)
        {
            _progressHideTimer.Stop();
            _refreshInitiated = false;
            if (_showCompletedProgress)
            {
                _showCompletedProgress = false;
                OnPropertyChanged(nameof(ShowProgress));
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _refreshService.RebuildProgress -= OnRebuildProgress;
            _progressHideTimer.Stop();
            _progressHideTimer.Tick -= OnProgressHideTimerTick;
        }
    }
}
