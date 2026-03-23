using Playnite.SDK;
using System;
using System.Collections.Concurrent;
using System.Threading;

namespace PlayniteAchievements.Services
{
    /// <summary>
    /// Thread-safe cache for tracking when each provider last probed authentication state.
    /// Used to implement expiry-based caching to avoid excessive auth probing.
    /// </summary>
    public sealed class AuthProbeCache
    {
        private readonly ConcurrentDictionary<string, ProbeEntry> _cache = new ConcurrentDictionary<string, ProbeEntry>();
        private readonly ILogger _logger;

        /// <summary>
        /// Default cache durations for each provider.
        /// These can be overridden per-provider via the ProbeCacheDuration property.
        /// </summary>
        public static readonly TimeSpan DefaultCacheDuration = TimeSpan.FromMinutes(30);

        /// <summary>
        /// Provider-specific cache durations based on token/cookie lifetime characteristics.
        /// </summary>
        public static class ProviderCacheDurations
        {
            /// <summary>
            /// Steam: CEF cookies persist well, 1 hour is reasonable.
            /// </summary>
            public static readonly TimeSpan Steam = TimeSpan.FromHours(1);

            /// <summary>
            /// GOG: Token-based with expiry, 30 minutes.
            /// </summary>
            public static readonly TimeSpan GOG = TimeSpan.FromMinutes(30);

            /// <summary>
            /// Epic: OAuth tokens in PersistedSettings, 15 minutes.
            /// </summary>
            public static readonly TimeSpan Epic = TimeSpan.FromMinutes(15);

            /// <summary>
            /// PSN: Mobile tokens have short lifetime, 10 minutes.
            /// </summary>
            public static readonly TimeSpan PSN = TimeSpan.FromMinutes(10);

            /// <summary>
            /// Xbox: XSTS tokens have limited lifetime, 20 minutes.
            /// </summary>
            public static readonly TimeSpan Xbox = TimeSpan.FromMinutes(20);

            /// <summary>
            /// Exophase: Cookie-based, 1 hour.
            /// </summary>
            public static readonly TimeSpan Exophase = TimeSpan.FromHours(1);
        }

        private sealed class ProbeEntry
        {
            public DateTime LastProbeUtc { get; set; }
            public bool LastResult { get; set; }
            public string UserId { get; set; }
        }

        public AuthProbeCache(ILogger logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Checks if a cached probe result is still valid for the given provider.
        /// </summary>
        /// <param name="providerKey">The provider identifier.</param>
        /// <param name="cacheDuration">The maximum age of a valid cache entry.</param>
        /// <returns>True if a valid cached entry exists and was successful.</returns>
        public bool IsCacheValid(string providerKey, TimeSpan cacheDuration)
        {
            if (string.IsNullOrWhiteSpace(providerKey))
                return false;

            if (!_cache.TryGetValue(providerKey, out var entry))
                return false;

            var age = DateTime.UtcNow - entry.LastProbeUtc;
            var isValid = entry.LastResult && age < cacheDuration;

            if (isValid)
            {
                _logger?.Debug($"[{providerKey}] Auth probe cache is valid (age: {age.TotalSeconds:F0}s, max: {cacheDuration.TotalSeconds:F0}s).");
            }

            return isValid;
        }

        /// <summary>
        /// Records a probe result in the cache.
        /// </summary>
        /// <param name="providerKey">The provider identifier.</param>
        /// <param name="isAuthenticated">Whether the probe indicated successful authentication.</param>
        /// <param name="userId">Optional user ID if authenticated.</param>
        public void RecordProbe(string providerKey, bool isAuthenticated, string userId = null)
        {
            if (string.IsNullOrWhiteSpace(providerKey))
                return;

            var entry = new ProbeEntry
            {
                LastProbeUtc = DateTime.UtcNow,
                LastResult = isAuthenticated,
                UserId = userId
            };

            _cache.AddOrUpdate(providerKey, entry, (key, existing) => entry);
            _logger?.Debug($"[{providerKey}] Recorded auth probe result: {(isAuthenticated ? "authenticated" : "not authenticated")}.");
        }

        /// <summary>
        /// Gets the cached user ID for a provider if available and valid.
        /// </summary>
        /// <param name="providerKey">The provider identifier.</param>
        /// <param name="cacheDuration">The maximum age of a valid cache entry.</param>
        /// <param name="userId">The cached user ID if valid.</param>
        /// <returns>True if a valid cached entry with user ID exists.</returns>
        public bool TryGetCachedUserId(string providerKey, TimeSpan cacheDuration, out string userId)
        {
            userId = null;

            if (string.IsNullOrWhiteSpace(providerKey))
                return false;

            if (!_cache.TryGetValue(providerKey, out var entry))
                return false;

            var age = DateTime.UtcNow - entry.LastProbeUtc;
            if (!entry.LastResult || age >= cacheDuration)
                return false;

            userId = entry.UserId;
            return !string.IsNullOrWhiteSpace(userId);
        }

        /// <summary>
        /// Invalidates the cache for a specific provider.
        /// </summary>
        /// <param name="providerKey">The provider identifier.</param>
        public void Invalidate(string providerKey)
        {
            if (string.IsNullOrWhiteSpace(providerKey))
                return;

            _cache.TryRemove(providerKey, out _);
            _logger?.Debug($"[{providerKey}] Auth probe cache invalidated.");
        }

        /// <summary>
        /// Invalidates the cache for all providers.
        /// </summary>
        public void InvalidateAll()
        {
            _cache.Clear();
            _logger?.Debug("All auth probe caches invalidated.");
        }

        /// <summary>
        /// Gets the time since the last probe for a provider.
        /// </summary>
        /// <param name="providerKey">The provider identifier.</param>
        /// <returns>Time since last probe, or null if never probed.</returns>
        public TimeSpan? GetTimeSinceLastProbe(string providerKey)
        {
            if (string.IsNullOrWhiteSpace(providerKey))
                return null;

            if (!_cache.TryGetValue(providerKey, out var entry))
                return null;

            return DateTime.UtcNow - entry.LastProbeUtc;
        }
    }
}
