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
    /// <see cref="Invalidate(string)"/> after a successful save and the gallery viewer re-scans fresh
    /// on open.
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

        /// <summary>Forces a fresh scan of one game (used by the viewer on open) and returns it.</summary>
        public GameCaptureSet RefreshGame(string gameName)
        {
            Invalidate(gameName);
            return ScanGame(gameName);
        }

        public void Invalidate(string gameName)
        {
            var folder = UnlockScreenshotService.SanitizeCaptureGameName(gameName);
            lock (_lock)
            {
                if (!string.IsNullOrEmpty(folder))
                {
                    _gameCache.Remove(folder);
                }

                _foldersWithCaptures = null;
            }
        }

        public void Invalidate()
        {
            lock (_lock)
            {
                _gameCache.Clear();
                _foldersWithCaptures = null;
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
