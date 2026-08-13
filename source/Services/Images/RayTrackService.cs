using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PlayniteAchievements.Common;
using PlayniteAchievements.Services.Logging;
using Playnite.SDK;

namespace PlayniteAchievements.Services.Images
{
    /// <summary>
    /// Cache of <see cref="RayTrack"/>s keyed by image uri, so the silhouette behind an icon is traced
    /// once and shared by every surface showing it.
    ///
    /// Sized above the bitmap cache on purpose. A track is a few kilobytes and the decode it saves you
    /// from repeating is a millisecond or so, so keeping a track alive after its source bitmap has been
    /// evicted is exactly the trade worth making.
    /// </summary>
    public sealed class RayTrackService : IDisposable
    {
        private static readonly ILogger StaticLogger = PluginLogger.GetLogger(nameof(RayTrackService));

        /// <summary>
        /// Decode size for analysis, independent of whatever the display asks for. The loop is smoothed
        /// and then resampled, so precision finer than this is discarded downstream; 64 also matches the
        /// bitmap cache's own default size, so a caller asking for the default shares the same decode.
        /// </summary>
        private const int AnalysisDecodePixel = 64;

        private const string GrayPrefix = "gray:";
        private const string PreviewHttpPrefix = "previewhttp:";

        private readonly ILogger _logger;
        private readonly MemoryImageService _images;
        private readonly int _maxItems;

        private readonly object _cacheLock = new object();
        private readonly LinkedList<string> _lru = new LinkedList<string>();

        private readonly Dictionary<string, CacheEntry> _cache =
            new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);

        private readonly ConcurrentDictionary<string, Task<RayTrack>> _inflight =
            new ConcurrentDictionary<string, Task<RayTrack>>(StringComparer.OrdinalIgnoreCase);

        private sealed class CacheEntry
        {
            public RayTrack Value { get; set; }
            public LinkedListNode<string> Node { get; set; }
        }

        public RayTrackService(ILogger logger, MemoryImageService images, int maxItems = 1024)
        {
            _logger = logger ?? StaticLogger;
            _images = images ?? throw new ArgumentNullException(nameof(images));
            _maxItems = Math.Max(128, maxItems);

            // Follow the bitmap cache's own invalidation rather than being called by name from inside
            // it: a re-downloaded icon has a new silhouette, and a full reset means everything is stale.
            _images.UriSegmentEvicted += OnUriSegmentEvicted;
            _images.CacheCleared += OnCacheCleared;
        }

        public void Dispose()
        {
            _images.UriSegmentEvicted -= OnUriSegmentEvicted;
            _images.CacheCleared -= OnCacheCleared;
            Clear();
        }

        /// <summary>
        /// Cache-only lookup. Surfaces that render synchronously into a bitmap have no seam to await on,
        /// and every other caller uses it to skip a frame of fallback when the track is already known.
        /// </summary>
        public bool TryGet(string uri, out RayTrack track)
        {
            track = null;
            var key = NormalizeKey(uri);
            if (key == null)
            {
                return false;
            }

            return TryGetCached(key, out track);
        }

        public Task<RayTrack> GetAsync(string uri, CancellationToken cancel)
        {
            var key = NormalizeKey(uri);
            if (key == null)
            {
                return Task.FromResult(RayTrack.Empty);
            }

            if (TryGetCached(key, out var cached))
            {
                return Task.FromResult(cached);
            }

            var inflight = _inflight.GetOrAdd(key, k => BuildAsync(k));
            return inflight.WithCancellation(cancel);
        }

        public void Clear()
        {
            lock (_cacheLock)
            {
                _cache.Clear();
                _lru.Clear();
            }
        }

        private async Task<RayTrack> BuildAsync(string key)
        {
            try
            {
                using (PerfScope.Start(_logger, "raytrack.build", 20, key))
                {
                    // GIFs are animated by extension alone, and a fixed loop traced from one frame of a
                    // moving silhouette would be wrong however cheaply it was obtained.
                    if (ImageFormats.IsAnimatedFile(key))
                    {
                        var animated = RayTrack.RoundedRect(1.0, 0.0);
                        AddToCache(key, animated);
                        return animated;
                    }

                    // CancellationToken.None deliberately: the outer WithCancellation already lets each
                    // caller walk away, and one control unloading must not cancel work others are
                    // waiting on. cacheResult false keeps these analysis-sized bitmaps out of the
                    // display LRU, where they would evict icons that are actually on screen.
                    var bitmap = await _images
                        .GetAsync(key, AnalysisDecodePixel, CancellationToken.None, cacheResult: false)
                        .ConfigureAwait(false);

                    var track = await Task.Run(() => RayTrackBuilder.Build(bitmap)).ConfigureAwait(false)
                                ?? RayTrack.RoundedRect(1.0, 0.0);

                    AddToCache(key, track);
                    return track;
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"Ray track build failed for '{key}'.");
                return RayTrack.RoundedRect(1.0, 0.0);
            }
            finally
            {
                _inflight.TryRemove(key, out _);
            }
        }

        /// <summary>
        /// Trims the prefixes that do not change a silhouette and keeps the one that does.
        ///
        /// The grayscale prefix goes: the grayscale conversion rewrites only the color bytes and leaves
        /// alpha untouched, so a locked icon and its unlocked original share one track. The cache-bust
        /// token stays, because that token IS the file's content identity — an overwritten icon arrives
        /// under a new key and rebuilds without needing to be evicted.
        /// </summary>
        private static string NormalizeKey(string uri)
        {
            var value = (uri ?? string.Empty).Trim();
            if (value.Length == 0)
            {
                return null;
            }

            while (value.StartsWith(GrayPrefix, StringComparison.OrdinalIgnoreCase))
            {
                value = value.Substring(GrayPrefix.Length);
            }

            if (value.StartsWith(PreviewHttpPrefix, StringComparison.OrdinalIgnoreCase))
            {
                value = value.Substring(PreviewHttpPrefix.Length);
            }

            return value.Length == 0 ? null : value;
        }

        private bool TryGetCached(string key, out RayTrack value)
        {
            lock (_cacheLock)
            {
                if (_cache.TryGetValue(key, out var entry) && entry?.Value != null)
                {
                    if (entry.Node != null)
                    {
                        _lru.Remove(entry.Node);
                        _lru.AddFirst(entry.Node);
                    }

                    value = entry.Value;
                    return true;
                }
            }

            value = null;
            return false;
        }

        private void AddToCache(string key, RayTrack value)
        {
            if (value == null)
            {
                return;
            }

            lock (_cacheLock)
            {
                if (_cache.TryGetValue(key, out var existing))
                {
                    existing.Value = value;
                    if (existing.Node != null)
                    {
                        _lru.Remove(existing.Node);
                        _lru.AddFirst(existing.Node);
                    }
                }
                else
                {
                    var node = new LinkedListNode<string>(key);
                    _lru.AddFirst(node);
                    _cache[key] = new CacheEntry { Value = value, Node = node };
                }

                while (_cache.Count > _maxItems && _lru.Last != null)
                {
                    var toEvict = _lru.Last.Value;
                    _lru.RemoveLast();
                    _cache.Remove(toEvict);
                }
            }
        }

        private void OnCacheCleared()
        {
            Clear();
        }

        private void OnUriSegmentEvicted(string segment)
        {
            if (string.IsNullOrWhiteSpace(segment))
            {
                return;
            }

            lock (_cacheLock)
            {
                List<string> keysToEvict = null;
                foreach (var key in _cache.Keys)
                {
                    if (key.IndexOf(segment, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        (keysToEvict ?? (keysToEvict = new List<string>())).Add(key);
                    }
                }

                if (keysToEvict == null)
                {
                    return;
                }

                foreach (var key in keysToEvict)
                {
                    if (_cache.TryGetValue(key, out var entry))
                    {
                        if (entry?.Node != null)
                        {
                            _lru.Remove(entry.Node);
                        }

                        _cache.Remove(key);
                    }
                }
            }
        }
    }
}
