using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Windows.Threading;
using Playnite.SDK;

namespace PlayniteAchievements.Common
{
    /// <summary>
    /// Logs every dispatcher operation that runs longer than a threshold, for a bounded window,
    /// with its priority and the method it invoked. Zero-cost when tracing is off.
    /// </summary>
    /// <remarks>
    /// PerfScope only sees code that opens a scope. UI-thread time also goes to operations the
    /// plugin never calls directly: layout and render passes (Render priority), Loaded broadcasts,
    /// binding updates, and image decode completions. This hooks the dispatcher so those show up
    /// with a name, which is how an unexplained gap between two scopes gets attributed.
    /// Arm it around the window of interest; it disarms itself when the window elapses.
    /// </remarks>
    internal static class DispatcherOperationProbe
    {
        private const long ThresholdMs = 15;

        private static ILogger _logger;
        private static Dispatcher _dispatcher;
        private static DispatcherTimer _disarmTimer;
        private static string _label;
        private static FieldInfo _methodField;
        private static bool _methodFieldResolved;

        // Operations nest when one pushes a dispatcher frame (modal dialogs, DoEvents-style
        // waits), so starts are a stack rather than a single timestamp.
        private static readonly Stack<long> Starts = new Stack<long>();

        public static void Arm(ILogger logger, string label, TimeSpan window)
        {
            if (!PerfScope.PerfTracingEnabled)
            {
                return;
            }

            var dispatcher = Dispatcher.CurrentDispatcher;
            _label = label ?? string.Empty;
            if (_dispatcher != null)
            {
                // Already armed on this thread: extend the window instead of double-hooking.
                if (ReferenceEquals(_dispatcher, dispatcher) && _disarmTimer != null)
                {
                    _disarmTimer.Stop();
                    _disarmTimer.Interval = window;
                    _disarmTimer.Start();
                }

                return;
            }

            _logger = logger;
            _dispatcher = dispatcher;
            Starts.Clear();
            dispatcher.Hooks.OperationStarted += OnOperationStarted;
            dispatcher.Hooks.OperationCompleted += OnOperationCompleted;
            _disarmTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
            {
                Interval = window
            };
            _disarmTimer.Tick += OnDisarmTick;
            _disarmTimer.Start();
            _logger?.Debug($"[DispatcherOp] armed label={_label} window={window.TotalMilliseconds:0}ms threshold={ThresholdMs}ms");
        }

        private static void OnDisarmTick(object sender, EventArgs e)
        {
            Disarm();
        }

        private static void Disarm()
        {
            var dispatcher = _dispatcher;
            if (dispatcher == null)
            {
                return;
            }

            _disarmTimer?.Stop();
            if (_disarmTimer != null)
            {
                _disarmTimer.Tick -= OnDisarmTick;
            }

            _disarmTimer = null;
            dispatcher.Hooks.OperationStarted -= OnOperationStarted;
            dispatcher.Hooks.OperationCompleted -= OnOperationCompleted;
            _dispatcher = null;
            Starts.Clear();
            _logger?.Debug($"[DispatcherOp] disarmed label={_label}");
        }

        private static void OnOperationStarted(object sender, DispatcherHookEventArgs e)
        {
            Starts.Push(Stopwatch.GetTimestamp());
        }

        private static void OnOperationCompleted(object sender, DispatcherHookEventArgs e)
        {
            if (Starts.Count == 0)
            {
                return;
            }

            var elapsedMs = (Stopwatch.GetTimestamp() - Starts.Pop()) * 1000L / Stopwatch.Frequency;
            if (elapsedMs < ThresholdMs)
            {
                return;
            }

            _logger?.Debug(
                $"[DispatcherOp] {_label} ms={elapsedMs} priority={e.Operation?.Priority} method={Describe(e.Operation)}");
        }

        // DispatcherOperation exposes no public accessor for its delegate; the private field is
        // read once by reflection and the probe degrades to the priority alone if it is missing.
        private static string Describe(DispatcherOperation operation)
        {
            if (operation == null)
            {
                return "?";
            }

            try
            {
                if (!_methodFieldResolved)
                {
                    _methodFieldResolved = true;
                    _methodField = typeof(DispatcherOperation).GetField(
                        "_method",
                        BindingFlags.Instance | BindingFlags.NonPublic);
                }

                if (_methodField?.GetValue(operation) is Delegate method && method.Method != null)
                {
                    var type = method.Method.DeclaringType;
                    // Lambdas compile into nested closure classes; the outer type is the readable one.
                    while (type != null && type.IsNested && type.Name.StartsWith("<", StringComparison.Ordinal))
                    {
                        type = type.DeclaringType;
                    }

                    return (type?.Name ?? "?") + "." + method.Method.Name;
                }
            }
            catch
            {
                // Diagnostics only.
            }

            return "?";
        }
    }
}
