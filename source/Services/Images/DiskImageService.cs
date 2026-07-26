using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using PlayniteAchievements.Services.Logging;
using Playnite.SDK;

namespace PlayniteAchievements.Services.Images
{
    public enum IconCacheClearScope
    {
        All = 0,
        LockedOnly = 1
    }

    /// <summary>
    /// Persistent disk-based icon cache with URI-based organization.
    /// Downloads icons, converts to PNG, and stores on disk for fast retrieval.
    /// </summary>
    public sealed class DiskImageService : IDisposable
    {
        private static readonly ILogger StaticLogger = PluginLogger.GetLogger(nameof(DiskImageService));
        private const int MaxBytes = 5 * 1024 * 1024; // 5MB limit

        // Domains known to rate-limit concurrent requests
        private static readonly string[] RateLimitedDomains = new[]
        {
            "image-ssl.xboxlive.com",
            "xboxlive.com",
            "images-eds-ssl.xboxlive.com"
        };

        private static readonly string[] SupportedImageExtensions =
        {
            ".png",
            ".jpg",
            ".jpeg",
            ".gif",
            ".bmp",
            ".tif",
            ".tiff",
            ".webp"
        };

        private readonly ILogger _logger;
        private readonly HttpClientHandler _httpHandler;
        private readonly HttpClient _http;
        private readonly string _cacheRoot;
        private readonly SemaphoreSlim _downloadGate;
        private readonly SemaphoreSlim _rateLimitedDownloadGate;
        private readonly object _pathWriteLocksSync = new object();
        private readonly Dictionary<string, PathWriteLockEntry> _pathWriteLocks =
            new Dictionary<string, PathWriteLockEntry>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Raised with the absolute target path whenever an existing cached image file is
        /// rewritten in place (force icon refresh, changed friend avatars, replaced custom
        /// icons). MemoryImageService listens to evict the matching decoded bitmaps; without
        /// this, its path-keyed entries would keep serving the old pixels.
        /// </summary>
        public event Action<string> ImageFileOverwritten;

        private void NotifyImageFileOverwritten(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            try { ImageFileOverwritten?.Invoke(path); } catch { }
        }

        // Per-game snapshot of the category_defaults folder contents, so whole-library snapshot
        // builds resolve default category art with one directory scan per game instead of
        // per-achievement File.Exists probes. Entries are immutable once published; writes and
        // deletes under the folder drop the entry so the next probe rescans.
        private readonly ConcurrentDictionary<string, Lazy<HashSet<string>>> _defaultCategoryArtSnapshots =
            new ConcurrentDictionary<string, Lazy<HashSet<string>>>(StringComparer.OrdinalIgnoreCase);

        private sealed class PathWriteLockEntry
        {
            public readonly SemaphoreSlim Semaphore = new SemaphoreSlim(1, 1);
            public int ReferenceCount;
        }

        private sealed class PathWriteLockLease : IDisposable
        {
            private DiskImageService _owner;
            private readonly string _key;
            private readonly PathWriteLockEntry _entry;
            private bool _disposed;

            public PathWriteLockLease(DiskImageService owner, string key, PathWriteLockEntry entry)
            {
                _owner = owner;
                _key = key;
                _entry = entry;
            }

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _owner?.ReleasePathWriteLock(_key, _entry);
                _owner = null;
            }
        }

#if TEST
        internal int PathWriteLockCountForTests
        {
            get
            {
                lock (_pathWriteLocksSync)
                {
                    return _pathWriteLocks.Count;
                }
            }
        }
#endif

        public DiskImageService(ILogger logger, string cacheRoot, int downloadConcurrency = 8)
        {
            _logger = logger ?? StaticLogger;
            _cacheRoot = cacheRoot ?? throw new ArgumentNullException(nameof(cacheRoot));
            _downloadGate = new SemaphoreSlim(Math.Max(1, downloadConcurrency), Math.Max(1, downloadConcurrency));
            _rateLimitedDownloadGate = new SemaphoreSlim(8, 8); // Xbox CDN concurrency (increased for EDS with w=128 param)

            // Increase HTTP connection limit for parallel downloads (.NET Framework approach)
            ServicePointManager.DefaultConnectionLimit = Math.Max(16, downloadConcurrency);

            _httpHandler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                AllowAutoRedirect = true,
                UseCookies = false
            };
            _http = new HttpClient(_httpHandler)
            {
                Timeout = TimeSpan.FromSeconds(30)
            };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("PlayniteAchievements/1.0");

            EnsureIconCacheDirectory();
        }

        public void Dispose()
        {
            try { _http?.Dispose(); } catch { }
            try { _httpHandler?.Dispose(); } catch { }
            try { _downloadGate?.Dispose(); } catch { }
            try { _rateLimitedDownloadGate?.Dispose(); } catch { }
            try
            {
                List<PathWriteLockEntry> entries;
                lock (_pathWriteLocksSync)
                {
                    entries = _pathWriteLocks.Values.ToList();
                    _pathWriteLocks.Clear();
                }

                foreach (var entry in entries)
                {
                    try { entry?.Semaphore?.Dispose(); } catch { }
                }
            } catch { }
        }

        private async Task<PathWriteLockLease> AcquirePathWriteLockAsync(string targetPath, CancellationToken cancel)
        {
            var key = NormalizePathLockKey(targetPath);
            PathWriteLockEntry entry;
            lock (_pathWriteLocksSync)
            {
                if (!_pathWriteLocks.TryGetValue(key, out entry))
                {
                    entry = new PathWriteLockEntry();
                    _pathWriteLocks[key] = entry;
                }

                entry.ReferenceCount++;
            }

            try
            {
                await entry.Semaphore.WaitAsync(cancel).ConfigureAwait(false);
                return new PathWriteLockLease(this, key, entry);
            }
            catch
            {
                ReleasePathWriteLock(key, entry, releaseSemaphore: false);
                throw;
            }
        }

        private void ReleasePathWriteLock(
            string key,
            PathWriteLockEntry entry,
            bool releaseSemaphore = true)
        {
            if (entry == null)
            {
                return;
            }

            if (releaseSemaphore)
            {
                entry.Semaphore.Release();
            }

            var disposeEntry = false;
            lock (_pathWriteLocksSync)
            {
                if (entry.ReferenceCount > 0)
                {
                    entry.ReferenceCount--;
                }

                if (entry.ReferenceCount == 0 &&
                    _pathWriteLocks.TryGetValue(key, out var currentEntry) &&
                    ReferenceEquals(currentEntry, entry))
                {
                    _pathWriteLocks.Remove(key);
                    disposeEntry = true;
                }
            }

            if (disposeEntry)
            {
                try { entry.Semaphore.Dispose(); } catch { }
            }
        }

        private static string NormalizePathLockKey(string targetPath)
        {
            var normalized = (targetPath ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return string.Empty;
            }

            try
            {
                return Path.GetFullPath(normalized);
            }
            catch
            {
                return normalized;
            }
        }

        private string IconCacheDirectory => Path.Combine(_cacheRoot, "icon_cache");

        private void EnsureIconCacheDirectory()
        {
            try
            {
                if (!Directory.Exists(IconCacheDirectory))
                {
                    Directory.CreateDirectory(IconCacheDirectory);
                    _logger?.Info($"Created icon cache directory: {IconCacheDirectory}");
                }
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed to create icon cache directory: {IconCacheDirectory}");
            }
        }

        /// <summary>
        /// Legacy helper: generate a cache filename from a URI using a SHA256 hash.
        /// New achievement icon writes should use API-name paths instead.
        /// This is retained for display-time hash-cache lookups (MemoryImageService, GifAnimationHelper).
        /// </summary>
        public string GetIconCachePathFromUri(string uri, int decodeSize, string gameId = null)
        {
            var useDecodeSizeSuffix = decodeSize > 0;
            var hashHex = ComputeUriHashPrefix(uri);

            // Use per-game subfolder if gameId is provided
            var cacheDir = string.IsNullOrEmpty(gameId)
                ? IconCacheDirectory
                : Path.Combine(IconCacheDirectory, gameId);

            var sizeSuffix = useDecodeSizeSuffix ? $"_{decodeSize}" : string.Empty;
            var extension = ResolvePreferredExtensionForSource(uri, decodeSize);
            return Path.Combine(cacheDir, $"{hashHex}{sizeSuffix}{extension}");
        }

        // The 16-char hash prefix that identifies all cached variants of a source URI.
        private static string ComputeUriHashPrefix(string uri)
        {
            using (var sha = SHA256.Create())
            {
                var hashBytes = sha.ComputeHash(Encoding.UTF8.GetBytes(uri));
                return BitConverter.ToString(hashBytes).Replace("-", "").Substring(0, 16);
            }
        }

        // Resolves an icon_cache-rooted relative path (e.g. "icon_cache/friendgames/...") to an
        // absolute path under the cache root, for callers that build their own stable target paths.
        internal string ResolveCacheRelativePath(string relativePath)
        {
            return string.IsNullOrWhiteSpace(relativePath)
                ? null
                : Path.Combine(_cacheRoot, relativePath);
        }

        internal string GetAchievementIconCachePath(
            string gameId,
            string fileStem,
            AchievementIconVariant variant)
        {
            var relativePath = AchievementIconCachePathBuilder.BuildRelativePath(
                gameId,
                fileStem,
                variant);
            return Path.Combine(_cacheRoot, relativePath);
        }

        // Probes the default cached achievement icon across supported extensions. Downloads with
        // decodeSize 0 preserve the source format, so the stored extension follows the source
        // rather than the canonical extension of the relative path.
        internal string FindExistingAchievementIconCachePath(
            string gameId,
            string fileStem,
            AchievementIconVariant variant)
        {
            var canonicalPath = GetAchievementIconCachePath(gameId, fileStem, variant);
            if (string.IsNullOrWhiteSpace(canonicalPath))
            {
                return null;
            }

            if (File.Exists(canonicalPath))
            {
                return canonicalPath;
            }

            foreach (var extension in SupportedImageExtensions)
            {
                var candidate = Path.ChangeExtension(canonicalPath, extension);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }

        // Path of the retired compressed 128px cache mode. Read-only fallback for games not yet
        // refreshed since the mode was removed; the folder is deleted after each game's refresh.
        internal string GetLegacyCompressedAchievementIconCachePath(
            string gameId,
            string fileStem,
            AchievementIconVariant variant)
        {
            var relativePath = AchievementIconCachePathBuilder.BuildLegacyCompressedRelativePath(
                gameId,
                fileStem,
                variant);
            return Path.Combine(_cacheRoot, relativePath);
        }

        // Absolute path for the provider-supplied default category art file. Deterministic per
        // (gameId, normalized category label); see AchievementIconCachePathBuilder for the layout.
        internal string GetDefaultCategoryImagePath(
            string gameId,
            string categoryLabel)
        {
            var relativePath = AchievementIconCachePathBuilder.BuildDefaultCategoryRelativePath(
                gameId,
                categoryLabel);
            return Path.Combine(_cacheRoot, relativePath);
        }

        // Probes the default category art across supported extensions. Downloads with
        // decodeSize 0 preserve the source format, so the stored extension follows the
        // source (e.g. RetroAchievements badges are .png) rather than the canonical .jpg
        // of the relative path.
        internal string FindExistingDefaultCategoryImagePath(
            string gameId,
            string categoryLabel)
        {
            var canonicalPath = GetDefaultCategoryImagePath(gameId, categoryLabel);
            if (string.IsNullOrWhiteSpace(canonicalPath))
            {
                return null;
            }

            var snapshot = GetDefaultCategoryArtSnapshot(gameId);
            if (snapshot == null)
            {
                return ProbeDefaultCategoryImagePath(canonicalPath);
            }

            if (snapshot.Count == 0)
            {
                return null;
            }

            if (snapshot.Contains(Path.GetFileName(canonicalPath)))
            {
                return canonicalPath;
            }

            foreach (var extension in SupportedImageExtensions)
            {
                var candidate = Path.ChangeExtension(canonicalPath, extension);
                if (snapshot.Contains(Path.GetFileName(candidate)))
                {
                    return candidate;
                }
            }

            return null;
        }

        // Direct disk fallback for a single call when the directory snapshot could not be built.
        private static string ProbeDefaultCategoryImagePath(string canonicalPath)
        {
            if (File.Exists(canonicalPath))
            {
                return canonicalPath;
            }

            foreach (var extension in SupportedImageExtensions)
            {
                var candidate = Path.ChangeExtension(canonicalPath, extension);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }

        private HashSet<string> GetDefaultCategoryArtSnapshot(string gameId)
        {
            var key = NormalizeSnapshotGameKey(gameId);
            var lazy = _defaultCategoryArtSnapshots.GetOrAdd(
                key,
                k => new Lazy<HashSet<string>>(
                    () => ScanDefaultCategoryArtDirectory(k),
                    LazyThreadSafetyMode.ExecutionAndPublication));

            var snapshot = lazy.Value;
            if (snapshot == null)
            {
                // Scan failed; drop the entry so a later probe retries instead of caching the failure.
                _defaultCategoryArtSnapshots.TryRemove(key, out _);
            }

            return snapshot;
        }

        private HashSet<string> ScanDefaultCategoryArtDirectory(string gameId)
        {
            try
            {
                var directory = Path.Combine(
                    IconCacheDirectory,
                    gameId,
                    AchievementIconCachePathBuilder.DefaultCategoryFolderName);
                if (!Directory.Exists(directory))
                {
                    return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                }

                return new HashSet<string>(
                    Directory.EnumerateFiles(directory).Select(Path.GetFileName),
                    StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                return null;
            }
        }

        // Mirrors the empty-gameId fallback in AchievementIconCachePathBuilder so the snapshot key
        // always matches the directory the path builder writes into.
        private static string NormalizeSnapshotGameKey(string gameId)
        {
            return string.IsNullOrWhiteSpace(gameId) ? Guid.Empty.ToString("D") : gameId.Trim();
        }

        internal void InvalidateDefaultCategoryArtSnapshot(string gameId)
        {
            _defaultCategoryArtSnapshots.TryRemove(NormalizeSnapshotGameKey(gameId), out _);
        }

        private void InvalidateDefaultCategoryArtSnapshotForTargetPath(string targetPath)
        {
            if (string.IsNullOrWhiteSpace(targetPath))
            {
                return;
            }

            try
            {
                var parent = Path.GetDirectoryName(targetPath);
                if (string.IsNullOrEmpty(parent) ||
                    !string.Equals(
                        Path.GetFileName(parent),
                        AchievementIconCachePathBuilder.DefaultCategoryFolderName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                var gameDir = Path.GetDirectoryName(parent);
                var gameId = string.IsNullOrEmpty(gameDir) ? null : Path.GetFileName(gameDir);
                if (!string.IsNullOrWhiteSpace(gameId))
                {
                    InvalidateDefaultCategoryArtSnapshot(gameId);
                }
            }
            catch
            {
            }
        }

        private void ClearDefaultCategoryArtSnapshots()
        {
            _defaultCategoryArtSnapshots.Clear();
        }

        /// <summary>
        /// Check if an icon is cached on disk by URI.
        /// </summary>
        public bool IsIconCached(string uri, int decodeSize, string gameId = null)
        {
            var cachePath = GetIconCachePathFromUri(uri, decodeSize, gameId);
            return File.Exists(cachePath);
        }

        /// <summary>
        /// Determines if a URL belongs to a domain known to rate-limit concurrent requests.
        /// </summary>
        private static bool IsRateLimitedDomain(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return false;
            }

            try
            {
                var host = new Uri(url).Host;
                foreach (var domain in RateLimitedDomains)
                {
                    if (host.EndsWith(domain, StringComparison.OrdinalIgnoreCase) ||
                        host.Equals(domain, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
            catch
            {
                // Invalid URL, treat as normal
            }

            return false;
        }


        /// <summary>
        /// Get or download an icon by URI, caching to disk by URI hash.
        /// If gameId is provided, stores in per-game subfolder.
        /// Returns the file path to the cached PNG file, or null on failure.
        /// </summary>
        public async Task<string> GetOrDownloadIconAsync(
            string uri,
            int decodeSize,
            CancellationToken cancel,
            string gameId = null)
        {
            if (string.IsNullOrWhiteSpace(uri))
            {
                return null;
            }

            var cachePath = GetIconCachePathFromUri(uri, decodeSize, gameId);
            return await GetOrDownloadIconToPathAsync(uri, cachePath, decodeSize, cancel).ConfigureAwait(false);
        }

        public async Task<string> GetOrDownloadIconToPathAsync(
            string uri,
            string targetPath,
            int decodeSize,
            CancellationToken cancel,
            bool overwriteExistingTarget = false)
        {
            if (string.IsNullOrWhiteSpace(uri) || string.IsNullOrWhiteSpace(targetPath))
            {
                return null;
            }

            var preserveOriginalFormat = ShouldPreserveOriginalFormat(decodeSize);
            var resolvedTargetPath = ResolveTargetPathForSource(targetPath, uri, decodeSize);
            EnsureTargetDirectory(resolvedTargetPath);

            var pathLock = await AcquirePathWriteLockAsync(targetPath, cancel).ConfigureAwait(false);
            using (pathLock)
            {
                try
                {
                    if (!overwriteExistingTarget && File.Exists(resolvedTargetPath))
                    {
                        return resolvedTargetPath;
                    }

                    if (!overwriteExistingTarget &&
                        string.Equals(resolvedTargetPath, targetPath, StringComparison.OrdinalIgnoreCase) &&
                        File.Exists(targetPath))
                    {
                        return targetPath;
                    }

                    var overwritingExisting = overwriteExistingTarget && File.Exists(resolvedTargetPath);
                    var downloadGate = IsRateLimitedDomain(uri) ? _rateLimitedDownloadGate : _downloadGate;

                    byte[] bytes;
                    await downloadGate.WaitAsync(cancel).ConfigureAwait(false);
                    try
                    {
                        if (!overwriteExistingTarget && File.Exists(resolvedTargetPath))
                        {
                            return resolvedTargetPath;
                        }

                        bytes = await DownloadBytesAsync(uri, cancel).ConfigureAwait(false);
                    }
                    finally
                    {
                        downloadGate.Release();
                    }

                    if (bytes == null || bytes.Length == 0)
                    {
                        return null;
                    }

                    if (preserveOriginalFormat)
                    {
                        await SaveBytesWithRetryAsync(resolvedTargetPath, bytes, cancel).ConfigureAwait(false);
                        InvalidateDefaultCategoryArtSnapshotForTargetPath(resolvedTargetPath);
                        if (overwritingExisting)
                        {
                            NotifyImageFileOverwritten(resolvedTargetPath);
                        }

                        return resolvedTargetPath;
                    }

                    using (var ms = new MemoryStream(bytes, writable: false))
                    {
                        var savedPath = await SaveBitmapStreamToPathAsync(ms, resolvedTargetPath, decodeSize, cancel).ConfigureAwait(false);
                        if (savedPath != null)
                        {
                            InvalidateDefaultCategoryArtSnapshotForTargetPath(savedPath);
                            if (overwritingExisting)
                            {
                                NotifyImageFileOverwritten(savedPath);
                            }
                        }

                        return savedPath;
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger?.Warn(ex, $"Failed to download/cache icon from {uri}");
                    return null;
                }
            }
        }

        private static async Task SavePngWithRetryAsync(
            string cachePath,
            PngBitmapEncoder encoder,
            CancellationToken cancel,
            int maxAttempts = 3)
        {
            var attempt = 0;
            while (true)
            {
                cancel.ThrowIfCancellationRequested();
                attempt++;
                try
                {
                    using (var fs = new FileStream(cachePath, FileMode.Create, FileAccess.Write, FileShare.Read))
                    {
                        encoder.Save(fs);
                    }
                    return;
                }
                catch (IOException) when (attempt < maxAttempts)
                {
                    await Task.Delay(50 * attempt, cancel).ConfigureAwait(false);
                }
            }
        }

        private static async Task SaveBytesWithRetryAsync(
            string targetPath,
            byte[] bytes,
            CancellationToken cancel,
            int maxAttempts = 3)
        {
            var attempt = 0;
            while (true)
            {
                cancel.ThrowIfCancellationRequested();
                attempt++;
                try
                {
                    using (var fs = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.Read))
                    {
                        await fs.WriteAsync(bytes, 0, bytes.Length, cancel).ConfigureAwait(false);
                    }

                    return;
                }
                catch (IOException) when (attempt < maxAttempts)
                {
                    await Task.Delay(50 * attempt, cancel).ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// Crop a bitmap to square from center. If already square, returns original.
        /// For rectangular images, crops to square using center horizontally.
        /// </summary>
        private static BitmapSource CropToSquare(BitmapSource source)
        {
            if (source == null) return null;

            int width = source.PixelWidth;
            int height = source.PixelHeight;

            // Already square (or close enough)
            if (Math.Abs(width - height) <= 1) return source;

            // Calculate crop rect - center horizontally
            int cropSize = Math.Min(width, height);
            int x = 0;
            int y = 0;

            if (height < width)
            {
                // Landscape: crop sides
                x = (width - height) / 2;
                cropSize = height;
            }
            else
            {
                // Portrait: crop top/bottom (rare for achievement icons)
                y = (height - width) / 2;
                cropSize = width;
            }

            var rect = new Int32Rect(x, y, cropSize, cropSize);
            var cropped = new CroppedBitmap(source, rect);
            cropped.Freeze();
            return cropped;
        }

        // A 4xx client error means the resource will not appear on retry (missing image), except
        // 408 Request Timeout and 429 Too Many Requests which are worth retrying. 5xx and network
        // faults fall through to the transient retry path.
        private static bool IsPermanentImageHttpFailure(int statusCode)
        {
            if (statusCode < 400 || statusCode >= 500)
            {
                return false;
            }

            return statusCode != 408 && statusCode != 429;
        }

        private async Task<byte[]> DownloadBytesAsync(string url, CancellationToken cancel)
        {
            const int maxAttempts = 3;
            const double maxBackoffSeconds = 3.0;
            var backoff = TimeSpan.FromSeconds(1);

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                // Create a linked token with timeout to ensure we control disposal timing.
                // This prevents ObjectDisposedException when the parent CTS is disposed
                // while async SSL stream operations are still unwinding.
                using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancel))
                {
                    linkedCts.CancelAfter(TimeSpan.FromSeconds(30));
                    var token = linkedCts.Token;

                    try
                    {
                        using (var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false))
                        {
                            if (!resp.IsSuccessStatusCode)
                            {
                                // A permanent client error (e.g. 404 for a Steam app that has no
                                // library_600x900 vertical capsule, or an Exophase award whose art was
                                // never published) will not change on retry, so bail immediately instead
                                // of burning the retry budget and its backoff — that both spams the log
                                // and stalls the image phase (each game waits for its whole icon+cover
                                // group). The next refresh that still lacks the file re-attempts once,
                                // so images that appear later are picked up without any cached failure.
                                if (IsPermanentImageHttpFailure((int)resp.StatusCode))
                                {
                                    _logger?.Debug($"Image unavailable (HTTP {(int)resp.StatusCode}) for {url}; not retrying.");
                                    return null;
                                }

                                resp.EnsureSuccessStatusCode();
                            }

                            using (var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                            using (var ms = new MemoryStream())
                            {
                                var buffer = new byte[64 * 1024]; // 64KB buffer for faster downloads
                                int read;
                                int total = 0;

                                while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false)) > 0)
                                {
                                    total += read;
                                    if (total > MaxBytes)
                                    {
                                        throw new InvalidOperationException("Image download exceeded size limit.");
                                    }
                                    ms.Write(buffer, 0, read);
                                }

                                return ms.ToArray();
                            }
                        }
                    }
                    catch (OperationCanceledException) when (cancel.IsCancellationRequested)
                    {
                        // User cancelled - rethrow to propagate
                        throw;
                    }
                    catch (OperationCanceledException)
                    {
                        // Timeout - retry with exponential backoff
                        if (attempt < maxAttempts)
                        {
                            _logger?.Debug($"Download timeout for {url}, attempt {attempt}/{maxAttempts}, retrying in {backoff.TotalSeconds}s");
                            await Task.Delay(backoff, cancel).ConfigureAwait(false);
                            backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, maxBackoffSeconds));
                            continue;
                        }
                        return null;
                    }
                    catch (HttpRequestException ex)
                    {
                        // Transient HTTP errors - retry with exponential backoff
                        if (attempt < maxAttempts)
                        {
                            _logger?.Debug($"HTTP error for {url}, attempt {attempt}/{maxAttempts}: {ex.Message}, retrying in {backoff.TotalSeconds}s");
                            await Task.Delay(backoff, cancel).ConfigureAwait(false);
                            backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, maxBackoffSeconds));
                            continue;
                        }
                        return null;
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Load a BitmapSource from a cached file path.
        /// </summary>
        public BitmapSource LoadCachedImage(string cachePath, int decodePixel)
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                // IgnoreImageCache bypasses WPF's URI-keyed decode cache, which would
                // otherwise serve stale pixels for files overwritten at the same path.
                bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.IgnoreImageCache;
                if (decodePixel > 0)
                {
                    bitmap.DecodePixelWidth = decodePixel;
                }
                bitmap.UriSource = new Uri(cachePath, UriKind.RelativeOrAbsolute);
                bitmap.EndInit();
                return bitmap;
            }
            catch
            {
                return null;
            }
        }

        public void ClearAllCache()
        {
            try
            {
                ClearIconCache(IconCacheClearScope.All);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Failed to clear icon cache");
            }
        }

        public int ClearIconCache(
            IconCacheClearScope scope,
            IEnumerable<string> additionalPaths = null,
            Action<int, int> reportDeleteProgress = null)
        {
            try
            {
                if (scope == IconCacheClearScope.All)
                {
                    return ClearEntireIconCache(reportDeleteProgress);
                }

                return ClearScopedIconCache(scope, additionalPaths, reportDeleteProgress);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed to clear icon cache for scope '{scope}'.");
                return 0;
            }
            finally
            {
                ClearDefaultCategoryArtSnapshots();
            }
        }

        public void ClearGameCache(string gameId)
        {
            if (string.IsNullOrWhiteSpace(gameId))
            {
                return;
            }

            try
            {
                var gameDir = Path.Combine(IconCacheDirectory, gameId.Trim());
                if (!Directory.Exists(gameDir))
                {
                    return;
                }

                var filesToDelete = Directory
                    .EnumerateFiles(gameDir, "*", SearchOption.AllDirectories)
                    .Where(IsSupportedCacheImageFile)
                    .Where(file => !IsManagedCustomCacheFile(file))
                    .ToList();
                DeleteFiles(filesToDelete, reportDeleteProgress: null);

                foreach (var directory in Directory.EnumerateDirectories(gameDir, "*", SearchOption.TopDirectoryOnly))
                {
                    if (string.Equals(
                        Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                        Path.GetFullPath(Path.Combine(gameDir, AchievementIconCachePathBuilder.GetCustomFolder()))
                            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                        StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    Directory.Delete(directory, recursive: true);
                }

                DeleteEmptyDirectories(gameDir);
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"Failed to clear icon cache for game '{gameId}'.");
            }
            finally
            {
                InvalidateDefaultCategoryArtSnapshot(gameId);
            }
        }

        public void RemoveGameIconCache(string gameId)
        {
            ClearGameCache(gameId);
        }

        // Deletes a game's retired compressed 128px icon folder after a refresh has repopulated the
        // original-resolution cache. No-op when the folder is absent, which is the steady state.
        internal void DeleteLegacyCompressedGameIconFolder(string gameId)
        {
            if (string.IsNullOrWhiteSpace(gameId))
            {
                return;
            }

            try
            {
                var legacyDir = Path.Combine(
                    IconCacheDirectory,
                    gameId.Trim(),
                    AchievementIconCachePathBuilder.LegacyCompressedModeFolderName);
                if (!Directory.Exists(legacyDir))
                {
                    return;
                }

                Directory.Delete(legacyDir, recursive: true);
                InvalidateDefaultCategoryArtSnapshot(gameId);
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"Failed to delete legacy compressed icon cache for game '{gameId}'.");
            }
        }

        // Deletes a single icon_cache-rooted subdirectory (recursively) and prunes parent
        // directories left empty, up to but not including the icon_cache root. Guarded so only
        // paths resolving under icon_cache can be removed. Used to drop a specific game's cached
        // images (e.g. an orphaned friend/provider-only game folder).
        public void DeleteCacheRelativeDirectory(string relativeDirectory)
        {
            if (string.IsNullOrWhiteSpace(relativeDirectory))
            {
                return;
            }

            try
            {
                var iconCacheRoot = Path.GetFullPath(IconCacheDirectory);
                var target = Path.GetFullPath(Path.Combine(_cacheRoot, relativeDirectory));

                // Refuse to touch anything outside icon_cache, or icon_cache itself.
                if (!IsPathWithinDirectory(target, iconCacheRoot) || IsSameDirectory(target, iconCacheRoot))
                {
                    return;
                }

                if (Directory.Exists(target))
                {
                    Directory.Delete(target, recursive: true);
                }

                var parent = Path.GetDirectoryName(target);
                while (!string.IsNullOrEmpty(parent) &&
                       IsPathWithinDirectory(parent, iconCacheRoot) &&
                       !IsSameDirectory(parent, iconCacheRoot))
                {
                    if (!Directory.Exists(parent) ||
                        Directory.GetDirectories(parent).Length != 0 ||
                        Directory.GetFiles(parent).Length != 0)
                    {
                        break;
                    }

                    Directory.Delete(parent, recursive: false);
                    parent = Path.GetDirectoryName(parent);
                }
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"Failed to delete cache directory '{relativeDirectory}'.");
            }
            finally
            {
                ClearDefaultCategoryArtSnapshots();
            }
        }

        private static bool IsSameDirectory(string left, string right)
        {
            return string.Equals(
                (left ?? string.Empty).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                (right ?? string.Empty).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }

        private int ClearEntireIconCache(Action<int, int> reportDeleteProgress)
        {
            if (!Directory.Exists(IconCacheDirectory))
            {
                EnsureIconCacheDirectory();
                reportDeleteProgress?.Invoke(0, 0);
                return 0;
            }

            var filesToDelete = Directory
                .EnumerateFiles(IconCacheDirectory, "*", SearchOption.AllDirectories)
                .Where(IsSupportedCacheImageFile)
                .Where(file => !IsManagedCustomCacheFile(file))
                .Select(Path.GetFullPath)
                .ToList();
            var deletedCount = DeleteFiles(filesToDelete, reportDeleteProgress);

            DeleteEmptyDirectories(IconCacheDirectory);
            EnsureIconCacheDirectory();
            _logger?.Info($"Cleared all icon cache files. deletedCount={deletedCount}");
            return deletedCount;
        }

        private int ClearScopedIconCache(
            IconCacheClearScope scope,
            IEnumerable<string> additionalPaths,
            Action<int, int> reportDeleteProgress)
        {
            if (!Directory.Exists(IconCacheDirectory))
            {
                EnsureIconCacheDirectory();
                reportDeleteProgress?.Invoke(0, 0);
                return 0;
            }

            var cacheRoot = Path.GetFullPath(IconCacheDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var filesToDelete = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var file in Directory.EnumerateFiles(IconCacheDirectory, "*", SearchOption.AllDirectories))
            {
                if (!IsSupportedCacheImageFile(file))
                {
                    continue;
                }

                if (ShouldDeleteCacheFile(file, scope))
                {
                    filesToDelete.Add(Path.GetFullPath(file));
                }
            }

            if (additionalPaths != null)
            {
                foreach (var path in additionalPaths)
                {
                    if (string.IsNullOrWhiteSpace(path))
                    {
                        continue;
                    }

                    try
                    {
                        var normalized = Path.GetFullPath(path);
                        if (IsPathWithinDirectory(normalized, cacheRoot) && File.Exists(normalized))
                        {
                            filesToDelete.Add(normalized);
                        }
                    }
                    catch
                    {
                        // Ignore malformed paths from stale cache data.
                    }
                }
            }

            var deletedCount = DeleteFiles(filesToDelete, reportDeleteProgress);

            DeleteEmptyDirectories(cacheRoot);
            EnsureIconCacheDirectory();
            _logger?.Info($"Cleared icon cache scope '{scope}'. deletedCount={deletedCount}");
            return deletedCount;
        }

        private int DeleteFiles(IEnumerable<string> filesToDelete, Action<int, int> reportDeleteProgress)
        {
            var targets = (filesToDelete ?? Enumerable.Empty<string>())
                .Where(file => !string.IsNullOrWhiteSpace(file))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            reportDeleteProgress?.Invoke(0, targets.Count);

            var deletedCount = 0;
            for (var i = 0; i < targets.Count; i++)
            {
                var file = targets[i];
                try
                {
                    if (!File.Exists(file))
                    {
                        continue;
                    }

                    File.Delete(file);
                    deletedCount++;
                }
                catch (Exception ex)
                {
                    _logger?.Warn(ex, $"Failed to delete cached icon '{file}'.");
                }
                finally
                {
                    reportDeleteProgress?.Invoke(i + 1, targets.Count);
                }
            }

            return deletedCount;
        }

        private static int CountCachedImageFiles(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                return 0;
            }

            var count = 0;
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                if (!IsSupportedCacheImageFile(file))
                {
                    continue;
                }

                count++;
            }

            return count;
        }

        private static bool ShouldDeleteCacheFile(string path, IconCacheClearScope scope)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            if (IsManagedCustomCacheFile(path))
            {
                return false;
            }

            var fileName = Path.GetFileName(path) ?? string.Empty;

            switch (scope)
            {
                case IconCacheClearScope.LockedOnly:
                    return fileName.IndexOf(".locked.", StringComparison.OrdinalIgnoreCase) >= 0;
                default:
                    return true;
            }
        }

        private static bool IsPathWithinDirectory(string candidatePath, string directoryPath)
        {
            if (string.IsNullOrWhiteSpace(candidatePath) || string.IsNullOrWhiteSpace(directoryPath))
            {
                return false;
            }

            if (string.Equals(candidatePath, directoryPath, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var normalizedDirectory = directoryPath.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? directoryPath
                : directoryPath + Path.DirectorySeparatorChar;

            return candidatePath.StartsWith(normalizedDirectory, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsManagedCustomCacheFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            try
            {
                var directory = new DirectoryInfo(Path.GetDirectoryName(path) ?? string.Empty);
                while (directory != null)
                {
                    if (string.Equals(
                        directory.Name,
                        AchievementIconCachePathBuilder.GetCustomFolder(),
                        StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }

                    directory = directory.Parent;
                }
            }
            catch
            {
            }

            return false;
        }

        private static void DeleteEmptyDirectories(string rootDirectory)
        {
            if (string.IsNullOrWhiteSpace(rootDirectory) || !Directory.Exists(rootDirectory))
            {
                return;
            }

            foreach (var directory in Directory.GetDirectories(rootDirectory, "*", SearchOption.AllDirectories))
            {
                DeleteEmptyDirectories(directory);
            }

            if (Directory.GetDirectories(rootDirectory).Length == 0 &&
                Directory.GetFiles(rootDirectory).Length == 0)
            {
                Directory.Delete(rootDirectory, recursive: false);
            }
        }

        public long GetCacheSizeBytes()
        {
            try
            {
                if (!Directory.Exists(IconCacheDirectory))
                {
                    return 0;
                }

                long total = 0;
                foreach (var file in Directory.EnumerateFiles(IconCacheDirectory, "*", SearchOption.AllDirectories))
                {
                    if (!IsSupportedCacheImageFile(file))
                    {
                        continue;
                    }

                    total += new FileInfo(file).Length;
                }
                return total;
            }
            catch
            {
                return 0;
            }
        }

        public string GetCacheDirectoryPath() => IconCacheDirectory;

        /// <summary>
        /// Check if a path refers to an existing local file.
        /// </summary>
        public static bool IsLocalIconPath(string path) =>
            !string.IsNullOrWhiteSpace(path) && File.Exists(path);

        /// <summary>
        /// Whether a source can actually be cached: an http(s) URL or an existing local file.
        /// Single source of truth for "is there something here worth downloading/copying".
        /// </summary>
        public static bool IsCacheableImageSource(string path) =>
            !string.IsNullOrWhiteSpace(path) &&
            (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
             path.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
             IsLocalIconPath(path));

        /// <summary>
        /// Get or copy a local icon file to the cache.
        /// If gameId is provided, stores in per-game subfolder.
        /// Returns the cached file path, or null on failure.
        /// </summary>
        public async Task<string> GetOrCopyLocalIconAsync(
            string localPath,
            int decodeSize,
            CancellationToken cancel,
            string gameId = null)
        {
            if (string.IsNullOrWhiteSpace(localPath) || !File.Exists(localPath))
            {
                return null;
            }

            var cachePath = GetIconCachePathFromUri(localPath, decodeSize, gameId);
            return await GetOrCopyLocalIconToPathAsync(localPath, cachePath, decodeSize, cancel).ConfigureAwait(false);
        }

        public async Task<string> GetOrCopyLocalIconToPathAsync(
            string localPath,
            string targetPath,
            int decodeSize,
            CancellationToken cancel,
            bool overwriteExistingTarget = false)
        {
            if (string.IsNullOrWhiteSpace(localPath) ||
                !File.Exists(localPath) ||
                string.IsNullOrWhiteSpace(targetPath))
            {
                return null;
            }

            var preserveOriginalFormat = ShouldPreserveOriginalFormat(decodeSize);
            var resolvedTargetPath = ResolveTargetPathForSource(targetPath, localPath, decodeSize);
            EnsureTargetDirectory(resolvedTargetPath);

            var pathLock = await AcquirePathWriteLockAsync(targetPath, cancel).ConfigureAwait(false);
            using (pathLock)
            {
                try
                {
                    if (!overwriteExistingTarget && File.Exists(resolvedTargetPath))
                    {
                        return resolvedTargetPath;
                    }

                    if (!overwriteExistingTarget &&
                        string.Equals(resolvedTargetPath, targetPath, StringComparison.OrdinalIgnoreCase) &&
                        File.Exists(targetPath))
                    {
                        return targetPath;
                    }

                    cancel.ThrowIfCancellationRequested();

                    var overwritingExisting = overwriteExistingTarget && File.Exists(resolvedTargetPath);
                    if (preserveOriginalFormat)
                    {
                        File.Copy(localPath, resolvedTargetPath, overwrite: overwriteExistingTarget);
                        InvalidateDefaultCategoryArtSnapshotForTargetPath(resolvedTargetPath);
                        if (overwritingExisting)
                        {
                            NotifyImageFileOverwritten(resolvedTargetPath);
                        }

                        return resolvedTargetPath;
                    }

                    using (var ms = new MemoryStream(File.ReadAllBytes(localPath), writable: false))
                    {
                        var savedPath = await SaveBitmapStreamToPathAsync(ms, resolvedTargetPath, decodeSize, cancel).ConfigureAwait(false);
                        if (savedPath != null)
                        {
                            InvalidateDefaultCategoryArtSnapshotForTargetPath(savedPath);
                            if (overwritingExisting)
                            {
                                NotifyImageFileOverwritten(savedPath);
                            }
                        }

                        return savedPath;
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger?.Warn(ex, $"Failed to copy/cache local icon from {localPath}");
                    return null;
                }
            }
        }

        public async Task<string> CopyCachedIconAsync(
            string existingPath,
            string targetPath,
            CancellationToken cancel,
            bool overwriteExistingTarget = false)
        {
            if (string.IsNullOrWhiteSpace(existingPath) ||
                !File.Exists(existingPath) ||
                string.IsNullOrWhiteSpace(targetPath))
            {
                return null;
            }

            if (string.Equals(existingPath, targetPath, StringComparison.OrdinalIgnoreCase))
            {
                return existingPath;
            }

            EnsureTargetDirectory(targetPath);
            var pathLock = await AcquirePathWriteLockAsync(targetPath, cancel).ConfigureAwait(false);
            using (pathLock)
            {
                try
                {
                    if (!overwriteExistingTarget && File.Exists(targetPath))
                    {
                        return targetPath;
                    }

                    cancel.ThrowIfCancellationRequested();
                    var overwritingExisting = overwriteExistingTarget && File.Exists(targetPath);
                    File.Copy(existingPath, targetPath, overwrite: overwriteExistingTarget);
                    InvalidateDefaultCategoryArtSnapshotForTargetPath(targetPath);
                    if (overwritingExisting)
                    {
                        NotifyImageFileOverwritten(targetPath);
                    }

                    return targetPath;
                }
                catch (IOException)
                {
                    return File.Exists(targetPath) ? targetPath : null;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger?.Warn(ex, $"Failed to copy cached icon from {existingPath} to {targetPath}");
                    return null;
                }
            }
        }

        private async Task<string> SaveBitmapStreamToPathAsync(
            Stream imageStream,
            string targetPath,
            int decodeSize,
            CancellationToken cancel)
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            if (decodeSize > 0)
            {
                bitmap.DecodePixelWidth = decodeSize;
            }
            bitmap.StreamSource = imageStream;
            bitmap.EndInit();

            var finalBitmap = CropToSquare(bitmap);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(finalBitmap));
            await SavePngWithRetryAsync(targetPath, encoder, cancel).ConfigureAwait(false);
            return targetPath;
        }

        private static void EnsureTargetDirectory(string targetPath)
        {
            if (string.IsNullOrWhiteSpace(targetPath))
            {
                return;
            }

            var directory = Path.GetDirectoryName(targetPath);
            if (string.IsNullOrWhiteSpace(directory))
            {
                return;
            }

            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }

        private static bool ShouldPreserveOriginalFormat(int decodeSize)
        {
            return decodeSize <= 0;
        }

        private static string ResolvePreferredExtensionForSource(string source, int decodeSize)
        {
            if (!ShouldPreserveOriginalFormat(decodeSize))
            {
                return ".png";
            }

            var extension = GetNormalizedSourceExtension(source);
            return string.IsNullOrWhiteSpace(extension)
                ? ".png"
                : extension;
        }

        internal static string ResolveTargetPathForSource(string targetPath, string source, int decodeSize)
        {
            return ResolveTargetPathForSource(targetPath, source, ShouldPreserveOriginalFormat(decodeSize));
        }

        private static string ResolveTargetPathForSource(string targetPath, string source, bool preserveOriginalFormat)
        {
            if (!preserveOriginalFormat || string.IsNullOrWhiteSpace(targetPath))
            {
                return targetPath;
            }

            var sourceExtension = GetNormalizedSourceExtension(source);
            if (string.IsNullOrWhiteSpace(sourceExtension))
            {
                return targetPath;
            }

            return Path.ChangeExtension(targetPath, sourceExtension);
        }

        private static string GetNormalizedSourceExtension(string source)
        {
            if (string.IsNullOrWhiteSpace(source))
            {
                return null;
            }

            try
            {
                if (Uri.TryCreate(source, UriKind.Absolute, out var uri) &&
                    !string.IsNullOrWhiteSpace(uri.AbsolutePath))
                {
                    var uriExtension = Path.GetExtension(uri.AbsolutePath);
                    if (IsSupportedImageExtension(uriExtension))
                    {
                        return uriExtension.ToLowerInvariant();
                    }
                }
            }
            catch
            {
            }

            var extension = Path.GetExtension(source);
            return IsSupportedImageExtension(extension)
                ? extension.ToLowerInvariant()
                : null;
        }

        private static bool IsSupportedImageExtension(string extension)
        {
            if (string.IsNullOrWhiteSpace(extension))
            {
                return false;
            }

            return SupportedImageExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
        }

        private static bool IsSupportedCacheImageFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return false;
            }

            return IsSupportedImageExtension(Path.GetExtension(path));
        }

    }
}
