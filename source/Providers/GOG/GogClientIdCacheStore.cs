using Newtonsoft.Json;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using PlayniteAchievements.Services.Cache;

namespace PlayniteAchievements.Providers.GOG
{
    public sealed class GogClientIdCacheStore
    {
        private const string CacheFileName = "client_id_cache.json.gz";
        private const int CacheSchemaVersion = 1;

        private readonly object _syncRoot = new object();
        private readonly ILogger _logger;
        private readonly string _cachePath;
        private readonly TimeSpan _ttl;

        private bool _dirty;
        private Dictionary<string, CacheEntry> _entries = new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);

        private sealed class CacheFile
        {
            [JsonProperty("version")]
            public int Version { get; set; } = CacheSchemaVersion;

            [JsonProperty("entries")]
            public Dictionary<string, CacheEntry> Entries { get; set; } = new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);
        }

        private sealed class CacheEntry
        {
            [JsonProperty("client_id")]
            public string ClientId { get; set; }

            [JsonProperty("updated_utc")]
            public DateTime UpdatedUtc { get; set; }
        }

        public GogClientIdCacheStore(string pluginUserDataPath, ILogger logger, TimeSpan? ttl = null)
        {
            _logger = logger;
            _ttl = ttl ?? TimeSpan.FromDays(30);

            var cacheDir = Path.Combine(pluginUserDataPath ?? string.Empty, "gog");
            Directory.CreateDirectory(cacheDir);
            _cachePath = Path.Combine(cacheDir, CacheFileName);

            LoadFromDisk();
        }

        public bool TryGetClientId(string productId, out string clientId)
        {
            clientId = null;
            if (string.IsNullOrWhiteSpace(productId))
            {
                return false;
            }

            var normalizedProductId = productId.Trim();
            if (normalizedProductId.Length == 0)
            {
                return false;
            }

            lock (_syncRoot)
            {
                if (!_entries.TryGetValue(normalizedProductId, out var entry))
                {
                    return false;
                }

                if (entry == null || string.IsNullOrWhiteSpace(entry.ClientId))
                {
                    _entries.Remove(normalizedProductId);
                    _dirty = true;
                    return false;
                }

                if (IsExpired(entry.UpdatedUtc, DateTime.UtcNow))
                {
                    _entries.Remove(normalizedProductId);
                    _dirty = true;
                    return false;
                }

                clientId = entry.ClientId;
                return true;
            }
        }

        public void SetClientId(string productId, string clientId)
        {
            if (string.IsNullOrWhiteSpace(productId) || string.IsNullOrWhiteSpace(clientId))
            {
                return;
            }

            var normalizedProductId = productId.Trim();
            var normalizedClientId = clientId.Trim();
            if (normalizedProductId.Length == 0 || normalizedClientId.Length == 0)
            {
                return;
            }

            lock (_syncRoot)
            {
                if (_entries.TryGetValue(normalizedProductId, out var existing) &&
                    existing != null &&
                    string.Equals(existing.ClientId, normalizedClientId, StringComparison.Ordinal))
                {
                    return;
                }

                _entries[normalizedProductId] = new CacheEntry
                {
                    ClientId = normalizedClientId,
                    UpdatedUtc = DateTime.UtcNow
                };
                _dirty = true;
            }
        }

        public void Save()
        {
            CacheFile snapshot;

            lock (_syncRoot)
            {
                if (_entries.Count > 0 && PruneExpiredUnsafe(DateTime.UtcNow) > 0)
                {
                    _dirty = true;
                }

                if (!_dirty)
                {
                    return;
                }

                snapshot = new CacheFile
                {
                    Entries = _entries.ToDictionary(
                        pair => pair.Key,
                        pair => new CacheEntry
                        {
                            ClientId = pair.Value?.ClientId,
                            UpdatedUtc = pair.Value?.UpdatedUtc ?? DateTime.UtcNow
                        },
                        StringComparer.OrdinalIgnoreCase)
                };
            }

            var tempPath = _cachePath + ".tmp";
            try
            {
                var json = JsonConvert.SerializeObject(snapshot, Formatting.None);
                var bytes = Encoding.UTF8.GetBytes(json);

                using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var gzip = new GZipStream(stream, CompressionMode.Compress))
                {
                    gzip.Write(bytes, 0, bytes.Length);
                }

                if (File.Exists(_cachePath))
                {
                    try
                    {
                        File.Replace(tempPath, _cachePath, destinationBackupFileName: null);
                    }
                    catch
                    {
                        File.Delete(_cachePath);
                        File.Move(tempPath, _cachePath);
                    }
                }
                else
                {
                    File.Move(tempPath, _cachePath);
                }

                lock (_syncRoot)
                {
                    _dirty = false;
                }
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"[GogCache] Failed to save client_id cache: {_cachePath}");
                lock (_syncRoot)
                {
                    _dirty = true;
                }
            }
            finally
            {
                try
                {
                    if (File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }
                }
                catch
                {
                    // Best effort cleanup.
                }
            }
        }

        private void LoadFromDisk()
        {
            if (!File.Exists(_cachePath))
            {
                return;
            }

            try
            {
                CacheFile parsed;
                using (var stream = new FileStream(_cachePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var gzip = new GZipStream(stream, CompressionMode.Decompress))
                using (var reader = new StreamReader(gzip, Encoding.UTF8))
                {
                    var json = reader.ReadToEnd();
                    parsed = JsonConvert.DeserializeObject<CacheFile>(json);
                }

                var loadedEntries = new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);
                var nowUtc = DateTime.UtcNow;

                foreach (var pair in parsed?.Entries ?? Enumerable.Empty<KeyValuePair<string, CacheEntry>>())
                {
                    var productId = pair.Key?.Trim();
                    var entry = pair.Value;
                    var clientId = entry?.ClientId?.Trim();

                    if (string.IsNullOrWhiteSpace(productId) || string.IsNullOrWhiteSpace(clientId))
                    {
                        continue;
                    }

                    var updatedUtc = entry.UpdatedUtc == default
                        ? nowUtc
                        : NormalizeUtc(entry.UpdatedUtc);

                    if (IsExpired(updatedUtc, nowUtc))
                    {
                        continue;
                    }

                    loadedEntries[productId] = new CacheEntry
                    {
                        ClientId = clientId,
                        UpdatedUtc = updatedUtc
                    };
                }

                lock (_syncRoot)
                {
                    _entries = loadedEntries;
                    _dirty = false;
                }
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"[GogCache] Failed to load client_id cache. Rebuilding cache in memory: {_cachePath}");
                lock (_syncRoot)
                {
                    _entries = new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);
                    _dirty = false;
                }
            }
        }

        private int PruneExpiredUnsafe(DateTime nowUtc)
        {
            var staleKeys = new List<string>();
            foreach (var pair in _entries)
            {
                if (pair.Value == null || string.IsNullOrWhiteSpace(pair.Value.ClientId) || IsExpired(pair.Value.UpdatedUtc, nowUtc))
                {
                    staleKeys.Add(pair.Key);
                }
            }

            foreach (var key in staleKeys)
            {
                _entries.Remove(key);
            }

            return staleKeys.Count;
        }

        private bool IsExpired(DateTime updatedUtc, DateTime nowUtc)
        {
            var normalized = NormalizeUtc(updatedUtc);
            return nowUtc - normalized > _ttl;
        }

        private static DateTime NormalizeUtc(DateTime value)
        {
            if (value.Kind == DateTimeKind.Utc)
            {
                return value;
            }

            if (value.Kind == DateTimeKind.Unspecified)
            {
                return DateTime.SpecifyKind(value, DateTimeKind.Utc);
            }

            return value.ToUniversalTime();
        }
    }
}
