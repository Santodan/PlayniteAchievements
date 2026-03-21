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
        // Local diagnostic toggle: set true when you want perf tracing.
        // Keep this runtime-evaluated to avoid constant-folded unreachable branches.
        private static readonly bool PerfTracingEnabled = false;

        private readonly ILogger _logger;
        private readonly string _tag;
        private readonly int _thresholdMs;
        private readonly string _context;
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
