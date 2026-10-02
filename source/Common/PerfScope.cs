using Playnite.SDK;
using System;
using System.Diagnostics;
using System.Threading;
using System.Windows;

namespace PlayniteAchievements.Common
{
    internal sealed class PerfScope : IDisposable
    {
        private const int SevereThresholdMs = 250;

        /// <summary>
        /// Name of the opt-in marker file. Create an empty file with this name in the plugin's
        /// extension data folder (the one holding playniteachievements.log) and tracing arms on
        /// the next Playnite start. That is what lets a user who reports a stall capture a log
        /// without being handed a custom build.
        /// </summary>
        internal const string TracingOptInFileName = "perftrace.enabled";

        // Diagnostic toggle for perf tracing. A Debug build traces unconditionally; a Release
        // build traces only when the opt-in file above is present.
        //
        // This used to be a hand-flipped constant with a "set this back to false before packing a
        // release" note, and it shipped on. That is expensive, not merely chatty:
        //   - LeakWatch.Track runs per editor row behind a global lock, and LeakWatch.TrackAll
        //     runs over whole library row sets inside the overview's per-edit delta, so an edit
        //     on a large library pays a locked scan it would not pay in a shipped build.
        //   - It turns on the cheap half of MemoryDiagnostics: the [MemPerf] counter lines, the
        //     sampler, and RetentionProbes.
        //   - It gates far more than the scopes: toast capture probes, toast placement
        //     diagnostics, the ray animation driver, the compact list controls, and a
        //     developer-only main-menu item that would otherwise be hidden from users.
        //
        // Runtime-evaluated (never a const) so branches are not constant-folded away.
        internal static bool PerfTracingEnabled { get; private set; } =
#if DEBUG
            true;
#else
            false;
#endif

        /// <summary>
        /// Arms tracing for a Release build when the opt-in marker file is present in the
        /// plugin's user data folder. Call once at startup, before the first scope. A Debug
        /// build is already on and is left alone; a probe failure leaves tracing off.
        /// </summary>
        public static void ConfigureTracing(string pluginUserDataPath)
        {
            if (PerfTracingEnabled || string.IsNullOrWhiteSpace(pluginUserDataPath))
            {
                return;
            }

            try
            {
                PerfTracingEnabled = System.IO.File.Exists(
                    System.IO.Path.Combine(pluginUserDataPath, TracingOptInFileName));
            }
            catch
            {
                // A probe that cannot run leaves tracing off, which is the shipping default.
            }
        }

        private readonly ILogger _logger;
        private readonly string _tag;
        private readonly int _thresholdMs;
        private string _context;
        private readonly bool _startupVariant;
        private readonly Stopwatch _stopwatch;
        private bool _disposed;

        private PerfScope(ILogger logger, string tag, int thresholdMs, string context, bool startupVariant)
        {
            _logger = logger;
            _tag = string.IsNullOrWhiteSpace(tag) ? "unknown" : tag.Trim();
            _thresholdMs = Math.Max(0, thresholdMs);
            _context = context ?? string.Empty;
            _startupVariant = startupVariant;
            _stopwatch = Stopwatch.StartNew();
        }

        /// <summary>
        /// Replaces the context detail emitted with this scope's line. A method rather than a
        /// property because Start returns null when tracing is off, and C# cannot assign through
        /// a null-conditional -- so callers write scope?.SetContext(...) and pay nothing when
        /// disabled. Use it for something known only once the work finishes, typically a result
        /// count: the duration alone cannot say whether a query is slow because of its volume or
        /// because of a sort.
        /// </summary>
        public void SetContext(string context)
        {
            _context = context ?? string.Empty;
        }

        public static PerfScope Start(ILogger logger, string tag, int thresholdMs = 50, string context = null)
        {
            if (!PerfTracingEnabled)
            {
                return null;
            }

            return new PerfScope(logger, tag, thresholdMs, context, startupVariant: false);
        }

        public static PerfScope StartStartup(ILogger logger, string tag, int thresholdMs = 50, string context = null)
        {
            if (!PerfTracingEnabled)
            {
                return null;
            }

            return new PerfScope(logger, tag, thresholdMs, context, startupVariant: true);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _stopwatch.Stop();

            var elapsedMs = (long)_stopwatch.Elapsed.TotalMilliseconds;
            if (elapsedMs < _thresholdMs)
            {
                return;
            }

            var onUiThread = false;
            try
            {
                onUiThread = Application.Current?.Dispatcher?.CheckAccess() ?? false;
            }
            catch
            {
                onUiThread = false;
            }

            var uiFlag = onUiThread ? "true" : "false";
            var threadId = Thread.CurrentThread.ManagedThreadId;
            var safeContext = string.IsNullOrWhiteSpace(_context) ? string.Empty : _context.Trim();

            var message = _startupVariant
                ? $"[StartupPerf] tag={_tag} ms={elapsedMs} ui={uiFlag}"
                : $"[UiBlockRisk] tag={_tag} ms={elapsedMs} ui={uiFlag} thread={threadId} context={safeContext}";

            if (elapsedMs >= SevereThresholdMs)
            {
                _logger?.Warn(message);
            }
            else
            {
                _logger?.Debug(message);
            }
        }
    }
}
