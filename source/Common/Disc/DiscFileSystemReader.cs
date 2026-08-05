using DiscUtils;
using DiscUtils.Iso9660;
using DiscUtils.Udf;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PlayniteAchievements.Common.Disc
{
    /// <summary>
    /// Filesystem reader over an optical disc image stream. Detects ISO9660
    /// first (preserving behavior for bridge discs such as PS2 DVDs, which carry
    /// both filesystems) and falls back to UDF (pure-UDF images such as PS3
    /// Blu-rays). Provider-specific stream opening (e.g. cue sheets) stays with
    /// the caller; a plain image file can be opened directly via the path ctor.
    /// </summary>
    internal sealed class DiscFileSystemReader : IDisposable
    {
        private readonly Stream _ownedStream;
        private readonly DiscFileSystem _fs;

        public DiscFileSystemReader(string imagePath)
            : this(OpenImageStream(imagePath), leaveOpen: false)
        {
        }

        public DiscFileSystemReader(Stream imageStream, bool leaveOpen)
        {
            if (imageStream == null) throw new ArgumentNullException(nameof(imageStream));

            _ownedStream = leaveOpen ? null : imageStream;

            try
            {
                _fs = CDReader.Detect(imageStream)
                    ? (DiscFileSystem)new CDReader(imageStream, true)
                    : new UdfReader(imageStream);
            }
            catch
            {
                _ownedStream?.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            _fs?.Dispose();
            _ownedStream?.Dispose();
        }

        public Stream OpenFileOrNull(string pathInsideImage)
        {
            if (string.IsNullOrWhiteSpace(pathInsideImage)) return null;

            var normalized = NormalizeImagePath(pathInsideImage);
            if (TryOpenExact(normalized, out var stream)) return stream;

            var resolved = ResolvePathCaseInsensitive(normalized);
            if (resolved != null && TryOpenExact(resolved, out stream)) return stream;

            return null;
        }

        public bool FileExists(string pathInsideImage)
        {
            if (string.IsNullOrWhiteSpace(pathInsideImage)) return false;

            var normalized = NormalizeImagePath(pathInsideImage);
            if (_fs.FileExists(normalized)) return true;

            var resolved = ResolvePathCaseInsensitive(normalized);
            return resolved != null && _fs.FileExists(resolved);
        }

        /// <summary>
        /// Names of the directories in the image root, empty on any read failure.
        /// Lets callers gate path probes on what actually exists.
        /// </summary>
        public IReadOnlyCollection<string> GetRootDirectoryNames()
        {
            return SafeGetDirectories("\\")
                .Select(directory => Path.GetFileName(directory.TrimEnd('\\')))
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToList();
        }

        /// <summary>
        /// Reads a file inside the image fully into memory, or null when absent
        /// or larger than <paramref name="maxBytes"/>.
        /// </summary>
        public byte[] ReadAllBytesOrNull(string pathInsideImage, long maxBytes = 64L * 1024 * 1024)
        {
            using (var stream = OpenFileOrNull(pathInsideImage))
            {
                if (stream == null || stream.Length <= 0 || stream.Length > maxBytes)
                {
                    return null;
                }

                using (var buffer = new MemoryStream((int)stream.Length))
                {
                    stream.CopyTo(buffer);
                    return buffer.ToArray();
                }
            }
        }

        private static Stream OpenImageStream(string imagePath)
        {
            if (string.IsNullOrWhiteSpace(imagePath))
            {
                throw new ArgumentException("Disc image path is required.", nameof(imagePath));
            }

            return new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        }

        private bool TryOpenExact(string normalizedPath, out Stream stream)
        {
            stream = null;
            try
            {
                if (!_fs.FileExists(normalizedPath))
                {
                    return false;
                }

                stream = _fs.OpenFile(normalizedPath, FileMode.Open);
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
            try { return _fs.GetDirectories(path) ?? Enumerable.Empty<string>(); }
            catch { return Enumerable.Empty<string>(); }
        }

        private IEnumerable<string> SafeGetFiles(string path)
        {
            try { return _fs.GetFiles(path) ?? Enumerable.Empty<string>(); }
            catch { return Enumerable.Empty<string>(); }
        }

        private static string NormalizeImagePath(string pathInsideImage)
        {
            var p = pathInsideImage.Trim();
            p = p.Replace('/', '\\');
            while (p.StartsWith("\\", StringComparison.Ordinal)) p = p.Substring(1);
            return p;
        }
    }
}
