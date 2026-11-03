using System;
using System.Threading;

namespace PlayniteAchievements.Services.InGameMonitoring
{
    // Marks only custom refresh work, never the upstream polling or manual-refresh paths.
    internal static class SantodanPollingScope
    {
        private static readonly AsyncLocal<bool> Active = new AsyncLocal<bool>();
        public static bool IsActive => Active.Value;

        public static IDisposable Enter(bool enabled)
        {
            var previous = Active.Value;
            Active.Value = enabled;
            return new Scope(previous);
        }

        private sealed class Scope : IDisposable
        {
            private readonly bool _previous;
            private bool _disposed;
            public Scope(bool previous) => _previous = previous;
            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                Active.Value = _previous;
            }
        }
    }
}
