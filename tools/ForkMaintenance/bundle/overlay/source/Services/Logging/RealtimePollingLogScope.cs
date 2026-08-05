using System;
using System.Threading;

namespace PlayniteAchievements.Services.Logging
{
    /// <summary>
    /// Marks refresh work performed by the in-game fallback poller so routine refresh diagnostics
    /// can stay quiet while errors and the monitor's throttled heartbeat remain visible.
    /// </summary>
    internal static class RealtimePollingLogScope
    {
        private static readonly AsyncLocal<int> Depth = new AsyncLocal<int>();

        public static bool IsActive => Depth.Value > 0;

        public static IDisposable Enter()
        {
            Depth.Value++;
            return new Scope();
        }

        private sealed class Scope : IDisposable
        {
            private bool _disposed;

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                Depth.Value = Math.Max(0, Depth.Value - 1);
            }
        }
    }
}
