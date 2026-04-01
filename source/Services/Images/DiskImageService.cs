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
        CompressedOnly = 1,
        FullResolutionOnly = 2,
        LockedOnly = 3
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
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _pathWriteLocks =
            new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.OrdinalIgnoreCase);

        // Cache for computed icon cache paths to avoid repeated SHA256 computation
        private readonly ConcurrentDictionary<string, string> _iconPathCache =
            new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

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
                foreach (var kvp in _pathWriteLocks)
                {
                    try { kvp.Value?.Dispose(); } catch { }
                }
                _pathWriteLocks.Clear();
            } catch { }
            try { _iconPathCache.Clear(); } catch { }
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
        /// This is retained for display-time fallback and lazy legacy migration.
        /// </summary>
        public string GetIconCachePathFromUri(string uri, int decodeSize, string gameId = null)
        {
            var useDecodeSizeSuffix = decodeSize > 0;
            var cacheKey = useDecodeSizeSuffix
                ? (string.IsNullOrEmpty(gameId) ? $"{uri}|{decodeSize}" : $"{uri}|{decodeSize}|{gameId}")
                : (string.IsNullOrEmpty(gameId) ? uri : $"{uri}|{gameId}");

            // Check cache first
            if (_iconPathCache.TryGetValue(cacheKey, out var cachedPath))
            {
                return cachedPath;
            }

            // Create hash-based filename from the URI
            using (var sha = SHA256.Create())
            {
                var hashBytes = sha.ComputeHash(Encoding.UTF8.GetBytes(uri));
                var hashHex = BitConverter.ToString(hashBytes).Replace("-", "").Substring(0, 16);

                // Use per-game subfolder if gameId is provided
                var cacheDir = string.IsNullOrEmpty(gameId)
                    ? IconCacheDirectory
                    : Path.Combine(IconCacheDirectory, gameId);

                var sizeSuffix = useDecodeSizeSuffix ? $"_{decodeSize}" : string.Empty;
                var extension = ResolvePreferredExtensionForSource(uri, decodeSize);
                var path = Path.Combine(cacheDir, $"{hashHex}{sizeSuffix}{extension}");
                _iconPathCache[cacheKey] = path;
                return path;
            }
        }

        internal string GetAchievementIconCachePath(
            string gameId,
            bool preserveOriginalResolution,
            string fileStem,
            AchievementIconVariant variant)
        {
            var relativePath = AchievementIconCachePathBuilder.BuildRelativePath(
                gameId,
                preserveOriginalResolution,
                fileStem,
                variant);
            return Path.Combine(_cacheRoot, relativePath);
        }

        public bool TryMigrateLegacyAchievementIcon(
            string legacySourceIdentifier,
            string targetPath,
            int legacyDecodeSize,
            string gameId = null)
        {
            if (string.IsNullOrWhiteSpace(legacySourceIdentifier) ||
                string.IsNullOrWhiteSpace(targetPath) ||
                legacyDecodeSize <= 0)
            {
                return false;
            }

            try
            {
                if (File.Exists(targetPath))
                {
                    return true;
                }

                var legacyPath = GetIconCachePathFromUri(legacySourceIdentifier, legacyDecodeSize, gameId);
                if (string.IsNullOrWhiteSpace(legacyPath) || !File.Exists(legacyPath))
                {
                    return false;
                }

                EnsureTargetDirectory(targetPath);
                File.Move(legacyPath, targetPath);
                return true;
            }
            catch (IOException)
            {
                return File.Exists(targetPath);
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"Failed to migrate legacy cached icon to {targetPath}");
                return false;
            }
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
            var resolvedTargetPath = ResolveTargetPathForSource(targetPath, uri, preserveOriginalFormat);
            EnsureTargetDirectory(resolvedTargetPath);

            var pathLock = _pathWriteLocks.GetOrAdd(targetPath, _ => new SemaphoreSlim(1, 1));
            await pathLock.WaitAsync(cancel).ConfigureAwait(false);
            try
            {
                if (!overwriteExistingTarget && File.Exists(targetPath))
                {
                    return targetPath;
                }

                if (!overwriteExistingTarget &&
                    !string.Equals(resolvedTargetPath, targetPath, StringComparison.OrdinalIgnoreCase) &&
                    File.Exists(resolvedTargetPath))
                {
                    return resolvedTargetPath;
                }

                var downloadGate = IsRateLimitedDomain(uri) ? _rateLimitedDownloadGate : _downloadGate;

                byte[] bytes;
                await downloadGate.WaitAsync(cancel).ConfigureAwait(false);
                try
                {
                    if (!overwriteExistingTarget && File.Exists(targetPath))
                    {
                        return targetPath;
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
                    return resolvedTargetPath;
                }

                using (var ms = new MemoryStream(bytes, writable: false))
                {
                    return await SaveBitmapStreamToPathAsync(ms, resolvedTargetPath, decodeSize, cancel).ConfigureAwait(false);
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
            finally
            {
                pathLock.Release();
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

        private async Task<byte[]> DownloadBytesAsync(string url, CancellationToken cancel)
        {
            const int maxAttempts = 3;
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
                            resp.EnsureSuccessStatusCode();

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
                            backoff = TimeSpan.FromSeconds(backoff.TotalSeconds * 2);
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
                            backoff = TimeSpan.FromSeconds(backoff.TotalSeconds * 2);
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
                bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
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
        }

        public void RemoveGameIconCache(string gameId)
        {
            ClearGameCache(gameId);
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
            try { _iconPathCache.Clear(); } catch { }
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
            var parentDirectory = Path.GetDirectoryName(path);
            var modeFolder = string.IsNullOrWhiteSpace(parentDirectory)
                ? string.Empty
                : new DirectoryInfo(parentDirectory).Name;
            var isCompressed = string.Equals(modeFolder, "128", StringComparison.OrdinalIgnoreCase) ||
                               fileName.IndexOf("_128.", StringComparison.OrdinalIgnoreCase) >= 0;

            switch (scope)
            {
                case IconCacheClearScope.CompressedOnly:
                    return isCompressed;
                case IconCacheClearScope.FullResolutionOnly:
                    return !isCompressed;
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
            var resolvedTargetPath = ResolveTargetPathForSource(targetPath, localPath, preserveOriginalFormat);
            EnsureTargetDirectory(resolvedTargetPath);

            var pathLock = _pathWriteLocks.GetOrAdd(targetPath, _ => new SemaphoreSlim(1, 1));
            await pathLock.WaitAsync(cancel).ConfigureAwait(false);
            try
            {
                if (!overwriteExistingTarget && File.Exists(targetPath))
                {
                    return targetPath;
                }

                if (!overwriteExistingTarget &&
                    !string.Equals(resolvedTargetPath, targetPath, StringComparison.OrdinalIgnoreCase) &&
                    File.Exists(resolvedTargetPath))
                {
                    return resolvedTargetPath;
                }

                cancel.ThrowIfCancellationRequested();

                if (preserveOriginalFormat)
                {
                    File.Copy(localPath, resolvedTargetPath, overwrite: overwriteExistingTarget);
                    return resolvedTargetPath;
                }

                using (var ms = new MemoryStream(File.ReadAllBytes(localPath), writable: false))
                {
                    return await SaveBitmapStreamToPathAsync(ms, resolvedTargetPath, decodeSize, cancel).ConfigureAwait(false);
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
            finally
            {
                pathLock.Release();
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
            var pathLock = _pathWriteLocks.GetOrAdd(targetPath, _ => new SemaphoreSlim(1, 1));
            await pathLock.WaitAsync(cancel).ConfigureAwait(false);
            try
            {
                if (!overwriteExistingTarget && File.Exists(targetPath))
                {
                    return targetPath;
                }

                cancel.ThrowIfCancellationRequested();
                File.Copy(existingPath, targetPath, overwrite: overwriteExistingTarget);
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
            finally
            {
                pathLock.Release();
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
