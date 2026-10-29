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
using PlayniteAchievements.Services.Images.Webm;
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

        // Every recognized format, not only the ones this machine can decode: a cached file must
        // stay visible to path probing, size accounting, and clearing regardless of codec state.
        private static string[] SupportedImageExtensions => ImageFormats.All;

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
            DeleteSessionImageFiles();
        }

        // Set once a session image is written, so saves skip looking for loose copies until then.
        private volatile bool _sessionImagesWritten;

        private bool IsSessionImagePath(string path)
        {
            try
            {
                return string.Equals(
                    Path.GetDirectoryName(Path.GetFullPath(path)),
                    Path.GetFullPath(IconCacheDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private void AfterSave(string uri, bool savedSessionImage)
        {
            if (savedSessionImage)
            {
                _sessionImagesWritten = true;
            }
            else
            {
                DeleteSessionImages(uri);
            }
        }

        // Saving an address that was already shown takes the session file when it holds the image
        // at full size, as a save does, instead of downloading it again.
        private bool TryTakeSessionImage(string uri, int decodeSize, string resolvedTargetPath)
        {
            if (decodeSize > 0)
            {
                return false;
            }

            var sessionPath = ResolveTargetPathForSource(GetIconCachePathFromUri(uri, 0), uri, 0);
            if (!File.Exists(sessionPath) ||
                !string.Equals(Path.GetExtension(sessionPath), Path.GetExtension(resolvedTargetPath), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            try
            {
                IconFileLinks.Detach(resolvedTargetPath);
                File.Copy(sessionPath, resolvedTargetPath, overwrite: true);
                NoteWritten(resolvedTargetPath);
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"Failed to take session image '{sessionPath}' for '{resolvedTargetPath}'.");
                return false;
            }

            try
            {
                File.Delete(sessionPath);
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"Failed to delete session image '{sessionPath}'.");
            }

            DeleteSessionImages(uri);
            return true;
        }

        // Deletes every session file of an address, at any size, once the image is saved elsewhere.
        private void DeleteSessionImages(string uri)
        {
            if (!_sessionImagesWritten)
            {
                return;
            }

            try
            {
                foreach (var file in Directory.EnumerateFiles(IconCacheDirectory, ComputeUriHashPrefix(uri) + "*", SearchOption.TopDirectoryOnly).ToList())
                {
                    try
                    {
                        File.Delete(file);
                    }
                    catch (Exception ex)
                    {
                        _logger?.Debug(ex, $"Failed to delete session image '{file}'.");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"Failed to look for session images of '{uri}'.");
            }
        }

        // An image shown from an address with no game to file it under is cached in the icon_cache
        // root for the session only: nothing stores its path, so each start clears what the last left.
        private void DeleteSessionImageFiles()
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(IconCacheDirectory, "*", SearchOption.TopDirectoryOnly)
                    .Where(IsSupportedCacheImageFile)
                    .ToList())
                {
                    try
                    {
                        File.Delete(file);
                    }
                    catch (Exception ex)
                    {
                        _logger?.Debug(ex, $"Failed to delete session image '{file}'.");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, "Failed to clear session images from the icon cache root.");
            }
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
        /// This is retained for display-time hash-cache lookups (MemoryImageService, AnimatedImageHelper).
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
            AchievementIconVariant variant,
            ISet<string> directorySnapshot = null)
        {
            var canonicalPath = GetAchievementIconCachePath(gameId, fileStem, variant);
            if (string.IsNullOrWhiteSpace(canonicalPath))
            {
                return null;
            }

            // A caller resolving many icons in one pass hands in a listing of the directory taken
            // once, and each probe becomes a set lookup. Without it this is up to nine File.Exists
            // per call -- the canonical name plus every supported extension -- and callers that
            // walk a game's achievements paid that per row, twice, once per variant.
            if (directorySnapshot != null)
            {
                if (directorySnapshot.Contains(Path.GetFileName(canonicalPath)))
                {
                    return canonicalPath;
                }

                foreach (var extension in SupportedImageExtensions)
                {
                    var candidate = Path.ChangeExtension(canonicalPath, extension);
                    if (directorySnapshot.Contains(Path.GetFileName(candidate)))
                    {
                        return candidate;
                    }
                }

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
        // Every reader and downloader resolves the path here, so art stored under the legacy
        // lowercase-hash name is adopted into this path before anyone probes or writes it.
        internal string GetDefaultCategoryImagePath(
            string gameId,
            string categoryLabel)
        {
            var relativePath = AchievementIconCachePathBuilder.BuildDefaultCategoryRelativePath(
                gameId,
                categoryLabel);
            var canonicalPath = Path.Combine(_cacheRoot, relativePath);
            AdoptLegacyDefaultCategoryImage(gameId, categoryLabel, canonicalPath);
            return canonicalPath;
        }

        // Moves default category art stored under the lowercase-hash name into the current
        // case-sensitive path when the current path holds no art yet. A no-op for all-lowercase
        // labels, whose two paths coincide.
        private void AdoptLegacyDefaultCategoryImage(
            string gameId,
            string categoryLabel,
            string canonicalPath)
        {
            var trimmedLabel = (categoryLabel ?? string.Empty).Trim();
            if (string.Equals(trimmedLabel, trimmedLabel.ToLowerInvariant(), StringComparison.Ordinal))
            {
                return;
            }

            var legacyPath = Path.Combine(
                _cacheRoot,
                AchievementIconCachePathBuilder.BuildLegacyDefaultCategoryRelativePath(gameId, categoryLabel));
            if (string.Equals(legacyPath, canonicalPath, StringComparison.Ordinal))
            {
                return;
            }

            var snapshot = PeekDefaultCategoryArtSnapshot(gameId);
            if (snapshot != null && snapshot.Count == 0)
            {
                return;
            }

            var current = snapshot == null
                ? ProbeDefaultCategoryImagePath(canonicalPath)
                : FindDefaultCategoryImageInSnapshot(snapshot, canonicalPath);
            if (current != null)
            {
                return;
            }

            var legacy = snapshot == null
                ? ProbeDefaultCategoryImagePath(legacyPath)
                : FindDefaultCategoryImageInSnapshot(snapshot, legacyPath);
            if (legacy == null || _failedLegacyDefaultCategoryAdoptions.ContainsKey(legacy))
            {
                return;
            }

            var target = Path.ChangeExtension(canonicalPath, Path.GetExtension(legacy));
            try
            {
                File.Move(legacy, target);
                InvalidateDefaultCategoryArtSnapshot(gameId);
            }
            catch (Exception ex)
            {
                // Not retried this session, so a file that cannot move costs one attempt, not one per read.
                _failedLegacyDefaultCategoryAdoptions[legacy] = 0;
                _logger?.Warn(ex, $"Failed to move default category image for '{categoryLabel}' from '{legacy}' to '{target}'.");
            }
        }

        private readonly ConcurrentDictionary<string, byte> _failedLegacyDefaultCategoryAdoptions =
            new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

        // The stored file for canonicalPath in a directory snapshot, across supported extensions.
        private static string FindDefaultCategoryImageInSnapshot(HashSet<string> snapshot, string canonicalPath)
        {
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

            return FindDefaultCategoryImageInSnapshot(snapshot, canonicalPath);
        }

        /// <summary>
        /// Publishes a local image file as the provider-supplied default category art for
        /// (gameId, normalized category label), preserving the source extension. Existing
        /// art is kept, so a re-refresh never overwrites a previous default or a manually
        /// replaced file. Returns the stored (or already existing) path, or null when the
        /// source is missing or the copy fails.
        /// </summary>
        internal string SaveDefaultCategoryImageFromFile(
            string gameId,
            string categoryLabel,
            string sourcePath)
        {
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            {
                return null;
            }

            var existing = FindExistingDefaultCategoryImagePath(gameId, categoryLabel);
            if (existing != null)
            {
                return existing;
            }

            var canonicalPath = GetDefaultCategoryImagePath(gameId, categoryLabel);
            if (string.IsNullOrWhiteSpace(canonicalPath))
            {
                return null;
            }

            try
            {
                var extension = Path.GetExtension(sourcePath)?.ToLowerInvariant();
                var targetPath = string.IsNullOrWhiteSpace(extension)
                    ? canonicalPath
                    : Path.ChangeExtension(canonicalPath, extension);
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath));
                File.Copy(sourcePath, targetPath, overwrite: false);
                NoteWritten(targetPath);
                InvalidateDefaultCategoryArtSnapshot(gameId);
                return targetPath;
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"Failed to store default category image for '{categoryLabel}'.");
                return null;
            }
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

        // The game's snapshot when one is already built, else null. Path resolution must not build
        // one: a caller resolves the path before writing the file, and a snapshot taken then would
        // miss it.
        private HashSet<string> PeekDefaultCategoryArtSnapshot(string gameId)
        {
            return _defaultCategoryArtSnapshots.TryGetValue(NormalizeSnapshotGameKey(gameId), out var lazy) &&
                   lazy.IsValueCreated
                ? lazy.Value
                : null;
        }

        /// <summary>
        /// One listing of a game's achievement icon cache directory, for a caller resolving many
        /// icons in a single pass. Deliberately not cached on this service: a stale listing would
        /// hide an icon that had just been written. The caller holds it only for its own pass,
        /// during which nothing writes here, and discards it afterwards.
        /// </summary>
        internal HashSet<string> ScanAchievementIconCacheDirectory(string gameId)
        {
            try
            {
                var canonicalPath = GetAchievementIconCachePath(
                    gameId,
                    "probe",
                    AchievementIconVariant.Unlocked);
                var directory = Path.GetDirectoryName(canonicalPath);
                if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                {
                    return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                }

                return new HashSet<string>(
                    Directory.EnumerateFiles(directory).Select(Path.GetFileName),
                    StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                // Null means "no snapshot": callers fall back to probing per file.
                return null;
            }
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

            var preserveOriginalFormat = ShouldPreserveOriginalFormat(uri, decodeSize);
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
                    var savesSessionImage = IsSessionImagePath(resolvedTargetPath);
                    if (!savesSessionImage && TryTakeSessionImage(uri, decodeSize, resolvedTargetPath))
                    {
                        InvalidateDefaultCategoryArtSnapshotForTargetPath(resolvedTargetPath);
                        if (overwritingExisting)
                        {
                            NotifyImageFileOverwritten(resolvedTargetPath);
                        }

                        return resolvedTargetPath;
                    }

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

                        AfterSave(uri, savesSessionImage);
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

                            AfterSave(uri, savesSessionImage);
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

        private async Task SavePngWithRetryAsync(
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
                    IconFileLinks.Detach(cachePath);
                    using (var fs = new FileStream(cachePath, FileMode.Create, FileAccess.Write, FileShare.Read))
                    {
                        encoder.Save(fs);
                    }

                    NoteWritten(cachePath);
                    return;
                }
                catch (IOException) when (attempt < maxAttempts)
                {
                    await Task.Delay(50 * attempt, cancel).ConfigureAwait(false);
                }
            }
        }

        private async Task SaveBytesWithRetryAsync(
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
                    IconFileLinks.Detach(targetPath);
                    using (var fs = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.Read))
                    {
                        await fs.WriteAsync(bytes, 0, bytes.Length, cancel).ConfigureAwait(false);
                    }

                    NoteWritten(targetPath);
                    return;
                }
                catch (IOException) when (attempt < maxAttempts)
                {
                    await Task.Delay(50 * attempt, cancel).ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// Replaces the bytes of a file already in the cache, leaving its path and extension alone.
        /// </summary>
        /// <remarks>
        /// Written for the compression sweep, which rewrites cached images smaller in place. It
        /// takes the same per-path write lock as the download path so a concurrent refresh cannot
        /// interleave with the rewrite, stages the new bytes in a sibling temp file, and swaps them
        /// in with <see cref="File.Replace(string, string, string)"/> so a cancel or crash mid-write
        /// can never leave a truncated image behind.
        /// </remarks>
        internal async Task ReplaceCachedImageBytesAsync(
            string path,
            byte[] bytes,
            CancellationToken cancel)
        {
            if (string.IsNullOrWhiteSpace(path) || bytes == null || bytes.Length == 0)
            {
                return;
            }

            var pathLock = await AcquirePathWriteLockAsync(path, cancel).ConfigureAwait(false);
            using (pathLock)
            {
                cancel.ThrowIfCancellationRequested();
                if (!File.Exists(path))
                {
                    return;
                }

                var tempPath = path + ".compress.tmp";
                try
                {
                    await SaveBytesWithRetryAsync(tempPath, bytes, cancel).ConfigureAwait(false);

                    try
                    {
                        File.Replace(tempPath, path, null);
                    }
                    catch (Exception)
                    {
                        // File.Replace refuses across volumes and on some filesystems; the
                        // delete-then-move fallback is not atomic but stays within the same folder.
                        File.Delete(path);
                        File.Move(tempPath, path);
                    }
                }
                finally
                {
                    try
                    {
                        if (File.Exists(tempPath))
                        {
                            File.Delete(tempPath);
                        }
                    }
                    catch
                    {
                    }
                }

                NotifyImageFileOverwritten(path);
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

        /// <summary>
        /// Ensures the icon at <paramref name="path"/> is stored as a centered square no larger than
        /// <paramref name="maxDimension"/> on a side. Compares the image dimensions via a header-only
        /// read (no full pixel decode), and only decodes, scales, crops, and rewrites the file (as
        /// PNG) when it is non-square or oversized. No-op for conforming, missing, or empty paths, so
        /// it is cheap to call on every refresh.
        /// </summary>
        public async Task EnsureIconSquareAsync(string path, int maxDimension, CancellationToken cancel)
        {
            // WebM keeps its own bytes: WPF can neither decode nor re-encode it.
            if (string.IsNullOrWhiteSpace(path) ||
                !File.Exists(path) ||
                ImageFormats.IsWebmExtension(ImageFormats.GetExtension(path)))
            {
                return;
            }

            var pathLock = await AcquirePathWriteLockAsync(path, cancel).ConfigureAwait(false);
            using (pathLock)
            {
                try
                {
                    if (!File.Exists(path))
                    {
                        return;
                    }

                    int width;
                    int height;

                    // Header-only read: DelayCreation reads dimensions from the image header without
                    // decoding the pixels, so already-square icons cost only a metadata read.
                    using (var headerStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        var frame = BitmapFrame.Create(
                            headerStream,
                            BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile,
                            BitmapCacheOption.None);
                        width = frame.PixelWidth;
                        height = frame.PixelHeight;
                    }

                    var oversized = maxDimension > 0 && Math.Min(width, height) > maxDimension;
                    if (width <= 0 || height <= 0 || (Math.Abs(width - height) <= 1 && !oversized))
                    {
                        return;
                    }

                    var originalBytes = File.ReadAllBytes(path);
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                    if (oversized)
                    {
                        // Decoded so the shorter edge lands on the cap, which the crop keeps whole.
                        if (height < width)
                        {
                            bitmap.DecodePixelHeight = maxDimension;
                        }
                        else
                        {
                            bitmap.DecodePixelWidth = maxDimension;
                        }
                    }
                    using (var ms = new MemoryStream(originalBytes, writable: false))
                    {
                        bitmap.StreamSource = ms;
                        bitmap.EndInit();
                    }

                    var squared = CropToSquare(bitmap);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(squared));
                    await SavePngWithRetryAsync(path, encoder, cancel).ConfigureAwait(false);

                    InvalidateDefaultCategoryArtSnapshotForTargetPath(path);
                    NotifyImageFileOverwritten(path);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger?.Warn(ex, $"Failed to normalize icon to square: {path}");
                }
            }
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
                                    return null;
                                }

                                resp.EnsureSuccessStatusCode();
                            }

                            // Sized from Content-Length when the server sends it, so the buffer is
                            // allocated once instead of doubling, and handed back without a copy.
                            var declared = resp.Content.Headers.ContentLength;
                            var capacity = declared > 0 && declared <= MaxBytes ? (int)declared.Value : 0;
                            using (var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                            using (var ms = new MemoryStream(capacity))
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

                                return capacity > 0 && ms.Length == capacity ? ms.GetBuffer() : ms.ToArray();
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
            if (ImageFormats.IsWebmExtension(ImageFormats.GetExtension(cachePath)))
            {
                return WebmStill.TryDecode(cachePath, decodePixel);
            }

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

        // Set once a link fails because the volume has no hard links; later games skip the scan.
        private volatile bool _hardLinksUnsupported;

        // Folders this service wrote a file into since their last link pass.
        private readonly ConcurrentDictionary<string, byte> _foldersWritten =
            new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

        private void NoteWritten(string path)
        {
            try
            {
                var folder = Path.GetDirectoryName(Path.GetFullPath(path));
                if (!string.IsNullOrEmpty(folder))
                {
                    _foldersWritten[folder] = 0;
                }
            }
            catch
            {
            }
        }

        // Folders whose existing files have been linked once, one "icon_cache-relative path|folder id"
        // per line, the last line for a path winning. The folder's file-system id makes a folder
        // restored from a backup, which holds separate copies again, or deleted and made anew, a
        // different folder that is scanned once more. Kept inside icon_cache so clearing the cache
        // clears it too.
        private readonly object _linkedOnceSync = new object();
        private Dictionary<string, string> _linkedOnce;

        private string LinkedOncePath => Path.Combine(IconCacheDirectory, "linked-folders.txt");

        private string LinkedOnceKey(string folder)
        {
            var full = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar);
            var root = Path.GetFullPath(IconCacheDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full.Substring(root.Length) : full;
        }

        private static string FolderId(string folder)
        {
            return IconFileLinks.TryGetFolderIdentity(folder, out var identity)
                ? identity.Volume.ToString("X8") + ":" + identity.Index.ToString("X16")
                : null;
        }

        private Dictionary<string, string> LoadLinkedOnce()
        {
            if (_linkedOnce == null)
            {
                _linkedOnce = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    if (File.Exists(LinkedOncePath))
                    {
                        foreach (var line in File.ReadAllLines(LinkedOncePath))
                        {
                            var split = line.LastIndexOf('|');
                            if (split > 0)
                            {
                                _linkedOnce[line.Substring(0, split)] = line.Substring(split + 1);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger?.Debug(ex, "Failed to read the linked icon folder list.");
                }
            }

            return _linkedOnce;
        }

        // A folder that does not exist has nothing to link; one made later is made by a write.
        private bool IsLinkedOnce(string folder)
        {
            if (!Directory.Exists(folder))
            {
                return true;
            }

            var id = FolderId(folder);
            lock (_linkedOnceSync)
            {
                return id != null &&
                       LoadLinkedOnce().TryGetValue(LinkedOnceKey(folder), out var recorded) &&
                       string.Equals(recorded, id, StringComparison.OrdinalIgnoreCase);
            }
        }

        private void MarkLinkedOnce(IEnumerable<string> folders)
        {
            var current = folders
                .Select(folder => (Key: LinkedOnceKey(folder), Id: FolderId(folder)))
                .Where(folder => folder.Id != null)
                .ToList();
            lock (_linkedOnceSync)
            {
                var linkedOnce = LoadLinkedOnce();
                var added = new List<string>();
                foreach (var folder in current)
                {
                    if (!linkedOnce.TryGetValue(folder.Key, out var recorded) ||
                        !string.Equals(recorded, folder.Id, StringComparison.OrdinalIgnoreCase))
                    {
                        linkedOnce[folder.Key] = folder.Id;
                        added.Add(folder.Key + "|" + folder.Id);
                    }
                }

                if (added.Count == 0)
                {
                    return;
                }

                try
                {
                    EnsureIconCacheDirectory();
                    File.AppendAllLines(LinkedOncePath, added);
                }
                catch (Exception ex)
                {
                    _logger?.Debug(ex, "Failed to record linked icon folders.");
                }
            }
        }

        // Clears and reports the written folders at or under any of the roots.
        private bool TakeFoldersWrittenUnder(IEnumerable<string> roots)
        {
            var taken = false;
            foreach (var root in roots)
            {
                var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
                foreach (var folder in _foldersWritten.Keys)
                {
                    if ((string.Equals(folder, prefix, StringComparison.OrdinalIgnoreCase) ||
                         folder.StartsWith(prefix + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) &&
                        _foldersWritten.TryRemove(folder, out _))
                    {
                        taken = true;
                    }
                }
            }

            return taken;
        }

        /// <summary>
        /// Hard-links byte-identical files in a game's original, category_defaults and metadata
        /// folders, so an image the provider reuses for many achievements is stored once. Every
        /// name stays where it is. custom/ is left alone: the user's files and the capstone's
        /// recorded write times live there. Returns the number of names linked.
        /// </summary>
        internal async Task<int> LinkIdenticalGameIconFilesAsync(string gameId, CancellationToken cancel, bool onlyWhenWritten = true)
        {
            if (_hardLinksUnsupported || string.IsNullOrWhiteSpace(gameId))
            {
                return 0;
            }

            var gameDir = Path.Combine(IconCacheDirectory, gameId.Trim());
            var folders = new[]
            {
                AchievementIconCachePathBuilder.ModeFolderName,
                AchievementIconCachePathBuilder.DefaultCategoryFolderName,
                AchievementIconCachePathBuilder.MetadataSourceFolderName
            }.Select(folder => Path.Combine(gameDir, folder));

            return await LinkIdenticalFilesAsync(folders, $"game '{gameId}'", cancel, onlyWhenWritten).ConfigureAwait(false);
        }

        /// <summary>
        /// Hard-links byte-identical files under <paramref name="folders"/>, such as a shared metadata
        /// list folder whose source reuses one image for many rows. Returns the number of names linked.
        /// With <paramref name="onlyWhenWritten"/>, folders this service wrote nothing into since
        /// their last pass are not scanned.
        /// </summary>
        internal async Task<int> LinkIdenticalFilesAsync(IEnumerable<string> folders, string label, CancellationToken cancel, bool onlyWhenWritten = true)
        {
            var roots = folders?.Where(folder => !string.IsNullOrWhiteSpace(folder)).ToList();
            if (_hardLinksUnsupported || roots == null || roots.Count == 0)
            {
                return 0;
            }

            // A folder is scanned once to link what it already holds, then only after writes.
            var written = TakeFoldersWrittenUnder(roots);
            if (onlyWhenWritten && !written && roots.All(IsLinkedOnce))
            {
                return 0;
            }

            folders = roots;

            var linked = 0;
            try
            {
                var groups = await Task.Run(() => IconFileLinks.FindIdenticalFiles(folders, cancel), cancel).ConfigureAwait(false);
                foreach (var group in groups)
                {
                    using (await AcquirePathWriteLockAsync(group.Keeper.Path, cancel).ConfigureAwait(false))
                    {
                        if (!group.Keeper.IsCurrent())
                        {
                            continue;
                        }

                        foreach (var file in group.Others)
                        {
                            using (await AcquirePathWriteLockAsync(file.Path, cancel).ConfigureAwait(false))
                            {
                                // A write since the hash may have put another image here.
                                if (!file.IsCurrent())
                                {
                                    continue;
                                }

                                var result = IconFileLinks.ReplaceWithLink(file.Path, group.Keeper.Path);
                                if (result == IconFileLinks.LinkResult.Linked)
                                {
                                    linked++;
                                }
                                else if (result == IconFileLinks.LinkResult.Unsupported)
                                {
                                    _hardLinksUnsupported = true;
                                    _logger?.Info("Icon cache volume does not support hard links; identical icons stay separate files.");
                                    return linked;
                                }
                                else if (result == IconFileLinks.LinkResult.KeeperFull)
                                {
                                    break;
                                }
                            }
                        }
                    }
                }

                MarkLinkedOnce(roots);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"Failed to link identical icon files for {label}.");
            }

            if (linked > 0)
            {
                _logger?.Debug($"Linked {linked} identical icon file(s) for {label}.");
            }

            return linked;
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

            switch (scope)
            {
                case IconCacheClearScope.LockedOnly:
                    return AchievementIconCachePathBuilder.IsLockedVariantPath(path);
                default:
                    return true;
            }
        }

        // Whether two paths name the same file on disk, compared as full paths so a differing but
        // equivalent spelling does not read as two files.
        private static bool IsSameFilePath(string first, string second)
        {
            if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second))
            {
                return false;
            }

            try
            {
                return string.Equals(
                    Path.GetFullPath(first),
                    Path.GetFullPath(second),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return string.Equals(first.Trim(), second.Trim(), StringComparison.OrdinalIgnoreCase);
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

            var preserveOriginalFormat = ShouldPreserveOriginalFormat(localPath, decodeSize);
            var resolvedTargetPath = ResolveTargetPathForSource(targetPath, localPath, decodeSize);

            // The source can already BE the target. The resolved target takes the source's
            // extension, so a value that was materialized here before resolves back onto itself
            // whenever the canonical extension differs from the stored one -- a .jpg custom icon
            // against a canonical .png target. File.Copy refuses a self-copy, and with
            // overwriteExistingTarget set both early returns below are skipped, so re-applying an
            // icon threw and the caller took the resulting null for "no icon", dropping the very
            // art it was re-applying.
            if (IsSameFilePath(localPath, resolvedTargetPath))
            {
                return resolvedTargetPath;
            }

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
                        IconFileLinks.Detach(resolvedTargetPath);
                        File.Copy(localPath, resolvedTargetPath, overwrite: overwriteExistingTarget);
                        NoteWritten(resolvedTargetPath);
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
                    IconFileLinks.Detach(targetPath);
                    File.Copy(existingPath, targetPath, overwrite: overwriteExistingTarget);
                    NoteWritten(targetPath);
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

        /// <summary>
        /// A decode size asks for a resized PNG, but WebM always keeps its own bytes: WPF has no
        /// decoder that could resize it, and the PNG would lose the animation.
        /// </summary>
        private static bool ShouldPreserveOriginalFormat(string source, int decodeSize)
        {
            return decodeSize <= 0 || ImageFormats.IsWebmExtension(GetNormalizedSourceExtension(source));
        }

        private static string ResolvePreferredExtensionForSource(string source, int decodeSize)
        {
            if (!ShouldPreserveOriginalFormat(source, decodeSize))
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
            return ResolveTargetPathForSource(targetPath, source, ShouldPreserveOriginalFormat(source, decodeSize));
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
            return ImageFormats.IsSupportedExtension(extension);
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
