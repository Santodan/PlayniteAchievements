using Playnite.SDK;
using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;

namespace PlayniteAchievements.Common
{
    /// <summary>
    /// Point-in-time process memory counters. <see cref="IsValid"/> is false when capture failed
    /// (or when a caller passes an empty baseline), so consumers can skip delta reporting.
    /// </summary>
    internal struct MemorySnapshot
    {
        public bool IsValid;
        public long WorkingSetBytes;
        public long PrivateBytes;
        public long ManagedBytes;
        public int Gen0;
        public int Gen1;
        public int Gen2;
    }

    /// <summary>
    /// Process memory logging shared by refresh diagnostics. Emits [MemPerf] lines pairing
    /// working set / private bytes (managed + native) with the managed heap size, so residual
    /// memory can be attributed to managed retention vs native (e.g. CEF) working set.
    /// Gated by the same <see cref="PerfScope.PerfTracingEnabled"/> toggle as timing logs.
    /// </summary>
    internal static class MemoryDiagnostics
    {
        private const double BytesPerMb = 1024d * 1024d;

        public static bool Enabled => PerfScope.PerfTracingEnabled;

        /// <summary>
        /// Captures current process memory counters. Never throws; returns an invalid snapshot
        /// on failure so callers in refresh paths stay safe.
        /// </summary>
        public static MemorySnapshot Capture()
        {
            try
            {
                using (var process = Process.GetCurrentProcess())
                {
                    return new MemorySnapshot
                    {
                        IsValid = true,
                        WorkingSetBytes = process.WorkingSet64,
                        PrivateBytes = process.PrivateMemorySize64,
                        ManagedBytes = GC.GetTotalMemory(false),
                        Gen0 = GC.CollectionCount(0),
                        Gen1 = GC.CollectionCount(1),
                        Gen2 = GC.CollectionCount(2)
                    };
                }
            }
            catch
            {
                return default(MemorySnapshot);
            }
        }

        public static MemorySnapshot Log(ILogger logger, string point, string detail = null)
        {
            return Log(logger, point, default(MemorySnapshot), detail);
        }

        /// <summary>
        /// Captures and logs a [MemPerf] line, with deltas against <paramref name="baseline"/>
        /// when the baseline is valid. Returns the captured snapshot for use as a later baseline.
        /// </summary>
        public static MemorySnapshot Log(ILogger logger, string point, MemorySnapshot baseline, string detail = null)
        {
            if (!Enabled)
            {
                return default(MemorySnapshot);
            }

            var snapshot = Capture();
            if (!snapshot.IsValid)
            {
                return snapshot;
            }

            logger?.Debug(Format(point, snapshot, baseline, detail));
            return snapshot;
        }

        internal static string Format(string point, MemorySnapshot snapshot, MemorySnapshot baseline, string detail)
        {
            var safePoint = string.IsNullOrWhiteSpace(point) ? "unknown" : point.Trim();
            var message = string.Format(
                CultureInfo.InvariantCulture,
                "[MemPerf] point={0} workingSetMb={1:F1} privateMb={2:F1} managedMb={3:F1} gen0={4} gen1={5} gen2={6}",
                safePoint,
                snapshot.WorkingSetBytes / BytesPerMb,
                snapshot.PrivateBytes / BytesPerMb,
                snapshot.ManagedBytes / BytesPerMb,
                snapshot.Gen0,
                snapshot.Gen1,
                snapshot.Gen2);

            if (baseline.IsValid)
            {
                message += string.Format(
                    CultureInfo.InvariantCulture,
                    " deltaWorkingSetMb={0:+0.0;-0.0;+0.0} deltaManagedMb={1:+0.0;-0.0;+0.0} deltaGen2={2:+0;-0;+0}",
                    (snapshot.WorkingSetBytes - baseline.WorkingSetBytes) / BytesPerMb,
                    (snapshot.ManagedBytes - baseline.ManagedBytes) / BytesPerMb,
                    snapshot.Gen2 - baseline.Gen2);
            }

            if (!string.IsNullOrWhiteSpace(detail))
            {
                message += " " + detail.Trim();
            }

            return message;
        }

        /// <summary>
        /// Captures current counters and formats a compact key=value suffix (with a leading
        /// space) for appending to an existing perf log line. Empty when disabled or capture
        /// fails, so callers can append unconditionally.
        /// </summary>
        public static string FormatInlineSuffix(MemorySnapshot baseline)
        {
            if (!Enabled)
            {
                return string.Empty;
            }

            var snapshot = Capture();
            if (!snapshot.IsValid)
            {
                return string.Empty;
            }

            var suffix = string.Format(
                CultureInfo.InvariantCulture,
                " workingSetMb={0:F1} managedMb={1:F1}",
                snapshot.WorkingSetBytes / BytesPerMb,
                snapshot.ManagedBytes / BytesPerMb);

            if (baseline.IsValid)
            {
                suffix += string.Format(
                    CultureInfo.InvariantCulture,
                    " deltaManagedMb={0:+0.0;-0.0;+0.0}",
                    (snapshot.ManagedBytes - baseline.ManagedBytes) / BytesPerMb);
            }

            return suffix;
        }

        /// <summary>
        /// Starts a periodic [MemPerf] point=sample logger for long-running work (e.g. multi-minute
        /// refresh phases). Runs on the thread pool; dispose to stop. Returns null when disabled.
        /// </summary>
        public static IDisposable StartSampler(ILogger logger, string detail, TimeSpan interval)
        {
            if (!Enabled)
            {
                return null;
            }

            return new Sampler(logger, detail, interval);
        }

        private sealed class Sampler : IDisposable
        {
            private readonly ILogger _logger;
            private readonly string _detail;
            private Timer _timer;

            public Sampler(ILogger logger, string detail, TimeSpan interval)
            {
                _logger = logger;
                _detail = detail;
                _timer = new Timer(OnTick, null, interval, interval);
            }

            private void OnTick(object state)
            {
                try
                {
                    Log(_logger, "sample", _detail);
                }
                catch
                {
                }
            }

            public void Dispose()
            {
                try
                {
                    Interlocked.Exchange(ref _timer, null)?.Dispose();
                }
                catch
                {
                }
            }
        }
    }
}
