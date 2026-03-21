using PlayniteAchievements.Models;
using Playnite.SDK;
using System;
using System.Diagnostics;
using System.Threading;

namespace PlayniteAchievements.Services.ProgressReporting
{
    internal sealed class ProgressReportingService : IDisposable
    {
        private readonly ILogger _logger;
        private readonly Action<Action> _postToUi;

        private readonly object _reportLock = new object();
        private ProgressReport _pendingReport;
        private bool _pendingReportIsPriority;
        private long _lastReportTimestamp = -1;
        private System.Timers.Timer _reportThrottleTimer;
        private const int ReportThrottleIntervalMs = 1000;

        private ProgressReport _lastProgress;
        private string _lastStatus;

        public ProgressReportingService(ILogger logger, Action<Action> postToUi)
        {
            _logger = logger;
            _postToUi = postToUi ?? throw new ArgumentNullException(nameof(postToUi));
        }

        public ProgressReport GetLastProgress()
        {
            lock (_reportLock)
            {
                return _lastProgress;
            }
        }

        public string GetLastStatus()
        {
            lock (_reportLock)
            {
                return _lastStatus;
            }
        }

        public void Report(
            object sender,
            EventHandler<ProgressReport> handler,
            ProgressReport report,
            bool prioritizePending)
        {
            if (report == null)
            {
                return;
            }

            lock (_reportLock)
            {
                _lastProgress = report;
                if (!string.IsNullOrWhiteSpace(report.Message))
                {
                    _lastStatus = report.Message;
                }
            }

            if (handler == null)
            {
                return;
            }

            var isFinal = report.IsCanceled || (report.TotalSteps > 0 && report.CurrentStep >= report.TotalSteps);
            var nowTimestamp = Stopwatch.GetTimestamp();
            if (!isFinal)
            {
                var lastTimestamp = Interlocked.Read(ref _lastReportTimestamp);
                if (lastTimestamp >= 0)
                {
                    var elapsedMs = (nowTimestamp - lastTimestamp) * 1000L / Stopwatch.Frequency;
                    if (elapsedMs < ReportThrottleIntervalMs)
                    {
                        lock (_reportLock)
                        {
                            if (_pendingReport == null || prioritizePending || !_pendingReportIsPriority)
                            {
                                _pendingReport = report;
                                _pendingReportIsPriority = prioritizePending;
                            }

                            if (_reportThrottleTimer == null)
                            {
                                _reportThrottleTimer = new System.Timers.Timer(ReportThrottleIntervalMs);
                                _reportThrottleTimer.AutoReset = false;
                                _reportThrottleTimer.Elapsed += (s, e) => OnThrottleTimerElapsed(sender, handler);
                            }

                            if (!_reportThrottleTimer.Enabled)
                            {
                                _reportThrottleTimer.Start();
                            }
                        }

                        return;
                    }
                }
            }

            lock (_reportLock)
            {
                Interlocked.Exchange(ref _lastReportTimestamp, nowTimestamp);
                _pendingReport = null;
                _pendingReportIsPriority = false;
                StopThrottleTimer();
            }

            SendReportToUi(sender, report, handler);
        }

        public double CalculateProgressPercent(ProgressReport report)
        {
            if (report == null)
            {
                return 0;
            }

            var pct = report.PercentComplete;
            if ((pct <= 0 || double.IsNaN(pct)) && report.TotalSteps > 0)
            {
                pct = Math.Max(0, Math.Min(100, (report.CurrentStep * 100.0) / report.TotalSteps));
            }

            if (double.IsNaN(pct))
            {
                return 0;
            }

            return Math.Max(0, Math.Min(100, pct));
        }

        public bool IsFinalProgressReport(ProgressReport report)
        {
            if (report == null)
            {
                return false;
            }

            var progressPercent = CalculateProgressPercent(report);
            return report.IsCanceled ||
                   (report.TotalSteps > 0 && report.CurrentStep >= report.TotalSteps) ||
                   progressPercent >= 100;
        }

        public string ResolveProgressMessage(ProgressReport report = null)
        {
            var effectiveReport = report ?? GetLastProgress();
            var progressPercent = CalculateProgressPercent(effectiveReport);
            var isFinal = effectiveReport != null &&
                          (effectiveReport.IsCanceled ||
                           (effectiveReport.TotalSteps > 0 && effectiveReport.CurrentStep >= effectiveReport.TotalSteps) ||
                           progressPercent >= 100);
            var lastStatus = GetLastStatus();

            if (!string.IsNullOrWhiteSpace(effectiveReport?.Message))
            {
                return effectiveReport.Message;
            }

            if (effectiveReport?.IsCanceled == true)
            {
                return ResourceProvider.GetString("LOCPlayAch_Status_Canceled");
            }

            if (isFinal)
            {
                return ResourceProvider.GetString("LOCPlayAch_Status_RefreshComplete");
            }

            if (!string.IsNullOrWhiteSpace(lastStatus))
            {
                return lastStatus;
            }

            return ResourceProvider.GetString("LOCPlayAch_Status_Starting");
        }

        public RefreshStatusSnapshot GetRefreshStatusSnapshot(bool isRefreshing, ProgressReport report = null)
        {
            var effectiveReport = report ?? GetLastProgress();
            var progressPercent = CalculateProgressPercent(effectiveReport);
            var isFinal = effectiveReport != null &&
                          (effectiveReport.IsCanceled ||
                           (effectiveReport.TotalSteps > 0 && effectiveReport.CurrentStep >= effectiveReport.TotalSteps) ||
                           progressPercent >= 100);

            return new RefreshStatusSnapshot
            {
                IsRefreshing = isRefreshing,
                IsFinal = isFinal,
                IsCanceled = effectiveReport?.IsCanceled == true,
                ProgressPercent = progressPercent,
                Message = ResolveProgressMessage(effectiveReport)
            };
        }

        public RefreshStatusSnapshot GetStartingRefreshStatusSnapshot(bool isRefreshing)
        {
            return new RefreshStatusSnapshot
            {
                IsRefreshing = isRefreshing,
                IsFinal = false,
                IsCanceled = false,
                ProgressPercent = 0,
                Message = ResourceProvider.GetString("LOCPlayAch_Status_Starting")
            };
        }

        public void Dispose()
        {
            lock (_reportLock)
            {
                if (_reportThrottleTimer != null)
                {
                    try
                    {
                        _reportThrottleTimer.Stop();
                        _reportThrottleTimer.Dispose();
                        _reportThrottleTimer = null;
                    }
                    catch
                    {
                    }
                }
            }
        }

        private void OnThrottleTimerElapsed(object sender, EventHandler<ProgressReport> handler)
        {
            ProgressReport reportToSend;
            lock (_reportLock)
            {
                reportToSend = _pendingReport;
                _pendingReport = null;
                _pendingReportIsPriority = false;
                Interlocked.Exchange(ref _lastReportTimestamp, Stopwatch.GetTimestamp());
            }

            if (reportToSend != null && handler != null)
            {
                SendReportToUi(sender, reportToSend, handler);
            }
        }

        private void StopThrottleTimer()
        {
            if (_reportThrottleTimer == null)
            {
                return;
            }

            try
            {
                _reportThrottleTimer.Stop();
            }
            catch
            {
            }
        }

        private void SendReportToUi(object sender, ProgressReport report, EventHandler<ProgressReport> handler)
        {
            _postToUi(() =>
            {
                foreach (EventHandler<ProgressReport> subscriber in handler.GetInvocationList())
                {
                    try
                    {
                        subscriber(sender, report);
                    }
                    catch (Exception ex)
                    {
                        _logger?.Error(ex, ResourceProvider.GetString("LOCPlayAch_Error_NotifySubscribers"));
                    }
                }
            });
        }
    }
}