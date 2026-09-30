using Newtonsoft.Json;
using Playnite.SDK;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.Hypixel
{
    /// <summary>
    /// The Hypixel achievement definitions, fetched on every refresh (one keyless request) and
    /// kept on disk only as a fallback. A failed fetch then still yields points, completion rates
    /// and stable identifiers instead of rows keyed by name alone.
    /// </summary>
    internal sealed class HypixelCatalogCache
    {
        private readonly ILogger _logger;
        private readonly string _cacheFilePath;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

        public HypixelCatalogCache(ILogger logger, string pluginUserDataPath)
        {
            _logger = logger;
            _cacheFilePath = string.IsNullOrWhiteSpace(pluginUserDataPath)
                ? null
                : Path.Combine(pluginUserDataPath, "hypixel", "achievements.json");
        }

        /// <summary>
        /// Returns the current definitions, or the last copy saved to disk when the fetch fails.
        /// Returns null when neither is available.
        /// </summary>
        public async Task<HypixelAchievementsResponse> GetCatalogAsync(HypixelApiClient api, CancellationToken cancel)
        {
            await _gate.WaitAsync(cancel).ConfigureAwait(false);
            try
            {
                HypixelAchievementsResponse fresh = null;
                try
                {
                    fresh = await api.FetchCatalogAsync(cancel).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancel.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger?.Warn(ex, "[Hypixel] Could not fetch the achievement definitions.");
                }

                if (fresh != null)
                {
                    SaveToDisk(fresh);
                    return fresh;
                }

                var cached = LoadFromDisk();
                if (cached != null)
                {
                    _logger?.Warn("[Hypixel] Using the saved copy of the achievement definitions.");
                }

                return cached;
            }
            finally
            {
                _gate.Release();
            }
        }

        private HypixelAchievementsResponse LoadFromDisk()
        {
            if (_cacheFilePath == null || !File.Exists(_cacheFilePath))
            {
                return null;
            }

            try
            {
                var response = JsonConvert.DeserializeObject<HypixelAchievementsResponse>(File.ReadAllText(_cacheFilePath));
                return response?.Achievements != null && response.Achievements.Count > 0 ? response : null;
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"[Hypixel] Saved achievement definitions at '{_cacheFilePath}' could not be read.");
                return null;
            }
        }

        private void SaveToDisk(HypixelAchievementsResponse response)
        {
            if (_cacheFilePath == null)
            {
                return;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_cacheFilePath));
                File.WriteAllText(_cacheFilePath, JsonConvert.SerializeObject(response));
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"[Hypixel] Could not save the achievement definitions to '{_cacheFilePath}'.");
            }
        }
    }
}
