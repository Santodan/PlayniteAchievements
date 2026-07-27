using Newtonsoft.Json;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PlayniteAchievements.Services.Cache;

namespace PlayniteAchievements.Providers.Ffxiv
{
    /// <summary>
    /// Disk-backed cache of the FFXIV Collect achievement catalog. The catalog only
    /// changes with game patches, so it is refreshed at most once per day to avoid
    /// re-paging ~3500 achievements on every refresh.
    /// </summary>
    internal sealed class FfxivCatalogCache
    {
        private static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

        private readonly ILogger _logger;
        private readonly string _cacheFilePath;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

        private List<FfxivAchievement> _memoryCache;

        public FfxivCatalogCache(ILogger logger, string pluginUserDataPath)
        {
            _logger = logger;
            var dir = Path.Combine(pluginUserDataPath ?? string.Empty, "ffxiv");
            _cacheFilePath = Path.Combine(dir, "catalog.json");
        }

        /// <summary>
        /// Returns the catalog, fetching from the API when the on-disk copy is
        /// missing or stale.
        /// </summary>
        public async Task<List<FfxivAchievement>> GetCatalogAsync(FfxivApiClient api, CancellationToken cancel)
        {
            await _gate.WaitAsync(cancel).ConfigureAwait(false);
            try
            {
                if (_memoryCache != null)
                {
                    return _memoryCache;
                }

                if (TryLoadFromDisk(out var cached))
                {
                    _memoryCache = cached;
                    return cached;
                }

                var fresh = await api.FetchCatalogAsync(cancel).ConfigureAwait(false);
                if (fresh != null && fresh.Count > 0)
                {
                    _memoryCache = fresh;
                    SaveToDisk(fresh);
                }

                return fresh ?? new List<FfxivAchievement>();
            }
            finally
            {
                _gate.Release();
            }
        }

        private bool TryLoadFromDisk(out List<FfxivAchievement> catalog)
        {
            catalog = null;

            try
            {
                if (!File.Exists(_cacheFilePath))
                {
                    return false;
                }

                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(_cacheFilePath) > MaxAge)
                {
                    return false;
                }

                var json = File.ReadAllText(_cacheFilePath);
                var entry = JsonConvert.DeserializeObject<CacheEntry>(json);
                if (entry?.Achievements == null || entry.Achievements.Count == 0)
                {
                    return false;
                }

                catalog = entry.Achievements;
                return true;
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, "[FFXIV] Failed to read catalog cache.");
                return false;
            }
        }

        private void SaveToDisk(List<FfxivAchievement> catalog)
        {
            try
            {
                var dir = Path.GetDirectoryName(_cacheFilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                var entry = new CacheEntry
                {
                    FetchedUtc = DateTime.UtcNow,
                    Achievements = catalog
                };

                File.WriteAllText(_cacheFilePath, JsonConvert.SerializeObject(entry));
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, "[FFXIV] Failed to write catalog cache.");
            }
        }

        private sealed class CacheEntry
        {
            public DateTime FetchedUtc { get; set; }
            public List<FfxivAchievement> Achievements { get; set; }
        }
    }
}
