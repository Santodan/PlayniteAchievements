using Newtonsoft.Json;
using Playnite.SDK;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.Ffxiv
{
    /// <summary>
    /// Minimum interval between FFXIV Collect character requests, shared by every refresh path
    /// (manual, automatic, theme commands, other add-ons). FFXIV Collect refreshes a character
    /// from the Lodestone, which itself updates every few hours, so fetching more often returns
    /// the same data and only adds load on the site. The last response is kept on disk so a
    /// Playnite restart does not reset the interval.
    /// </summary>
    internal sealed class FfxivCharacterCache
    {
        /// <summary>
        /// The Lodestone updates a character roughly every 4-6 hours (per the FFXIV Collect
        /// developer), so this is the shortest interval that can return new data.
        /// </summary>
        internal static readonly TimeSpan MinFetchInterval = TimeSpan.FromHours(4);

        /// <summary>
        /// Shorter floor for a character FFXIV Collect has not indexed yet: the fix is the user
        /// adding it on the site, and the next refresh should see that without a 4-hour wait.
        /// </summary>
        internal static readonly TimeSpan NotIndexedRetryInterval = TimeSpan.FromMinutes(15);

        private readonly ILogger _logger;
        private readonly string _cacheFilePath;
        private readonly Func<DateTime> _utcNow;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

        private CacheEntry _memoryEntry;
        private bool _diskLoaded;

        public FfxivCharacterCache(ILogger logger, string pluginUserDataPath, Func<DateTime> utcNow = null)
        {
            _logger = logger;
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
            var dir = Path.Combine(pluginUserDataPath ?? string.Empty, "ffxiv");
            _cacheFilePath = Path.Combine(dir, "character.json");
        }

        /// <summary>
        /// Returns the character, calling <paramref name="fetch"/> only when the last response for
        /// this id is older than its interval. A cached not-indexed outcome is rethrown as
        /// <see cref="FfxivCharacterNotIndexedException"/>. Other failures are not cached.
        /// </summary>
        public async Task<FfxivCharacter> GetAsync(
            long lodestoneId,
            Func<CancellationToken, Task<FfxivCharacter>> fetch,
            CancellationToken cancel)
        {
            if (fetch == null) throw new ArgumentNullException(nameof(fetch));

            await _gate.WaitAsync(cancel).ConfigureAwait(false);
            try
            {
                var now = _utcNow();
                var entry = LoadEntry();
                if (IsFresh(entry, lodestoneId, now))
                {
                    _logger?.Debug(
                        $"[FFXIV] Character {lodestoneId} served from the last response " +
                        $"({(now - entry.FetchedUtc).TotalMinutes:0} min old, notIndexed={entry.NotIndexed}).");
                    if (entry.NotIndexed)
                    {
                        throw new FfxivCharacterNotIndexedException(lodestoneId);
                    }

                    return entry.Character;
                }

                FfxivCharacter character;
                try
                {
                    character = await fetch(cancel).ConfigureAwait(false);
                }
                catch (FfxivCharacterNotIndexedException)
                {
                    Store(new CacheEntry { LodestoneId = lodestoneId, FetchedUtc = now, NotIndexed = true });
                    throw;
                }

                Store(new CacheEntry { LodestoneId = lodestoneId, FetchedUtc = now, Character = character });
                return character;
            }
            finally
            {
                _gate.Release();
            }
        }

        private static bool IsFresh(CacheEntry entry, long lodestoneId, DateTime now)
        {
            if (entry == null || entry.LodestoneId != lodestoneId)
            {
                return false;
            }

            if (!entry.NotIndexed && entry.Character == null)
            {
                return false;
            }

            var age = now - entry.FetchedUtc;
            var interval = entry.NotIndexed ? NotIndexedRetryInterval : MinFetchInterval;

            // A negative age means the clock moved backwards; fetch rather than trust the stamp.
            return age >= TimeSpan.Zero && age < interval;
        }

        private CacheEntry LoadEntry()
        {
            if (_memoryEntry != null || _diskLoaded)
            {
                return _memoryEntry;
            }

            _diskLoaded = true;
            try
            {
                if (File.Exists(_cacheFilePath))
                {
                    _memoryEntry = JsonConvert.DeserializeObject<CacheEntry>(File.ReadAllText(_cacheFilePath));
                }
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, "[FFXIV] Failed to read character cache.");
                _memoryEntry = null;
            }

            return _memoryEntry;
        }

        private void Store(CacheEntry entry)
        {
            _memoryEntry = entry;
            _diskLoaded = true;

            try
            {
                var dir = Path.GetDirectoryName(_cacheFilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                File.WriteAllText(_cacheFilePath, JsonConvert.SerializeObject(entry));
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, "[FFXIV] Failed to write character cache.");
            }
        }

        private sealed class CacheEntry
        {
            public long LodestoneId { get; set; }
            public DateTime FetchedUtc { get; set; }
            public bool NotIndexed { get; set; }
            public FfxivCharacter Character { get; set; }
        }
    }
}
