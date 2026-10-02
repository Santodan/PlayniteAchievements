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

        /// <summary>
        /// The first filesystem read failure swallowed by the tolerant accessors
        /// (directory listing, file open), as "ExceptionType: message", or null when
        /// no read has failed. Filesystem parse errors otherwise surface only as
        /// empty listings; this lets callers report why an image that a desktop OS
        /// mounts fine yielded nothing.
        /// </summary>
        public string LastError { get; private set; }

        public DiscFileSystemReader(string imagePath)
            : this(OpenImageStream(imagePath), leaveOpen: false)
        {
        }

        public DiscFileSystemReader(Stream imageStream, bool leaveOpen)
            : this(imageStream, leaveOpen, allowUdf: true)
        {
        }

        private DiscFileSystemReader(Stream imageStream, bool leaveOpen, bool allowUdf)
        {
            if (imageStream == null) throw new ArgumentNullException(nameof(imageStream));

            _ownedStream = leaveOpen ? null : imageStream;

            try
            {
                if (!allowUdf)
                {
                    // CDReader validates the volume descriptors itself and throws for non-ISO9660 images.
                    _fs = new CDReader(imageStream, true);
                }
                else
                {
                    _fs = CDReader.Detect(imageStream)
                        ? (DiscFileSystem)new CDReader(imageStream, true)
                        : new UdfReader(imageStream);
                }
            }
            catch
            {
                _ownedStream?.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Opens an ISO9660 (Joliet preferred) image with no UDF fallback. RetroAchievements
        /// hashing reads only ISO9660, as rcheevos does.
        /// </summary>
        public static DiscFileSystemReader OpenIso9660(Stream imageStream, bool leaveOpen)
        {
            return new DiscFileSystemReader(imageStream, leaveOpen, allowUdf: false);
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
            var result = TryOpenExact(normalized, out var stream);
            if (result == OpenResult.Opened) return stream;
            if (result == OpenResult.Failed) return null;

            var resolved = ResolvePathCaseInsensitive(normalized);
            if (resolved != null && TryOpenExact(resolved, out stream) == OpenResult.Opened) return stream;

            return null;
        }

        /// <summary>
        /// Byte offset and length of a file's data within the image stream, resolved like
        /// <see cref="OpenFileOrNull"/>. False when the file is absent or not stored as one
        /// contiguous extent.
        /// </summary>
        public bool TryGetFileExtent(string pathInsideImage, out long startByte, out long length)
        {
            startByte = 0;
            length = 0;
            if (string.IsNullOrWhiteSpace(pathInsideImage) || !(_fs is IClusterBasedFileSystem clustered)) return false;

            var normalized = NormalizeImagePath(pathInsideImage);
            var path = _fs.FileExists(normalized) ? normalized : ResolvePathCaseInsensitive(normalized);
            if (path == null) return false;

            try
            {
                var extents = clustered.PathToExtents(path)?.ToList();
                if (extents == null || extents.Count != 1) return false;

                startByte = extents[0].Start;
                length = extents[0].Length;
                return true;
            }
            catch (Exception ex)
            {
                RecordError(ex);
                return false;
            }
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
            return GetDirectoryNames(null);
        }

        /// <summary>
        /// Names of the immediate child directories of a directory inside the
        /// image, empty when the directory is absent or unreadable. A null or
        /// empty path enumerates the image root. Lets callers walk containers
        /// whose child names are data (e.g. a TROPDIR of NPWR ids) rather than
        /// probing a fixed path list.
        /// </summary>
        public IReadOnlyCollection<string> GetDirectoryNames(string pathInsideImage)
        {
            var directory = "\\";
            if (!string.IsNullOrWhiteSpace(pathInsideImage))
            {
                directory = ResolveDirectoryCaseInsensitive(NormalizeImagePath(pathInsideImage));
                if (directory == null)
                {
                    return Array.Empty<string>();
                }
            }

            return SafeGetDirectories(directory)
                .Select(child => Path.GetFileName(child.TrimEnd('\\')))
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

        private enum OpenResult
        {
            Opened,
            Missing,
            Failed
        }

        /// <summary>
        /// Opens a file with one path walk. A missing file is a miss; any other exception is a
        /// miss when no file exists at the path (a directory, an invalid name) and a read failure
        /// otherwise.
        /// </summary>
        private OpenResult TryOpenExact(string normalizedPath, out Stream stream)
        {
            stream = null;
            try
            {
                stream = _fs.OpenFile(normalizedPath, FileMode.Open);
                return stream != null ? OpenResult.Opened : OpenResult.Missing;
            }
            catch (Exception ex) when (ex is FileNotFoundException || ex is DirectoryNotFoundException)
            {
                return OpenResult.Missing;
            }
            catch (Exception ex)
            {
                try
                {
                    if (!_fs.FileExists(normalizedPath))
                    {
                        return OpenResult.Missing;
                    }
                }
                catch (Exception existsEx)
                {
                    RecordError(existsEx);
                    return OpenResult.Failed;
                }

                RecordError(ex);
                return OpenResult.Failed;
            }
        }

        private string ResolvePathCaseInsensitive(string normalizedPath)
        {
            var parts = normalizedPath.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return null;

            var currentDir = ResolveDirectoryChain(parts, parts.Length - 1);
            if (currentDir == null) return null;

            var fileName = parts[parts.Length - 1];
            var files = SafeGetFiles(currentDir);
            var fileMatch = files.FirstOrDefault(f =>
                string.Equals(Path.GetFileName(f), fileName, StringComparison.OrdinalIgnoreCase));
            if (fileMatch != null) return fileMatch;

            // Some images expose versioned filenames; try appending ;1
            return files.FirstOrDefault(f =>
                string.Equals(Path.GetFileName(f), fileName + ";1", StringComparison.OrdinalIgnoreCase));
        }

        private string ResolveDirectoryCaseInsensitive(string normalizedPath)
        {
            var parts = normalizedPath.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
            return ResolveDirectoryChain(parts, parts.Length);
        }

        /// <summary>
        /// Walks the first <paramref name="count"/> segments as directories,
        /// matching each case-insensitively. Returns the directory path with a
        /// trailing separator, or null when a segment does not exist.
        /// </summary>
        private string ResolveDirectoryChain(string[] parts, int count)
        {
            var currentDir = "\\";

            for (var i = 0; i < count; i++)
            {
                var match = SafeGetDirectories(currentDir).FirstOrDefault(d =>
                    string.Equals(Path.GetFileName(d.TrimEnd('\\')), parts[i], StringComparison.OrdinalIgnoreCase));
                if (match == null) return null;

                currentDir = match.EndsWith("\\", StringComparison.Ordinal) ? match : match + "\\";
            }

            return currentDir;
        }

        private IEnumerable<string> SafeGetDirectories(string path)
        {
            try { return _fs.GetDirectories(path) ?? Enumerable.Empty<string>(); }
            catch (Exception ex) { RecordError(ex); return Enumerable.Empty<string>(); }
        }

        private IEnumerable<string> SafeGetFiles(string path)
        {
            try { return _fs.GetFiles(path) ?? Enumerable.Empty<string>(); }
            catch (Exception ex) { RecordError(ex); return Enumerable.Empty<string>(); }
        }

        private void RecordError(Exception ex)
        {
            if (LastError == null && ex != null)
            {
                LastError = $"{ex.GetType().Name}: {ex.Message}";
            }
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
