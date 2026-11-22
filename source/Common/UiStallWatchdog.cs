using Playnite.SDK;
using System;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace PlayniteAchievements.Common
{
    /// <summary>
    /// Reports how long the UI thread was unresponsive, without needing to know what blocked it.
    /// </summary>
    /// <remarks>
    /// Every other timing in this plugin is a scope around code someone suspected. That only ever
    /// finds a stall that happens to sit inside a scope, and a reported freeze was repeatedly not
    /// in one: the instrumented work totalled a few hundred milliseconds while the window was
    /// described as hard-locked. This measures the symptom instead of a hypothesis about it.
    ///
    /// A background timer posts an empty callback to the dispatcher and times the round trip. The
    /// dispatcher runs it once the work at or above its priority is done, so the round trip is
    /// how long the thread could neither paint nor take input -- which is what the user
    /// experiences as a freeze. Posted at Loaded priority, the lowest the dispatcher runs while
    /// input is pending: it waits behind layout and rendering but not behind input. A Background
    /// probe waits until no input is pending, so a scrollbar drag or any steady mouse movement
    /// held it back for over a second while frames kept painting.
    /// </remarks>
    internal static class UiStallWatchdog
    {
        /// <summary>How often a probe is sent while the previous one is not outstanding.</summary>
        private static readonly TimeSpan ProbeInterval = TimeSpan.FromMilliseconds(250);

        /// <summary>
        /// Below this a delay is ordinary scheduling, not something a user would notice. Set well
        /// under the threshold of a perceptible pause so a stall is reported in full rather than
        /// only once it has become severe.
        /// </summary>
        private const long ReportThresholdMs = 200;

        private static readonly object Sync = new object();
        private static Timer _timer;
        private static ILogger _logger;

        // Read on the pool thread and written on the UI thread, so both accesses are volatile
        // rather than locked: a probe must never take a lock the stalled thread could want.
        private static volatile bool _probeOutstanding;
        private static long _probeSentTicks;

        public static void Start(ILogger logger)
        {
            if (!PerfScope.PerfTracingEnabled)
            {
                return;
            }

            lock (Sync)
            {
                if (_timer != null)
                {
                    return;
                }

                _logger = logger;
                _timer = new Timer(_ => Probe(), null, ProbeInterval, ProbeInterval);
            }
        }

        public static void Stop()
        {
            lock (Sync)
            {
                _timer?.Dispose();
                _timer = null;
                _probeOutstanding = false;
            }
        }

        private static void Probe()
        {
            // One probe at a time. While the UI thread is stuck the timer keeps firing, and
            // queueing a probe per tick would both distort the measurement and pile up work for
            // the thread to do the moment it recovers.
            if (_probeOutstanding)
            {
                return;
            }

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.HasShutdownStarted)
            {
                return;
            }

            _probeOutstanding = true;
            _probeSentTicks = Stopwatch.GetTimestamp();

            try
            {
                _ = dispatcher.BeginInvoke(new Action(OnProbeRan), DispatcherPriority.Loaded);
            }
            catch
            {
                // A dispatcher shutting down rejects the post; nothing to measure.
                _probeOutstanding = false;
            }
        }

        private static void OnProbeRan()
        {
            var elapsedMs = (Stopwatch.GetTimestamp() - _probeSentTicks) * 1000L / Stopwatch.Frequency;
            _probeOutstanding = false;

            if (elapsedMs < ReportThresholdMs)
            {
                return;
            }

            // Logged from the UI thread, immediately after it frees up, so this line lands in the
            // log next to whatever finished just before it -- which is the thing to suspect.
            _logger?.Warn($"[UiStall] ms={elapsedMs} thread={Thread.CurrentThread.ManagedThreadId}");
        }
    }
}
