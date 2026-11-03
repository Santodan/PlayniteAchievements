using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;

namespace PlayniteAchievements.Services.Logging
{
    // Passive diagnostics only. Never changes browser lifetime, navigation or cookies.
    internal static class CefDebugTrace
    {
        private static long _nextId;

        public static T Run<T>(string operation, Func<T> work,
            [CallerMemberName] string caller = "", [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
        {
            using (var trace = Begin(operation, caller, file, line))
            {
                try
                {
                    var result = work();
                    if (PollingDebugLog.IsEnabled && result != null)
                        PollingDebugLog.WriteExternal("cef.result id=" + (trace as Scope)?.Id + " operation=" + operation +
                            " objectId=" + RuntimeHelpers.GetHashCode(result));
                    return result;
                }
                catch (Exception ex)
                {
                    PollingDebugLog.WriteExternal("cef.error id=" + (trace as Scope)?.Id + " operation=" + operation + " type=" + ex.GetType().Name);
                    throw;
                }
            }
        }

        public static IDisposable Begin(string operation,
            [CallerMemberName] string caller = "", [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
        {
            if (!PollingDebugLog.IsEnabled) return null;
            var id = Interlocked.Increment(ref _nextId);
            PollingDebugLog.WriteExternal("cef.begin id=" + id + " operation=" + operation +
                " caller=" + Path.GetFileName(file) + ":" + line + ":" + caller +
                " customPolling=" + InGameMonitoring.SantodanPollingScope.IsActive +
                " stack=" + new StackTrace(1, false).ToString().Replace('\r', ' ').Replace('\n', ' '));
            return new Scope(id, operation);
        }

        public static string Host(string url) =>
            Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : "unknown";

        private sealed class Scope : IDisposable
        {
            private readonly long _id;
            public long Id => _id;
            private readonly string _operation;
            private readonly Stopwatch _timer = Stopwatch.StartNew();
            private bool _disposed;
            public Scope(long id, string operation) { _id = id; _operation = operation; }
            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                PollingDebugLog.WriteExternal("cef.end id=" + _id + " operation=" + _operation +
                    " elapsedMs=" + _timer.Elapsed.TotalMilliseconds.ToString("F2", CultureInfo.InvariantCulture));
            }
        }
    }
}
