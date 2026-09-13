using Newtonsoft.Json;
using Playnite.SDK;
using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.GuildWars2
{
    /// <summary>
    /// Disk-backed cache of the Guild Wars 2 achievement catalog - roughly 6,900 achievements across
    /// 19 groups and 355 categories, which costs about 38 requests to page in full.
    ///
    /// The catalog only changes when ArenaNet ships a game build, and /v2/build reports exactly that,
    /// so the cache is keyed on the build id rather than on an age. A stale-after-N-hours rule would
    /// re-page an unchanged catalog on a quiet week and serve a stale one an hour after a patch.
    /// </summary>
    internal sealed class Gw2CatalogCache
    {
        private readonly ILogger _logger;
        private readonly string _cacheDirectory;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

        private Gw2Catalog _memoryCache;

        public Gw2CatalogCache(ILogger logger, string pluginUserDataPath)
        {
            _logger = logger;
            _cacheDirectory = string.IsNullOrWhiteSpace(pluginUserDataPath)
                ? null
                : Path.Combine(pluginUserDataPath, "gw2");
        }

        /// <summary>
        /// Returns the catalog for the given language, paging it in only when the cached copy is
        /// missing, built against an older game build, or in a different language.
        /// </summary>
        public async Task<Gw2Catalog> GetCatalogAsync(
            Gw2ApiClient api,
            string language,
            IProgress<string> progress,
            CancellationToken cancel)
        {
            await _gate.WaitAsync(cancel).ConfigureAwait(false);
            try
            {
                var buildId = await GetBuildIdAsync(api, cancel).ConfigureAwait(false);

                if (IsCurrent(_memoryCache, buildId, language))
                {
                    return _memoryCache;
                }

                var cached = LoadFromDisk(language);
                if (IsCurrent(cached, buildId, language))
                {
                    _memoryCache = cached;
                    return cached;
                }

                var fresh = await FetchCatalogAsync(api, buildId, language, progress, cancel).ConfigureAwait(false);
                if (fresh != null && fresh.IsUsable)
                {
                    _memoryCache = fresh;
                    SaveToDisk(fresh);
                    return fresh;
                }

                // A failed page-in must not discard a usable older catalog: stale names are far
                // better than an empty payload, which the cache guard would treat as an erasure.
                if (cached != null && cached.IsUsable)
                {
                    _logger?.Warn("[GW2] Could not refresh the catalog; using the cached copy.");
                    _memoryCache = cached;
                    return cached;
                }

                return fresh;
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// Reads the current build id. A failure here is not fatal - it only means the build cannot
        /// be compared, so the cached catalog is accepted as-is rather than re-paged on a blip.
        /// </summary>
        private async Task<int> GetBuildIdAsync(Gw2ApiClient api, CancellationToken cancel)
        {
            try
            {
                return await api.GetBuildIdAsync(cancel).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, "[GW2] Could not read the game build id.");
                return 0;
            }
        }

        /// <summary>
        /// A catalog is current when it is usable, in the right language, and either built at the
        /// same build id or being compared against an unknown one.
        /// </summary>
        private static bool IsCurrent(Gw2Catalog catalog, int buildId, string language)
        {
            if (catalog == null || !catalog.IsUsable)
            {
                return false;
            }

            if (!string.Equals(catalog.Language, language, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return buildId <= 0 || catalog.BuildId == buildId;
        }

        private async Task<Gw2Catalog> FetchCatalogAsync(
            Gw2ApiClient api,
            int buildId,
            string language,
            IProgress<string> progress,
            CancellationToken cancel)
        {
            progress?.Report("groups");
            var groups = await api.GetGroupsAsync(language, cancel).ConfigureAwait(false);

            progress?.Report("categories");
            var categories = await api.GetCategoriesAsync(language, cancel).ConfigureAwait(false);

            // Achievements are paged by the ids the categories list rather than by /v2/achievements,
            // which also returns roughly 1,350 orphans that belong to no category and so have no
            // place in the tree.
            var ids = Gw2AchievementMapper.CollectAchievementIds(categories);

            progress?.Report("achievements");
            var achievements = await api
                .GetAchievementsAsync(ids, language, null, cancel)
                .ConfigureAwait(false);

            _logger?.Info(
                $"[GW2] Paged in {achievements.Count} achievements across {categories.Count} categories " +
                $"and {groups.Count} groups for build {buildId} ({language}).");

            return new Gw2Catalog
            {
                BuildId = buildId,
                Language = language,
                Groups = groups,
                Categories = categories,
                Achievements = achievements
            };
        }

        private string GetCachePath(string language)
            => _cacheDirectory == null ? null : Path.Combine(_cacheDirectory, $"catalog-{language}.json");

        private Gw2Catalog LoadFromDisk(string language)
        {
            var path = GetCachePath(language);
            if (path == null || !File.Exists(path))
            {
                return null;
            }

            try
            {
                var catalog = JsonConvert.DeserializeObject<Gw2Catalog>(File.ReadAllText(path, Encoding.UTF8));
                return catalog != null && catalog.IsUsable ? catalog : null;
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"[GW2] Cached catalog at '{path}' could not be read.");
                return null;
            }
        }

        private void SaveToDisk(Gw2Catalog catalog)
        {
            var path = GetCachePath(catalog?.Language);
            if (path == null)
            {
                return;
            }

            try
            {
                Directory.CreateDirectory(_cacheDirectory);
                File.WriteAllText(path, JsonConvert.SerializeObject(catalog), Encoding.UTF8);
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"[GW2] Could not cache the catalog to '{path}'.");
            }
        }
    }
}
