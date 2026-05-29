using DiscUtils.Iso9660;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PlayniteAchievements.Providers.RetroAchievements.Hashing
{
    internal sealed class DiscUtilsFacade : IDisposable
    {
        private readonly DiscImageReader _image;
        private readonly CDReader _cd;

        public DiscUtilsFacade(string isoPath)
        {
            if (string.IsNullOrWhiteSpace(isoPath)) throw new ArgumentException("ISO path is required.", nameof(isoPath));

            _image = DiscImageReader.Open(isoPath);
            _cd = new CDReader(_image.Stream, true);
        }

        public void Dispose()
        {
            _cd?.Dispose();
            _image?.Dispose();
        }

        public Stream OpenFileOrNull(string pathInsideIso)
        {
            if (string.IsNullOrWhiteSpace(pathInsideIso)) return null;

            var normalized = NormalizeIsoPath(pathInsideIso);
            if (TryOpenExact(normalized, out var stream)) return stream;

            var resolved = ResolvePathCaseInsensitive(normalized);
            if (resolved != null && TryOpenExact(resolved, out stream)) return stream;

            return null;
        }

        public bool FileExists(string pathInsideIso)
        {
            if (string.IsNullOrWhiteSpace(pathInsideIso)) return false;

            var normalized = NormalizeIsoPath(pathInsideIso);
            if (_cd.FileExists(normalized)) return true;

            var resolved = ResolvePathCaseInsensitive(normalized);
            return resolved != null && _cd.FileExists(resolved);
        }

        private bool TryOpenExact(string normalizedPath, out Stream stream)
        {
            stream = null;
            try
            {
                if (!_cd.FileExists(normalizedPath))
                {
                    return false;
                }

                stream = _cd.OpenFile(normalizedPath, FileMode.Open);
                return true;
            }
            catch
            {
                stream?.Dispose();
                stream = null;
                return false;
            }
        }

        private string ResolvePathCaseInsensitive(string normalizedPath)
        {
            var parts = normalizedPath.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return null;

            var currentDir = "\\";

            for (var i = 0; i < parts.Length; i++)
            {
                var part = parts[i];
                var isLast = i == parts.Length - 1;

                if (!isLast)
                {
                    var dirs = SafeGetDirectories(currentDir);
                    var match = dirs.FirstOrDefault(d =>
                        string.Equals(Path.GetFileName(d.TrimEnd('\\')), part, StringComparison.OrdinalIgnoreCase));
                    if (match == null) return null;

                    currentDir = match;
                    if (!currentDir.EndsWith("\\", StringComparison.Ordinal)) currentDir += "\\";
                }
                else
                {
                    var files = SafeGetFiles(currentDir);
                    var fileMatch = files.FirstOrDefault(f =>
                        string.Equals(Path.GetFileName(f), part, StringComparison.OrdinalIgnoreCase));
                    if (fileMatch != null) return fileMatch;

                    // Some images expose versioned filenames; try appending ;1
                    fileMatch = files.FirstOrDefault(f =>
                        string.Equals(Path.GetFileName(f), part + ";1", StringComparison.OrdinalIgnoreCase));
                    if (fileMatch != null) return fileMatch;

                    return null;
                }
            }

            return null;
        }

        private IEnumerable<string> SafeGetDirectories(string path)
        {
            try { return _cd.GetDirectories(path) ?? Enumerable.Empty<string>(); }
            catch { return Enumerable.Empty<string>(); }
        }

        private IEnumerable<string> SafeGetFiles(string path)
        {
            try { return _cd.GetFiles(path) ?? Enumerable.Empty<string>(); }
            catch { return Enumerable.Empty<string>(); }
        }

        private static string NormalizeIsoPath(string pathInsideIso)
        {
            var p = pathInsideIso.Trim();
            p = p.Replace('/', '\\');
            while (p.StartsWith("\\", StringComparison.Ordinal)) p = p.Substring(1);
            return p;
        }
    }
}
