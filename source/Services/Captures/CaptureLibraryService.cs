using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    internal sealed class CaptureLibraryService
    {
        private readonly Func<PersistedSettings> _settingsAccessor;
        private readonly ILogger _logger;
        private readonly object _lock = new object();
        private readonly Dictionary<string, GameCaptureSet> _gameCache =
            new Dictionary<string, GameCaptureSet>(StringComparer.OrdinalIgnoreCase);
        private HashSet<string> _foldersWithCaptures;

        public CaptureLibraryService(Func<PersistedSettings> settingsAccessor, ILogger logger)
        {
            _settingsAccessor = settingsAccessor;
            _logger = logger;
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
        /// Forces a fresh scan of one game (used by the viewer on open) and returns it. Deliberately
        /// silent: opening the gallery is a read, not a change, and must not make every open grid
        /// re-stamp itself.
        /// </summary>
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
            lock (_lock)
            {
                _gameCache.Clear();
                _foldersWithCaptures = null;
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
                dirs.Add(screenshotDir.Trim());
            }

            // Recording dir falls back to the screenshot dir at write time; mirror that here.
            var recordingDir = persisted?.UnlockRecordingDirectory;
            recordingDir = string.IsNullOrWhiteSpace(recordingDir) ? screenshotDir : recordingDir;
            if (!string.IsNullOrWhiteSpace(recordingDir))
            {
                recordingDir = recordingDir.Trim();
                if (!dirs.Any(d => string.Equals(d, recordingDir, StringComparison.OrdinalIgnoreCase)))
                {
                    dirs.Add(recordingDir);
                }
            }

            return dirs;
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

        private static bool IsCaptureFile(string path)
        {
            var ext = Path.GetExtension(path);
            return string.Equals(ext, ".png", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(ext, ".mp4", StringComparison.OrdinalIgnoreCase);
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
                var gameFolder = Path.Combine(baseDir, sanitizedFolder);
                try
                {
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
                    _logger?.Debug(ex, $"Capture scan failed for '{gameFolder}'.");
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
    }
}
