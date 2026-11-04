using System;
using System.Collections.Generic;
using System.Threading;

namespace PlayniteAchievements.Services.InGameMonitoring
{
    // Marks only custom refresh work, never the upstream polling or manual-refresh paths.
    internal static class SantodanPollingScope
    {
        private static readonly AsyncLocal<bool> Active = new AsyncLocal<bool>();
        private static readonly AsyncLocal<Dictionary<object, Dictionary<string, object>>> Discovery =
            new AsyncLocal<Dictionary<object, Dictionary<string, object>>>();
        public static bool IsActive => Active.Value;

        public static IDisposable Enter(bool enabled)
        {
            var previous = Active.Value;
            var previousDiscovery = Discovery.Value;
            Active.Value = enabled;
            Discovery.Value = enabled ? new Dictionary<object, Dictionary<string, object>>() : null;
            return new Scope(previous, previousDiscovery);
        }

        // Owner isolates provider instances; each Enter allocates a fresh poll lifetime.
        internal static T GetOrDiscover<T>(object owner, string key, Func<T> discover)
        {
            var cache = Discovery.Value;
            if (!IsActive || cache == null) return discover();
            lock (cache)
            {
                if (!cache.TryGetValue(owner, out var entries))
                    cache[owner] = entries = new Dictionary<string, object>(StringComparer.Ordinal);
                if (entries.TryGetValue(key, out var value)) return (T)value;
                var result = discover();
                entries[key] = result;
                return result;
            }
        }

        private sealed class Scope : IDisposable
        {
            private readonly bool _previous;
            private readonly Dictionary<object, Dictionary<string, object>> _previousDiscovery;
            private bool _disposed;
            public Scope(bool previous, Dictionary<object, Dictionary<string, object>> previousDiscovery)
            {
                _previous = previous;
                _previousDiscovery = previousDiscovery;
            }
            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                Active.Value = _previous;
                Discovery.Value = _previousDiscovery;
            }
        }
    }
}
