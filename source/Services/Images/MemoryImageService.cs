using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using PlayniteAchievements.Services.Logging;
using Playnite.SDK;

namespace PlayniteAchievements.Services.Images
{
    /// <summary>
    /// Hybrid image loader with memory LRU cache and persistent disk backing.
    /// Memory cache provides fast access for recently used icons.
    /// Disk cache provides persistent storage across sessions.
    /// </summary>
    public sealed class MemoryImageService : IDisposable
    {
        private static readonly ILogger StaticLogger = PluginLogger.GetLogger(nameof(MemoryImageService));
        private const int DefaultDecodePixel = 64;
        private const int MinDecodePixel = 16;
        private const int MaxDecodePixel = 1024;
        private const string CacheBustPrefix = "cachebust|";
        private const string PreviewHttpPrefix = "previewhttp:";

        private readonly ILogger _logger;
        private readonly DiskImageService _diskService;

        private readonly int _maxItems;

        private readonly object _cacheLock = new object();
        private readonly LinkedList<string> _lru = new LinkedList<string>();
        private readonly Dictionary<string, CacheEntry> _cache =
            new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);

        private readonly ConcurrentDictionary<string, Task<BitmapSource>> _inflight =
            new ConcurrentDictionary<string, Task<BitmapSource>>(StringComparer.OrdinalIgnoreCase);

        private sealed class CacheEntry
        {
            public BitmapSource Value { get; set; }
            public LinkedListNode<string> Node { get; set; }
        }

        public MemoryImageService(
            ILogger logger,
            DiskImageService diskService,
            int maxItems = 512)
        {
            _logger = logger ?? StaticLogger;
            _diskService = diskService ?? throw new ArgumentNullException(nameof(diskService));
            _maxItems = Math.Max(64, maxItems);
        }

        public void Dispose()
        {
            // No unmanaged resources to dispose.
        }

        public void Clear()
        {
            lock (_cacheLock)
            {
                _cache.Clear();
                _lru.Clear();
            }
        }

        public void ClearDiskCache()
        {
            _diskService.ClearAllCache();
        }

        public int ClearDiskCache(
            IconCacheClearScope scope,
            IEnumerable<string> additionalPaths = null,
            Action<int, int> reportDeleteProgress = null)
        {
            return _diskService.ClearIconCache(scope, additionalPaths, reportDeleteProgress);
        }

        private const string GrayPrefix = "gray:";

        public Task<BitmapSource> GetAsync(string uri, int decodePixel, CancellationToken cancel)
        {
            var requestedUri = (uri ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(requestedUri))
            {
                return Task.FromResult<BitmapSource>(null);
            }

            var size = NormalizeDecodePixel(decodePixel);
            var key = $"{size}\u001f{requestedUri}";

            if (TryGetCached(key, out var cached))
            {
                return Task.FromResult(cached);
            }

            // Dedupe work per key, but allow UI-level cancellation (we just won't apply the result).
            var inflight = _inflight.GetOrAdd(key, _ => LoadAndCacheAsync(key, requestedUri, size));
            return inflight.WithCancellation(cancel);
        }

        private bool TryGetCached(string key, out BitmapSource value)
        {
            lock (_cacheLock)
            {
                if (_cache.TryGetValue(key, out var entry) && entry?.Value != null)
                {
                    // touch LRU
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

        private async Task<BitmapSource> LoadAndCacheAsync(string key, string requestedUri, int decodePixel)
        {
            try
            {
                var uri = requestedUri;
                TryStripCacheBust(ref uri);
                bool gray = TryStripGrayPrefix(ref uri);
                TryStripPreviewHttpPrefix(ref uri);
                if (string.IsNullOrWhiteSpace(uri))
                {
                    return null;
                }

                BitmapSource bmp;
                if (IsHttpUrl(uri))
                {
                    // Route preview HTTP values through the normal disk-cache path.
                    // This avoids creating thread-affined WPF image objects in ad-hoc direct-load paths.
                    bmp = await LoadFromDiskCacheAsync(uri, decodePixel).ConfigureAwait(false);
                }
                else
                {
                    bmp = LoadLocal(uri, decodePixel);
                }

                if (gray && bmp != null)
                {
                    bmp = ConvertToGrayscale(bmp);
                }

                if (bmp != null && bmp.CanFreeze)
                {
                    bmp.Freeze();
                }

                if (bmp != null)
                {
                    AddToCache(key, bmp);
                }

                return bmp;
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, "Image load failed.");
                return null;
            }
            finally
            {
                _inflight.TryRemove(key, out _);
            }
        }

        private static bool TryStripGrayPrefix(ref string uri)
        {
            if (string.IsNullOrWhiteSpace(uri))
            {
                return false;
            }

            bool gray = false;
            while (uri.StartsWith(GrayPrefix, StringComparison.OrdinalIgnoreCase))
            {
                uri = uri.Substring(GrayPrefix.Length);
                gray = true;
            }

            return gray;
        }

        private static bool TryStripPreviewHttpPrefix(ref string uri)
        {
            if (string.IsNullOrWhiteSpace(uri) ||
                !uri.StartsWith(PreviewHttpPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            uri = uri.Substring(PreviewHttpPrefix.Length);
            return true;
        }

        private static void TryStripCacheBust(ref string uri)
        {
            if (string.IsNullOrWhiteSpace(uri) ||
                !uri.StartsWith(CacheBustPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var firstSeparator = uri.IndexOf('|');
            if (firstSeparator < 0)
            {
                return;
            }

            var secondSeparator = uri.IndexOf('|', firstSeparator + 1);
            if (secondSeparator < 0 || secondSeparator + 1 >= uri.Length)
            {
                return;
            }

            uri = uri.Substring(secondSeparator + 1);
        }

        private static BitmapSource ConvertToGrayscale(BitmapSource source)
        {
            // More robust grayscale conversion that preserves alpha.
            // If conversion fails for any reason, fall back to the original image.
            try
            {
                if (source == null)
                {
                    return null;
                }

                BitmapSource bgraSource = source;
                if (bgraSource.Format != System.Windows.Media.PixelFormats.Bgra32)
                {
                    var converted = new FormatConvertedBitmap();
                    converted.BeginInit();
                    converted.Source = bgraSource;
                    converted.DestinationFormat = System.Windows.Media.PixelFormats.Bgra32;
                    converted.EndInit();
                    converted.Freeze();
                    bgraSource = converted;
                }

                int width = bgraSource.PixelWidth;
                int height = bgraSource.PixelHeight;
                int stride = width * 4;
                byte[] pixels = new byte[stride * height];
                bgraSource.CopyPixels(pixels, stride, 0);

                // BGRA byte order.
                for (int i = 0; i < pixels.Length; i += 4)
                {
                    byte b = pixels[i + 0];
                    byte g = pixels[i + 1];
                    byte r = pixels[i + 2];

                    // Standard luma approximation.
                    byte gray = (byte)Math.Min(255, (int)(0.114 * b + 0.587 * g + 0.299 * r));
                    pixels[i + 0] = gray;
                    pixels[i + 1] = gray;
                    pixels[i + 2] = gray;
                }

                var grayImage = BitmapSource.Create(
                    width,
                    height,
                    bgraSource.DpiX,
                    bgraSource.DpiY,
                    System.Windows.Media.PixelFormats.Bgra32,
                    null,
                    pixels,
                    stride);

                grayImage.Freeze();
                return grayImage;
            }
            catch
            {
                return source;
            }
        }

        private static int NormalizeDecodePixel(int decodePixel)
        {
            if (decodePixel <= 0)
            {
                return DefaultDecodePixel;
            }

            return Math.Max(MinDecodePixel, Math.Min(decodePixel, MaxDecodePixel));
        }

        private void AddToCache(string key, BitmapSource value)
        {
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

        private BitmapSource LoadLocal(string uri, int decodePixel)
        {
            try
            {
                var isGif = IsGifPathOrUri(uri);
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;

                if (!isGif && decodePixel > 0)
                {
                    bitmap.DecodePixelWidth = decodePixel;
                }

                bitmap.UriSource = new Uri(uri, UriKind.RelativeOrAbsolute);
                bitmap.EndInit();
                return bitmap;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Load an image from disk cache. If missing, attempt to download and cache it.
        /// </summary>
        private async Task<BitmapSource> LoadFromDiskCacheAsync(string uri, int decodePixel)
        {
            try
            {
                var decodeForCache = IsGifPathOrUri(uri) ? 0 : decodePixel;
                var cachePath = _diskService.GetIconCachePathFromUri(uri, decodeForCache, gameId: null);
                if (string.IsNullOrWhiteSpace(cachePath))
                {
                    return null;
                }

                if (!File.Exists(cachePath))
                {
                    cachePath = await _diskService
                        .GetOrDownloadIconAsync(uri, decodeForCache, CancellationToken.None, gameId: null)
                        .ConfigureAwait(false);
                }

                if (string.IsNullOrWhiteSpace(cachePath) || !File.Exists(cachePath))
                {
                    return null;
                }

                var isGif = IsGifPathOrUri(cachePath);

                return await Task.Run(() =>
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                    if (!isGif && decodePixel > 0)
                    {
                        bitmap.DecodePixelWidth = decodePixel;
                    }
                    bitmap.UriSource = new Uri(cachePath, UriKind.Absolute);
                    bitmap.EndInit();
                    return bitmap;
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"Failed to load from disk cache: {uri}");
                return null;
            }
        }

        private static bool IsHttpUrl(string url)
        {
            return url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                   url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsGifPathOrUri(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            try
            {
                if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
                {
                    return uri.AbsolutePath.EndsWith(".gif", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch
            {
            }

            return value.EndsWith(".gif", StringComparison.OrdinalIgnoreCase);
        }
    }

    internal static class TaskCancellationExtensions
    {
        public static async Task<T> WithCancellation<T>(this Task<T> task, CancellationToken cancel)
        {
            if (task == null)
            {
                return default;
            }

            if (!cancel.CanBeCanceled)
            {
                return await task.ConfigureAwait(false);
            }

            var tcs = new TaskCompletionSource<bool>();
            using (cancel.Register(state => ((TaskCompletionSource<bool>)state).TrySetResult(true), tcs))
            {
                if (task != await Task.WhenAny(task, tcs.Task).ConfigureAwait(false))
                {
                    throw new OperationCanceledException(cancel);
                }
            }

            return await task.ConfigureAwait(false);
        }
    }
}

