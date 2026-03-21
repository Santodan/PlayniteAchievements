using PlayniteAchievements.Models;
using PlayniteAchievements.Providers.RetroAchievements.Models;
using PlayniteAchievements.Providers.Settings;
using Playnite.SDK;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.RetroAchievements
{
    internal sealed class RetroAchievementsHashIndexStore
    {
        private const int PageSize = 5000;

        private static readonly JsonSerializerSettings _jsonSettings = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore
        };

        private readonly ILogger _logger;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly RetroAchievementsApiClient _api;

        private readonly string _cacheDir;

        private sealed class CachedIndex
        {
            public DateTime UpdatedUtc { get; set; }
            public Dictionary<string, int> Index { get; set; }
        }

        private readonly ConcurrentDictionary<int, CachedIndex> _memory = new ConcurrentDictionary<int, CachedIndex>();
        private readonly ConcurrentDictionary<int, SemaphoreSlim> _locks = new ConcurrentDictionary<int, SemaphoreSlim>();

        public RetroAchievementsHashIndexStore(
            ILogger logger,
            PlayniteAchievementsSettings settings,
            RetroAchievementsApiClient api,
            string pluginUserDataPath)
        {
            _logger = logger;
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _api = api ?? throw new ArgumentNullException(nameof(api));

            _cacheDir = Path.Combine(pluginUserDataPath ?? string.Empty, "ra");
            Directory.CreateDirectory(_cacheDir);
        }

        public async Task<Dictionary<string, int>> GetHashIndexAsync(int consoleId, CancellationToken cancel)
        {
            if (_memory.TryGetValue(consoleId, out var cached) && !IsStale(cached.UpdatedUtc))
            {
                _logger?.Debug($"[RA] Using cached hash index for consoleId={consoleId} with {cached.Index.Count} hashes.");
                return cached.Index;
            }

            var gate = _locks.GetOrAdd(consoleId, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancel).ConfigureAwait(false);
            try
            {
                if (_memory.TryGetValue(consoleId, out cached) && !IsStale(cached.UpdatedUtc))
                {
                    return cached.Index;
                }

                var disk = await TryLoadFromDiskAsync(consoleId, cancel).ConfigureAwait(false);
                if (disk != null && !IsStale(disk.UpdatedUtc))
                {
                    _logger?.Info($"[RA] Loaded hash index from disk for consoleId={consoleId} with {disk.HashToGameId.Count} hashes (cached at {disk.UpdatedUtc:yyyy-MM-dd HH:mm:ss} UTC).");
                    _memory[consoleId] = new CachedIndex { UpdatedUtc = disk.UpdatedUtc, Index = disk.HashToGameId };
                    return disk.HashToGameId;
                }

                var rebuilt = await RebuildIndexAsync(consoleId, cancel).ConfigureAwait(false);
                _memory[consoleId] = new CachedIndex { UpdatedUtc = rebuilt.UpdatedUtc, Index = rebuilt.HashToGameId };
                await SaveToDiskAsync(consoleId, rebuilt, cancel).ConfigureAwait(false);
                return rebuilt.HashToGameId;
            }
            finally
            {
                gate.Release();
            }
        }

        private bool IsStale(DateTime updatedUtc)
        {
            updatedUtc = DateTime.SpecifyKind(updatedUtc, DateTimeKind.Utc);
            var raSettings = ProviderSettings.Load<RetroAchievementsSettings>();
            var maxAgeDays = Math.Max(1, raSettings.HashIndexMaxAgeDays);
            return DateTime.UtcNow - updatedUtc > TimeSpan.FromDays(maxAgeDays);
        }

        private string CachePath(int consoleId) => Path.Combine(_cacheDir, $"hashindex_{consoleId}.json.gz");

        private async Task<RaHashIndexCacheFile> TryLoadFromDiskAsync(int consoleId, CancellationToken cancel)
        {
            var path = CachePath(consoleId);
            if (!File.Exists(path))
            {
                return null;
            }

            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var gz = new GZipStream(fs, CompressionMode.Decompress))
                using (var ms = new MemoryStream())
                {
                    await gz.CopyToAsync(ms, 81920, cancel).ConfigureAwait(false);
                    ms.Position = 0;
                    using (var sr = new StreamReader(ms))
                    using (var jr = new JsonTextReader(sr))
                    {
                        var json = JsonSerializer.CreateDefault(_jsonSettings).Deserialize<RaHashIndexCacheFile>(jr);
                        return json;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"[RA] Failed to load hash index cache: {path}");
                return null;
            }
        }

        private async Task SaveToDiskAsync(int consoleId, RaHashIndexCacheFile file, CancellationToken cancel)
        {
            var path = CachePath(consoleId);
            var tmp = path + ".tmp";

            try
            {
                var json = JsonConvert.SerializeObject(file, _jsonSettings);
                var jsonBytes = Encoding.UTF8.GetBytes(json);

                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var gz = new GZipStream(fs, CompressionMode.Compress))
                {
                    await gz.WriteAsync(jsonBytes, 0, jsonBytes.Length).ConfigureAwait(false);
                }

                if (File.Exists(path))
                {
                    try
                    {
                        File.Replace(tmp, path, destinationBackupFileName: null);
                        return;
                    }
                    catch
                    {
                        File.Delete(path);
                    }
                }

                File.Move(tmp, path);
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }

        private async Task<RaHashIndexCacheFile> RebuildIndexAsync(int consoleId, CancellationToken cancel)
        {
            _logger?.Info($"[RA] Rebuilding hash index for consoleId={consoleId}...");

            var index = new Dictionary<string, int>(StringComparer.Ordinal);

            var offset = 0;
            while (true)
            {
                cancel.ThrowIfCancellationRequested();

                var json = await _api.GetGameListPageAsync(consoleId, offset, PageSize, cancel).ConfigureAwait(false);
                _logger?.Debug($"[RA] API returned {json?.Length ?? 0} characters for consoleId={consoleId} offset={offset}.");
                if (json != null && json.Length < 500)
                {
                    _logger?.Debug($"[RA] API response preview: {json}");
                }

                var items = EnumerateGameListItems(json);
                _logger?.Debug($"[RA] Parsed {items.Count} game items from API response.");

                // Log first few items for debugging
                for (var i = 0; i < Math.Min(3, items.Count); i++)
                {
                    var item = items[i];
                    var hashesType = item.Hashes?.GetType().FullName ?? "null";
                    var hashesValue = item.Hashes?.ToString() ?? "null";
                    _logger?.Debug($"[RA] Item {i}: ID={item.ID}, GameID={item.GameID}, Hashes type={hashesType}, value={hashesValue}");
                }

                foreach (var item in items)
                {
                    cancel.ThrowIfCancellationRequested();

                    var gameId = item.ID;
                    if (gameId == 0)
                        gameId = item.GameID;
                    if (gameId == 0)
                        continue;

                    // Filter out subset/tournament games by title pattern
                    if (!string.IsNullOrWhiteSpace(item.Title))
                    {
                        var titleLower = item.Title.ToLowerInvariant();
                        // Skip subsets, tournaments, events, bonus sets, and hubs
                        if (titleLower.Contains("[subset") ||
                            titleLower.Contains("[tournament") ||
                            titleLower.Contains("[event") ||
                            titleLower.Contains("[bonus") ||
                            titleLower.Contains("[hub") ||
                            titleLower.Contains("[specialty") ||
                            titleLower.Contains("[exclusive") ||
                            titleLower.Contains("(subset"))
                        {
                            _logger?.Debug($"[RA] Skipping subset/tournament game: {item.Title} (ID={gameId})");
                            continue;
                        }
                    }

                    foreach (var hash in EnumerateHashes(item.Hashes))
                    {
                        if (string.IsNullOrWhiteSpace(hash)) continue;
                        var key = hash.Trim().ToLowerInvariant();
                        if (key.Length == 0) continue;
                        index[key] = gameId;
                    }
                }

                if (items.Count < PageSize)
                {
                    break;
                }

                offset += PageSize;
            }

            _logger?.Info($"[RA] Hash index for consoleId={consoleId} built with {index.Count} hashes.");

            return new RaHashIndexCacheFile
            {
                UpdatedUtc = DateTime.UtcNow,
                HashToGameId = index
            };
        }

        private List<RaGameListItem> EnumerateGameListItems(string json)
        {
            List<RaGameListItem> items = null;

            // Try to deserialize as array first
            try
            {
                var arrayItems = JsonConvert.DeserializeObject<RaGameListItem[]>(json, _jsonSettings);
                if (arrayItems != null && arrayItems.Length > 0)
                {
                    items = new List<RaGameListItem>(arrayItems);
                }
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, "[RA] Failed to deserialize API response as array.");
            }

            // Try to deserialize as response object
            if (items == null)
            {
                try
                {
                    var response = JsonConvert.DeserializeObject<RaGameListResponse>(json, _jsonSettings);
                    if (response?.Results != null && response.Results.Count > 0)
                    {
                        items = response.Results;
                    }
                    else if (response?.GameList != null && response.GameList.Count > 0)
                    {
                        items = response.GameList;
                    }
                }
                catch (Exception ex)
                {
                    _logger?.Warn(ex, "[RA] Failed to deserialize API response as object.");
                }
            }

            // Log JSON preview if parsing failed and JSON is relatively short
            if (items == null && json != null && json.Length < 1000)
            {
                _logger?.Debug($"[RA] Failed to parse API response. JSON preview: {json.Substring(0, Math.Min(500, json.Length))}");
            }

            return items ?? new List<RaGameListItem>();
        }

        private static IEnumerable<string> EnumerateHashes(object hashesElement)
        {
            if (hashesElement == null)
            {
                yield break;
            }

            // If it's a JToken (from Newtonsoft.Json when deserializing to object)
            if (hashesElement is JToken jToken)
            {
                if (jToken is JArray jArray)
                {
                    foreach (var item in jArray)
                    {
                        if (item is JValue jValue && jValue.Type == JTokenType.String)
                        {
                            var hash = jValue.ToString();
                            if (!string.IsNullOrWhiteSpace(hash))
                            {
                                yield return hash;
                            }
                        }
                    }
                }
                else if (jToken is JValue jValue && jValue.Type == JTokenType.String)
                {
                    var elementStringValue = jValue.ToString();
                    if (!string.IsNullOrWhiteSpace(elementStringValue))
                    {
                        foreach (var part in elementStringValue.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                        {
                            yield return part.Trim();
                        }
                    }
                }
                yield break;
            }

            // If it's already a string array
            if (hashesElement is string[] stringArray)
            {
                foreach (var h in stringArray)
                {
                    if (!string.IsNullOrWhiteSpace(h))
                    {
                        yield return h;
                    }
                }
                yield break;
            }

            // If it's an object array (from DataContractJsonSerializer)
            if (hashesElement is object[] objectArray)
            {
                foreach (var obj in objectArray)
                {
                    if (obj != null && !string.IsNullOrWhiteSpace(obj.ToString()))
                    {
                        yield return obj.ToString();
                    }
                }
                yield break;
            }

            // If it's a single string
            if (hashesElement is string hashString)
            {
                if (string.IsNullOrWhiteSpace(hashString)) yield break;

                foreach (var part in hashString.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    yield return part.Trim();
                }
            }
        }
    }
}
