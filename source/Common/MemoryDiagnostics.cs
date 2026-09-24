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

        /// <summary>
        /// Independent switch for the memory lines, so residual-memory work can be traced
        /// without the per-operation timing noise. Off by default: the retention report forces
        /// a blocking collection after every refresh, which is too expensive to ship enabled.
        /// Flip to true (with a rebuild) to re-arm the [MemPerf] lines, the per-cache occupancy
        /// report, and the LeakWatch live counts.
        /// </summary>
        internal static readonly bool MemoryTracingEnabled = false;

        // Two gates, deliberately different.
        //
        // Enabled covers everything cheap: the [MemPerf] counter lines, the inline suffixes, the
        // sampler, LeakWatch and RetentionProbes. It ORs in PerfScope.PerfTracingEnabled so a
        // tracing build reports memory alongside its timings.
        //
        // RetentionReportEnabled covers only the retention report, which forces two blocking gen2
        // collections per call (see LogRetained). That is far too expensive to ride along with
        // timing tracing: the report is scheduled off every cache invalidation, so a session spent
        // editing custom data -- which invalidates per edit -- turned into a forced full collection
        // every few seconds. One captured session took ~1838 of them on a ~350MB managed heap, and
        // every one suspends every thread including the UI. It answers to MemoryTracingEnabled
        // alone, which is what that flag's "too expensive to ship enabled" note always meant.

#if TEST
        /// <summary>
        /// Test-only seam. The real switches are compile-time constants, so without this the
        /// retention counters cannot be exercised at all. Does not exist outside the test
        /// compilation, so production behaviour is unchanged.
        /// </summary>
        internal static bool? TestEnabledOverride;

        public static bool Enabled =>
            TestEnabledOverride ?? (MemoryTracingEnabled || PerfScope.PerfTracingEnabled);

        public static bool RetentionReportEnabled => TestEnabledOverride ?? MemoryTracingEnabled;
#else
        public static bool Enabled => MemoryTracingEnabled || PerfScope.PerfTracingEnabled;

        public static bool RetentionReportEnabled => MemoryTracingEnabled;
#endif

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
        /// Logs a [MemPerf] line after forcing a full blocking collection, so the managed number
        /// reflects what is actually still rooted rather than uncollected garbage. Only for
        /// once-per-refresh retention reporting - never in a hot path.
        /// <para>
        /// Gated on <see cref="RetentionReportEnabled"/>, not <see cref="Enabled"/>: the two
        /// collections below suspend every thread, so this must not ride along with timing
        /// tracing. Callers that schedule it should check the same gate before building the
        /// detail string, which is itself expensive.
        /// </para>
        /// </summary>
        public static void LogRetained(ILogger logger, string point, string detail = null)
        {
            if (!RetentionReportEnabled)
            {
                return;
            }

            try
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                // Blocking variant: a background gen2 collection may not have finished when
                // the counters are read, which would report garbage as still-rooted memory.
                GC.GetTotalMemory(forceFullCollection: true);
            }
            catch
            {
                return;
            }

            Log(logger, point, detail);
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
                // nativeMb is privateMb minus managedMb: everything the process committed that
                // the GC heap does not account for -- decoded bitmap backing stores, SQLite page
                // caches, WPF's unmanaged side. It is derived rather than measured, but without it
                // this line cannot distinguish a managed leak from a native one, and a reported
                // session grew private bytes by ~300 MB while managedMb stayed flat, which read as
                // "no leak" against every counter here.
                "[MemPerf] point={0} workingSetMb={1:F1} privateMb={2:F1} managedMb={3:F1} nativeMb={4:F1} gen0={5} gen1={6} gen2={7}",
                safePoint,
                snapshot.WorkingSetBytes / BytesPerMb,
                snapshot.PrivateBytes / BytesPerMb,
                snapshot.ManagedBytes / BytesPerMb,
                Math.Max(0, snapshot.PrivateBytes - snapshot.ManagedBytes) / BytesPerMb,
                snapshot.Gen0,
                snapshot.Gen1,
                snapshot.Gen2);

            if (baseline.IsValid)
            {
                message += string.Format(
                    CultureInfo.InvariantCulture,
                    " deltaWorkingSetMb={0:+0.0;-0.0;+0.0} deltaManagedMb={1:+0.0;-0.0;+0.0} deltaNativeMb={2:+0.0;-0.0;+0.0} deltaGen2={3:+0;-0;+0}",
                    (snapshot.WorkingSetBytes - baseline.WorkingSetBytes) / BytesPerMb,
                    (snapshot.ManagedBytes - baseline.ManagedBytes) / BytesPerMb,
                    ((snapshot.PrivateBytes - snapshot.ManagedBytes)
                        - (baseline.PrivateBytes - baseline.ManagedBytes)) / BytesPerMb,
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
