using Playnite.SDK;
using PlayniteAchievements.Providers.RetroAchievements.Hashing;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PlayniteAchievements.Providers.RetroAchievements
{
    /// <summary>
    /// How a game's RA game ID was resolved. Entries written before this field existed
    /// deserialize as <see cref="HashMatch"/>, which is what they recorded.
    /// </summary>
    internal enum RaHashCacheResolution
    {
        HashMatch = 0,
        NameMatch = 1,
        None = 2
    }

    /// <summary>
    /// Cache entry for a game's RA resolution.
    /// A hash match stores the matched ROM's metadata and the RA game ID to fetch.
    /// Every entry also records the hashes computed for each candidate file, so an
    /// unchanged file is re-matched against the current hash index without re-reading it.
    /// </summary>
    internal sealed class RaHashCacheEntry
    {
        [JsonConverter(typeof(StringEnumConverter))]
        public RaHashCacheResolution Resolution { get; set; }
        public string MatchedRomPath { get; set; }
        public long FileSize { get; set; }
        public long LastWriteTicksUtc { get; set; }
        public int RaGameId { get; set; }
        public List<RaHashCacheDependency> Dependencies { get; set; }

        // Name matches are valid while the Playnite name and console are unchanged.
        public string NameMatchedGameName { get; set; }
        public int? NameMatchedConsoleId { get; set; }

        public List<RaHashCacheCandidate> Candidates { get; set; }
    }

    /// <summary>
    /// Hashes computed for one candidate file by one hasher.
    /// <see cref="Complete"/> is false when hashing stopped at the first matching archive entry,
    /// so the list may be missing hashes of entries that were never read.
    /// </summary>
    internal sealed class RaHashCacheCandidate
    {
        public string Path { get; set; }
        public string HasherName { get; set; }
        public List<RaHashCacheDependency> Dependencies { get; set; }
        public List<string> Hashes { get; set; }
        public bool Complete { get; set; }
    }

    internal sealed class RaHashCacheDependency
    {
        public string Path { get; set; }
        public long FileSize { get; set; }
        public long LastWriteTicksUtc { get; set; }
    }

    /// <summary>
    /// Persistent cache storing RA game IDs keyed by Playnite Game ID.
    /// Allows skipping expensive hash computation when file stats match.
    /// </summary>
    internal sealed class RetroAchievementsHashCacheStore
    {
        private readonly ILogger _logger;
        private readonly string _cacheFilePath;
        private Dictionary<string, RaHashCacheEntry> _cache;
        private readonly object _lock = new object();
        private int _pendingWrites;

        private static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore,
            Formatting = Formatting.Indented
        };

        public RetroAchievementsHashCacheStore(ILogger logger, string pluginUserDataPath)
        {
            _logger = logger;
            var dir = Path.Combine(pluginUserDataPath ?? string.Empty, "ra");
            Directory.CreateDirectory(dir);
            _cacheFilePath = Path.Combine(dir, "hash_cache.json");
            _cache = new Dictionary<string, RaHashCacheEntry>(StringComparer.OrdinalIgnoreCase);
            Load();
        }

        private void Load()
        {
            if (!File.Exists(_cacheFilePath))
            {
                return;
            }

            try
            {
                var json = File.ReadAllText(_cacheFilePath, Encoding.UTF8);
                var data = JsonConvert.DeserializeObject<Dictionary<string, RaHashCacheEntry>>(json, JsonSettings);
                if (data != null)
                {
                    _cache = new Dictionary<string, RaHashCacheEntry>(data, StringComparer.OrdinalIgnoreCase);
                    _logger?.Debug($"[RA] Loaded hash cache with {_cache.Count} entries from '{_cacheFilePath}'");
                }
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"[RA] Failed to load hash cache from '{_cacheFilePath}', starting fresh");
                _cache = new Dictionary<string, RaHashCacheEntry>(StringComparer.OrdinalIgnoreCase);
            }
        }

        public void Save()
        {
            SaveIfPending(1);
        }

        /// <summary>
        /// Saves when at least <paramref name="minPendingWrites"/> writes are unsaved.
        /// Lets a long scan persist progress periodically instead of only at the end.
        /// </summary>
        public void SaveIfPending(int minPendingWrites)
        {
            lock (_lock)
            {
                if (_pendingWrites == 0 || _pendingWrites < minPendingWrites)
                {
                    return;
                }

                try
                {
                    var json = JsonConvert.SerializeObject(_cache, JsonSettings);
                    var tmp = _cacheFilePath + ".tmp";
                    File.WriteAllText(tmp, json, Encoding.UTF8);

                    if (File.Exists(_cacheFilePath))
                    {
                        File.Replace(tmp, _cacheFilePath, destinationBackupFileName: null);
                    }
                    else
                    {
                        File.Move(tmp, _cacheFilePath);
                    }

                    _pendingWrites = 0;
                    _logger?.Debug($"[RA] Saved hash cache with {_cache.Count} entries to '{_cacheFilePath}'");
                }
                catch (Exception ex)
                {
                    _logger?.Warn(ex, $"[RA] Failed to save hash cache to '{_cacheFilePath}'");
                }
            }
        }

        public bool TryGet(Guid playniteGameId, out RaHashCacheEntry entry)
        {
            lock (_lock)
            {
                return _cache.TryGetValue(playniteGameId.ToString(), out entry);
            }
        }

        public void Set(Guid playniteGameId, RaHashCacheEntry entry)
        {
            lock (_lock)
            {
                _cache[playniteGameId.ToString()] = entry;
                _pendingWrites++;
            }
        }

        public void Remove(Guid playniteGameId)
        {
            lock (_lock)
            {
                if (_cache.Remove(playniteGameId.ToString()))
                {
                    _pendingWrites++;
                }
            }
        }

        internal static List<RaHashCacheDependency> CaptureDependencySnapshot(string matchedPath)
        {
            var dependencies = new List<RaHashCacheDependency>();

            IReadOnlyList<string> dependencyPaths = null;
            if (CueTrackReader.IsCuePath(matchedPath) &&
                CueTrackReader.TryGetDataTrackDependencies(matchedPath, out var cueDependencies, out _))
            {
                dependencyPaths = cueDependencies;
            }
            else if (!string.IsNullOrWhiteSpace(matchedPath))
            {
                dependencyPaths = new[] { matchedPath };
            }

            if (dependencyPaths == null)
            {
                return dependencies;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var dependencyPath in dependencyPaths)
            {
                if (string.IsNullOrWhiteSpace(dependencyPath))
                {
                    continue;
                }

                var normalizedPath = dependencyPath.Trim().Trim('"');
                if (!seen.Add(normalizedPath))
                {
                    continue;
                }

                try
                {
                    var fi = new FileInfo(normalizedPath);
                    if (!fi.Exists)
                    {
                        continue;
                    }

                    dependencies.Add(new RaHashCacheDependency
                    {
                        Path = normalizedPath,
                        FileSize = fi.Length,
                        LastWriteTicksUtc = fi.LastWriteTimeUtc.Ticks
                    });
                }
                catch
                {
                    // Ignore individual dependency stat failures.
                }
            }

            return dependencies;
        }

        internal static bool ValidateDependencySnapshot(IReadOnlyList<RaHashCacheDependency> dependencies)
        {
            if (dependencies == null || dependencies.Count == 0)
            {
                return false;
            }

            foreach (var dependency in dependencies)
            {
                if (dependency == null || string.IsNullOrWhiteSpace(dependency.Path))
                {
                    return false;
                }

                try
                {
                    var fi = new FileInfo(dependency.Path);
                    if (!fi.Exists)
                    {
                        return false;
                    }

                    if (fi.Length != dependency.FileSize)
                    {
                        return false;
                    }

                    if (fi.LastWriteTimeUtc.Ticks != dependency.LastWriteTicksUtc)
                    {
                        return false;
                    }
                }
                catch
                {
                    return false;
                }
            }

            return true;
        }
    }
}
