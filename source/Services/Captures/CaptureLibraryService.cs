using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Media.Imaging;
using Playnite.SDK;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Images;
using PlayniteAchievements.Services.UI;

namespace PlayniteAchievements.Services.Captures
{
    /// <summary>
    /// Read side of the unlock-capture pipeline. The writers (<see cref="UnlockScreenshotService"/>
    /// and the recording service) drop loose files as
    /// <c>&lt;baseDir&gt;\&lt;Game&gt;\NNN_AchievementName[_variant].png|mp4</c> with no index; this
    /// service enumerates them and parses each via <see cref="CaptureFileNameParser"/> into a per-game
    /// <see cref="GameCaptureSet"/>. Results are cached per game (deterministic, no TTL); writers call
    /// <see cref="Invalidate(string)"/> after a successful save, which also raises
    /// <see cref="CapturesChanged"/> so grids that are already open re-stamp their rows instead of
    /// waiting for a rebuild. The gallery viewer re-scans fresh on open via <see cref="RefreshGame"/>,
    /// which stays silent because it is a read path.
    /// </summary>
    internal sealed class CaptureLibraryService : IDisposable
    {
        private readonly Func<PersistedSettings> _settingsAccessor;
        private readonly ILogger _logger;
        private readonly object _lock = new object();
        private readonly Dictionary<string, GameCaptureSet> _gameCache =
            new Dictionary<string, GameCaptureSet>(StringComparer.OrdinalIgnoreCase);
        private HashSet<string> _foldersWithCaptures;
        private readonly List<FileSystemWatcher> _watchers = new List<FileSystemWatcher>();
        private readonly HashSet<string> _watchedDirectories =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ImageValidationCacheEntry> _imageValidationCache =
            new Dictionary<string, ImageValidationCacheEntry>(StringComparer.OrdinalIgnoreCase);
        private string _captureConfigurationKey;
        private Timer _changeDebounce;
        private bool _disposed;
        private readonly bool _watchForChanges;

        /// <param name="watchForChanges">
        /// Whether to watch the capture directories for changes made outside the plugin. Off only
        /// for tests: with a watcher running, nothing happens to the directories behind the
        /// service's back, so a test that changes them out of band is racing the watcher thread
        /// rather than observing the caching behavior it means to assert.
        /// </param>
        public CaptureLibraryService(
            Func<PersistedSettings> settingsAccessor,
            ILogger logger,
            bool watchForChanges = true)
        {
            _settingsAccessor = settingsAccessor;
            _logger = logger;
            _watchForChanges = watchForChanges;
        }

        /// <summary>
        /// Raised on the UI dispatcher after a game's captures on disk have changed. Subscribers may
        /// touch UI-bound collections directly; a throwing subscriber never reaches the writer.
        /// </summary>
        public event EventHandler<CapturesChangedEventArgs> CapturesChanged;

        /// <summary>Parses (or returns the cached) capture set for a game. Never throws.</summary>
        public GameCaptureSet ScanGame(string gameName)
        {
            var folder = UnlockScreenshotService.SanitizeCaptureGameName(gameName);
            if (string.IsNullOrEmpty(folder))
            {
                return GameCaptureSet.Empty;
            }

            lock (_lock)
            {
                if (_gameCache.TryGetValue(folder, out var cached))
                {
                    return cached;
                }
            }

            var scanned = ScanGameFolder(folder);
            lock (_lock)
            {
                _gameCache[folder] = scanned;
            }

            return scanned;
        }

        public bool GameHasCaptures(string gameName) => ScanGame(gameName).HasAny;

        public bool AchievementHasCaptures(string gameName, string achievementDisplayName)
        {
            var stem = AchievementIconCachePathBuilder.SanitizeSegment(achievementDisplayName);
            return ScanGame(gameName).ContainsAchievementStem(stem);
        }

        /// <summary>
        /// Membership test for the summary grid: true when the game's capture folder exists and holds
        /// at least one capture file. Backed by a single cached directory enumeration so the summary
        /// grid does not scan-and-parse every game.
        /// </summary>
        public bool GameFolderHasCaptures(string gameName)
        {
            var folder = UnlockScreenshotService.SanitizeCaptureGameName(gameName);
            return !string.IsNullOrEmpty(folder) && GetGameFoldersWithCaptures().Contains(folder);
        }

        /// <summary>Sanitized folder names (siblings of the Test folder) that contain any capture file.</summary>
        public IReadOnlyCollection<string> GetGameFoldersWithCaptures()
        {
            EnsureWatchers();
            lock (_lock)
            {
                if (_foldersWithCaptures != null)
                {
                    return _foldersWithCaptures;
                }
            }

            var set = ComputeFoldersWithCaptures();
            lock (_lock)
            {
                _foldersWithCaptures = set;
            }

            return set;
        }

        /// <summary>
        /// Returns parsed, readable screenshots across the capture library. This is the shared
        /// source for slideshow-style consumers; it intentionally reuses the same configured
        /// suffix parser and per-game cache as the capture gallery.
        /// </summary>
        public IReadOnlyList<CaptureItem> GetScreenshots(
            CaptureVariant? variant = null,
            bool forceRefresh = false,
            IReadOnlyCollection<string> gameFolders = null)
        {
            EnsureWatchers();
            if (forceRefresh)
            {
                ClearCaches();
            }

            // The filter holds sanitized folder names (the slideshow scopes to a pin collection);
            // limiting the scan here keeps a scoped consumer from parsing every game folder.
            var allowedFolders = gameFolders == null
                ? null
                : new HashSet<string>(gameFolders, StringComparer.OrdinalIgnoreCase);
            var screenshots = new List<CaptureItem>();
            foreach (var folder in GetGameFoldersWithCaptures())
            {
                if (allowedFolders != null && !allowedFolders.Contains(folder))
                {
                    continue;
                }

                screenshots.AddRange(
                    ScanGame(folder)
                        .Groups
                        .SelectMany(group => group.Items)
                        .Where(item => !item.IsVideo)
                        .Where(item => !variant.HasValue || item.Variant == variant.Value)
                        .Where(item => IsReadableImage(item.FilePath)));
            }

            return screenshots
                .GroupBy(item => item.FilePath, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderByDescending(item => GetLastWriteTimeUtc(item.FilePath))
                .ThenBy(item => item.FilePath, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>Forces a fresh scan of one game (used by the viewer on open) and returns it.</summary>
        public GameCaptureSet RefreshGame(string gameName)
        {
            InvalidateGameCore(UnlockScreenshotService.SanitizeCaptureGameName(gameName));
            return ScanGame(gameName);
        }

        public void Invalidate(string gameName)
        {
            var folder = UnlockScreenshotService.SanitizeCaptureGameName(gameName);
            InvalidateGameCore(folder);
            RaiseCapturesChanged(gameName, folder);
        }

        public void Invalidate()
        {
            ClearCaches();
            SignalChanged();
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                DisposeWatchersLocked();
                _imageValidationCache.Clear();
                _changeDebounce?.Dispose();
                _changeDebounce = null;
            }

            RaiseCapturesChanged(null, null);
        }

        /// <summary>
        /// Drops one game's parsed set and re-probes just that folder in the membership set. Probing
        /// the single folder keeps the cheap summary-grid set warm: nulling it would make every saved
        /// capture force a full re-enumeration of every game folder on the next summary mark, once per
        /// capture while a game is running. Nothing to sync when the set was never materialized.
        /// </summary>
        private void InvalidateGameCore(string sanitizedFolder)
        {
            if (string.IsNullOrEmpty(sanitizedFolder))
            {
                return;
            }

            bool needsProbe;
            lock (_lock)
            {
                _gameCache.Remove(sanitizedFolder);
                needsProbe = _foldersWithCaptures != null;
            }

            if (!needsProbe)
            {
                return;
            }

            // Probe outside the lock: it touches the file system.
            var hasCaptures = ProbeFolderHasCaptures(sanitizedFolder);

            lock (_lock)
            {
                if (_foldersWithCaptures == null)
                {
                    // A racing Invalidate() dropped the set; it will be recomputed on next read.
                    return;
                }

                // Copy-on-write: GetGameFoldersWithCaptures hands the live set to lock-free readers.
                var updated = new HashSet<string>(_foldersWithCaptures, StringComparer.OrdinalIgnoreCase);
                if (hasCaptures)
                {
                    updated.Add(sanitizedFolder);
                }
                else
                {
                    updated.Remove(sanitizedFolder);
                }

                _foldersWithCaptures = updated;
            }
        }

        private bool ProbeFolderHasCaptures(string sanitizedFolder)
        {
            foreach (var baseDir in ResolveBaseDirectories())
            {
                try
                {
                    var folder = Path.Combine(baseDir, sanitizedFolder);
                    if (Directory.Exists(folder) && FolderHasCaptureFile(folder))
                    {
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    _logger?.Debug(ex, $"Capture folder probe failed for '{sanitizedFolder}'.");
                }
            }

            return false;
        }

        /// <summary>
        /// Marshals to the UI dispatcher so subscribers can read UI-bound collections, and swallows
        /// subscriber failures: this runs on the capture pipeline's thread right after a save.
        /// </summary>
        private void RaiseCapturesChanged(string gameName, string folderName)
        {
            var handler = CapturesChanged;
            if (handler == null)
            {
                return;
            }

            var args = new CapturesChangedEventArgs(gameName, folderName);
            Action raise = () =>
            {
                try
                {
                    handler(this, args);
                }
                catch (Exception ex)
                {
                    _logger?.Debug(ex, "Captures-changed subscriber failed.");
                }
            };

            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
            {
                raise();
            }
            else
            {
                dispatcher.BeginInvoke(raise);
            }
        }

        private IReadOnlyList<string> ResolveBaseDirectories()
        {
            var persisted = _settingsAccessor?.Invoke();
            var dirs = new List<string>(2);

            var screenshotDir = persisted?.UnlockScreenshotDirectory;
            if (!string.IsNullOrWhiteSpace(screenshotDir))
            {
                dirs.Add(ResolveLibraryRoot(screenshotDir));
            }

            // Recording dir falls back to the screenshot dir at write time; mirror that here.
            var recordingDir = persisted?.UnlockRecordingDirectory;
            recordingDir = string.IsNullOrWhiteSpace(recordingDir) ? screenshotDir : recordingDir;
            if (!string.IsNullOrWhiteSpace(recordingDir))
            {
                recordingDir = ResolveLibraryRoot(recordingDir);
                if (!dirs.Any(d => string.Equals(d, recordingDir, StringComparison.OrdinalIgnoreCase)))
                {
                    dirs.Add(recordingDir);
                }
            }

            // A recording directory can be configured separately. Do not let an explicitly
            // configured <capture root>\Test path reintroduce the reserved test captures through
            // a second base-directory scan.
            return dirs
                .Where(candidate => !dirs.Any(parent =>
                    !string.Equals(parent, candidate, StringComparison.OrdinalIgnoreCase) &&
                    IsSamePath(candidate, Path.Combine(parent, UnlockScreenshotService.TestFolderName))))
                .ToList();
        }

        private static string ResolveLibraryRoot(string configuredDirectory)
        {
            var directory = configuredDirectory?.Trim();
            const string gameToken = "<gameName>";
            if (!string.IsNullOrWhiteSpace(directory) &&
                directory.EndsWith(gameToken, StringComparison.OrdinalIgnoreCase))
            {
                directory = directory.Substring(0, directory.Length - gameToken.Length)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }

            return directory;
        }

        private void EnsureWatchers()
        {
            if (!_watchForChanges)
            {
                return;
            }

            var persisted = _settingsAccessor?.Invoke();
            var desired = ResolveBaseDirectories()
                .Where(Directory.Exists)
                .Select(path =>
                {
                    try
                    {
                        return Path.GetFullPath(path);
                    }
                    catch
                    {
                        return null;
                    }
                })
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var configurationKey = string.Join("|", desired) + "\n" +
                (persisted?.UnlockScreenshotSuffixClean ?? string.Empty) + "\n" +
                (persisted?.UnlockScreenshotSuffixWithToast ?? string.Empty) + "\n" +
                (persisted?.UnlockScreenshotSuffixFramed ?? string.Empty);

            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }

                var configurationChanged = !string.Equals(
                    _captureConfigurationKey,
                    configurationKey,
                    StringComparison.Ordinal);
                if (configurationChanged)
                {
                    _captureConfigurationKey = configurationKey;
                    _gameCache.Clear();
                    _foldersWithCaptures = null;
                    _imageValidationCache.Clear();
                }

                if (_watchedDirectories.SetEquals(desired))
                {
                    return;
                }

                DisposeWatchersLocked();
                foreach (var directory in desired)
                {
                    try
                    {
                        var watcher = new FileSystemWatcher(directory)
                        {
                            IncludeSubdirectories = true,
                            NotifyFilter = NotifyFilters.FileName |
                                NotifyFilters.DirectoryName |
                                NotifyFilters.LastWrite |
                                NotifyFilters.CreationTime
                        };
                        watcher.Created += CaptureFileChanged;
                        watcher.Changed += CaptureFileChanged;
                        watcher.Deleted += CaptureFileChanged;
                        watcher.Renamed += CaptureFileChanged;
                        watcher.EnableRaisingEvents = true;
                        _watchers.Add(watcher);
                        _watchedDirectories.Add(directory);
                    }
                    catch (Exception ex)
                    {
                        _logger?.Debug(ex, $"Capture watcher could not monitor '{directory}'.");
                    }
                }
            }
        }

        private void CaptureFileChanged(object sender, FileSystemEventArgs e)
        {
            // A rename away from the capture shape still removes a capture, so either end counts.
            var isCapture = IsCaptureFile(e?.FullPath) ||
                (e is RenamedEventArgs renamed && IsCaptureFile(renamed.OldFullPath));
            if (!isCapture || IsReservedTestCapture(sender, e?.FullPath))
            {
                return;
            }

            ClearCaches();
            SignalChanged();
        }

        private void SignalChanged()
        {
            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }

                if (_changeDebounce == null)
                {
                    _changeDebounce = new Timer(
                        _ => RaiseCapturesChanged(null, null),
                        null,
                        Timeout.Infinite,
                        Timeout.Infinite);
                }

                _changeDebounce.Change(200, Timeout.Infinite);
            }
        }

        private void ClearCaches()
        {
            lock (_lock)
            {
                _gameCache.Clear();
                _foldersWithCaptures = null;
                _imageValidationCache.Clear();
            }
        }

        private void DisposeWatchersLocked()
        {
            foreach (var watcher in _watchers)
            {
                try
                {
                    watcher.EnableRaisingEvents = false;
                    watcher.Created -= CaptureFileChanged;
                    watcher.Changed -= CaptureFileChanged;
                    watcher.Deleted -= CaptureFileChanged;
                    watcher.Renamed -= CaptureFileChanged;
                    watcher.Dispose();
                }
                catch
                {
                }
            }

            _watchers.Clear();
            _watchedDirectories.Clear();
        }

        private bool IsReadableImage(string path)
        {
            DateTime? lastWriteUtc = null;
            long? length = null;
            try
            {
                var info = new FileInfo(path);
                lastWriteUtc = info.LastWriteTimeUtc;
                length = info.Length;
                lock (_lock)
                {
                    if (_imageValidationCache.TryGetValue(path, out var cached) &&
                        cached.LastWriteUtc == lastWriteUtc.Value &&
                        cached.Length == length.Value)
                    {
                        return cached.IsReadable;
                    }
                }

                using (var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    // None, not OnLoad: the dimensions below come from the header, so decoding the
                    // pixels would buy nothing. The validation cache is per-session, so on a cold
                    // start this runs over every capture in the library before the first slide can
                    // be shown, and OnLoad made that a full decode of each one.
                    var decoder = BitmapDecoder.Create(
                        stream,
                        BitmapCreateOptions.PreservePixelFormat,
                        BitmapCacheOption.None);
                    var readable = decoder.Frames.Count > 0 &&
                        decoder.Frames[0].PixelWidth > 0 &&
                        decoder.Frames[0].PixelHeight > 0;
                    CacheImageValidation(path, lastWriteUtc, length, readable);
                    return readable;
                }
            }
            catch
            {
                CacheImageValidation(path, lastWriteUtc, length, isReadable: false);
                return false;
            }
        }

        private void CacheImageValidation(
            string path,
            DateTime? lastWriteUtc,
            long? length,
            bool isReadable)
        {
            if (string.IsNullOrWhiteSpace(path) || !lastWriteUtc.HasValue || !length.HasValue)
            {
                return;
            }

            lock (_lock)
            {
                if (!_disposed)
                {
                    _imageValidationCache[path] = new ImageValidationCacheEntry
                    {
                        LastWriteUtc = lastWriteUtc.Value,
                        Length = length.Value,
                        IsReadable = isReadable
                    };
                }
            }
        }

        private static DateTime GetLastWriteTimeUtc(string path)
        {
            try
            {
                return File.GetLastWriteTimeUtc(path);
            }
            catch
            {
                return DateTime.MinValue;
            }
        }

        private HashSet<string> ComputeFoldersWithCaptures()
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var baseDir in ResolveBaseDirectories())
            {
                try
                {
                    if (!Directory.Exists(baseDir))
                    {
                        continue;
                    }

                    foreach (var sub in Directory.EnumerateDirectories(baseDir))
                    {
                        var name = Path.GetFileName(sub);
                        if (string.Equals(name, UnlockScreenshotService.TestFolderName, StringComparison.OrdinalIgnoreCase) ||
                            result.Contains(name))
                        {
                            continue;
                        }

                        if (FolderHasCaptureFile(sub))
                        {
                            result.Add(name);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger?.Debug(ex, $"Capture folder enumeration failed for '{baseDir}'.");
                }
            }

            return result;
        }

        private static bool FolderHasCaptureFile(string folder)
        {
            return Directory
                .EnumerateFiles(folder, "*.*", SearchOption.TopDirectoryOnly)
                .Any(IsCaptureFile);
        }

        // Same shape test the scan applies, so a folder or watcher event holding only other tools'
        // screenshots never reads as a capture.
        private static bool IsCaptureFile(string path) =>
            CaptureFileNameParser.HasCaptureSignature(path);

        private static bool IsReservedTestCapture(object sender, string path)
        {
            if (!(sender is FileSystemWatcher watcher) || string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            try
            {
                var testRoot = Path.GetFullPath(Path.Combine(
                    watcher.Path,
                    UnlockScreenshotService.TestFolderName));
                var candidate = Path.GetFullPath(path);
                return candidate.StartsWith(
                    testRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                        Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsSamePath(string first, string second)
        {
            try
            {
                return string.Equals(
                    Path.GetFullPath(first).TrimEnd(
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar),
                    Path.GetFullPath(second).TrimEnd(
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private GameCaptureSet ScanGameFolder(string sanitizedFolder)
        {
            var persisted = _settingsAccessor?.Invoke();
            var resolver = CaptureFileNameParser.CreateResolver(
                persisted?.UnlockScreenshotSuffixClean,
                persisted?.UnlockScreenshotSuffixWithToast,
                persisted?.UnlockScreenshotSuffixFramed);
            var items = new List<CaptureItem>();

            foreach (var baseDir in ResolveBaseDirectories())
            {
                try
                {
                    // The capture directories are user-configurable. Path.Combine itself throws
                    // for malformed paths, so it must be inside the same guard as enumeration;
                    // capture discovery is an optional menu feature and must never abort Playnite's
                    // complete game-menu construction.
                    var gameFolder = Path.Combine(baseDir, sanitizedFolder);
                    if (!Directory.Exists(gameFolder))
                    {
                        continue;
                    }

                    foreach (var file in Directory.EnumerateFiles(gameFolder, "*.*", SearchOption.TopDirectoryOnly))
                    {
                        if (CaptureFileNameParser.TryParse(file, resolver, out var item))
                        {
                            items.Add(item);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger?.Debug(ex, $"Capture scan failed for base '{baseDir}' and game '{sanitizedFolder}'.");
                }
            }

            if (items.Count == 0)
            {
                return GameCaptureSet.Empty;
            }

            var groups = items
                .GroupBy(i => i.AchievementStem, StringComparer.OrdinalIgnoreCase)
                .Select(g => new AchievementCaptureGroup(
                    g.Min(i => i.Number),
                    g.Key,
                    g.OrderBy(i => (int)i.Variant)
                        .ThenBy(i => i.FilePath, StringComparer.OrdinalIgnoreCase)
                        .ToList()))
                .OrderBy(g => g.Number)
                .ThenBy(g => g.AchievementStem, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new GameCaptureSet(groups);
        }

        private sealed class ImageValidationCacheEntry
        {
            public DateTime LastWriteUtc { get; set; }

            public long Length { get; set; }

            public bool IsReadable { get; set; }
        }
    }
}
