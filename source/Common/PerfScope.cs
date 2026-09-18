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
        // Diagnostic toggle for perf tracing. Flip to true and rebuild to emit timing logs; set
        // back to false to disable (Start/StartStartup then return null, so `using` scopes are a
        // no-op with zero overhead). Kept runtime-evaluated to avoid constant-folded unreachable
        // branches. Also gates MemoryDiagnostics [MemPerf] lines.
        internal static readonly bool PerfTracingEnabled = false;

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
