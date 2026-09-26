using PlayniteAchievements.Services.Logging;
using SharpCompress.Archives;
using SharpCompress.Archives.SevenZip;
using SharpCompress.Archives.Zip;
using SharpCompress.Common;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace PlayniteAchievements.Providers.RetroAchievements.Hashing
{
    internal static class ArchiveUtils
    {
        private const int DefaultMaxEntries = 25;
        private const int ExtractBufferSize = 1024 * 1024;

        public static bool IsArchivePath(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return false;
            var ext = Path.GetExtension(filePath);
            return ext != null &&
                   (ext.Equals(".zip", StringComparison.OrdinalIgnoreCase) ||
                    ext.Equals(".7z", StringComparison.OrdinalIgnoreCase) ||
                    ext.Equals(".rar", StringComparison.OrdinalIgnoreCase) ||
                    CsoUtils.IsCsoPath(filePath) ||
                    RvzUtils.IsRvzPath(filePath));
        }

        public static bool IsCsoPath(string filePath)
        {
            return CsoUtils.IsCsoPath(filePath);
        }

        public static bool IsRvzPath(string filePath)
        {
            return RvzUtils.IsRvzPath(filePath);
        }

        /// <summary>
        /// Hash inputs for the entries of an archive, produced lazily so a caller that stops at the
        /// first match does no further work. Each input is disposed by the caller before the next
        /// one is requested; ending the enumeration removes any temporary files.
        /// </summary>
        /// <param name="forwardOnly">
        /// True when the hasher reads front to back (<see cref="IRaHasher.SupportsForwardOnlyInput"/>):
        /// entries are hashed straight from the archive with no temporary file. Otherwise entries
        /// are extracted, and a cue sheet is extracted together with the files it references.
        /// </param>
        public static IEnumerable<ArchiveHashInput> EnumerateHashInputs(string archivePath, bool forwardOnly, int maxEntries = DefaultMaxEntries)
        {
            using (var archive = OpenArchive(archivePath))
            {
                if (archive == null)
                {
                    yield break;
                }

                var entries = archive.Entries.Where(e => e != null && !e.IsDirectory && e.Size > 0).ToList();
                var wanted = entries
                    .Where(e => IsPlausibleRomEntry(e.Key) && (forwardOnly || !IsAudioEntry(e.Key)))
                    .OrderByDescending(e => e.Size)
                    .Take(Math.Max(1, maxEntries))
                    .ToList();

                if (forwardOnly)
                {
                    var inputs = archive.IsSolid
                        ? StreamSolidEntries(archive, archivePath, wanted)
                        : StreamEntries(archivePath, wanted);
                    foreach (var input in inputs)
                    {
                        yield return input;
                    }

                    yield break;
                }

                var tempDir = Path.Combine(Path.GetTempPath(), $"PlayniteAchievements_ra_{Guid.NewGuid():N}");
                Directory.CreateDirectory(tempDir);
                try
                {
                    var cues = entries.Where(e => CueTrackReader.IsCuePath(e.Key)).ToList();
                    var inputs = archive.IsSolid
                        ? ExtractSolidForDisc(archive, archivePath, tempDir, cues, wanted)
                        : ExtractForDisc(archivePath, entries, tempDir, cues, wanted);
                    foreach (var input in inputs)
                    {
                        yield return input;
                    }
                }
                finally
                {
                    TryDeleteDirectory(tempDir);
                }
            }
        }

        private static IEnumerable<ArchiveHashInput> StreamEntries(string archivePath, IEnumerable<IArchiveEntry> entries)
        {
            foreach (var entry in entries)
            {
                var e = entry;
                yield return new ArchiveHashInput(
                    e.Key,
                    RaHashSource.FromForwardOnlyStream(EntryHint(archivePath, e.Key), e.Size, () => e.OpenEntryStream()));
            }
        }

        // Solid archives decompress from the start of each block, so entries are read in archive
        // order in a single pass rather than opened one by one.
        private static IEnumerable<ArchiveHashInput> StreamSolidEntries(IArchive archive, string archivePath, IReadOnlyCollection<IArchiveEntry> wanted)
        {
            var keys = new HashSet<string>(wanted.Select(e => e.Key), StringComparer.OrdinalIgnoreCase);
            using (var reader = archive.ExtractAllEntries())
            {
                while (keys.Count > 0 && reader.MoveToNextEntry())
                {
                    var entry = reader.Entry;
                    if (entry == null || entry.IsDirectory || !keys.Remove(entry.Key))
                    {
                        continue;
                    }

                    yield return new ArchiveHashInput(
                        entry.Key,
                        RaHashSource.FromForwardOnlyStream(EntryHint(archivePath, entry.Key), entry.Size, () => reader.OpenEntryStream()));
                }
            }
        }

        private static IEnumerable<ArchiveHashInput> ExtractForDisc(
            string archivePath,
            IReadOnlyList<IArchiveEntry> entries,
            string tempDir,
            IReadOnlyList<IArchiveEntry> cues,
            IReadOnlyList<IArchiveEntry> wanted)
        {
            if (cues.Count > 0)
            {
                foreach (var cue in cues)
                {
                    var cuePath = ExtractTo(cue, tempDir);
                    foreach (var referenced in ReferencedEntries(cue.Key, cuePath, entries))
                    {
                        ExtractTo(referenced, tempDir);
                    }

                    yield return new ArchiveHashInput(cue.Key, RaHashSource.FromFile(cuePath));
                }

                yield break;
            }

            foreach (var entry in wanted)
            {
                var path = ExtractTo(entry, tempDir);
                yield return new ArchiveHashInput(entry.Key, RaHashSource.FromFile(path), deleteOnDispose: path);
            }
        }

        private static IEnumerable<ArchiveHashInput> ExtractSolidForDisc(
            IArchive archive,
            string archivePath,
            string tempDir,
            IReadOnlyList<IArchiveEntry> cues,
            IReadOnlyList<IArchiveEntry> wanted)
        {
            // One sequential pass writes everything the hashers could need; picking files by random
            // access would decompress the archive again for each one.
            var keep = new HashSet<string>(wanted.Select(e => e.Key).Concat(cues.Select(c => c.Key)), StringComparer.OrdinalIgnoreCase);
            var written = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var referencedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            using (var reader = archive.ExtractAllEntries())
            {
                while (reader.MoveToNextEntry())
                {
                    var entry = reader.Entry;
                    if (entry == null || entry.IsDirectory)
                    {
                        continue;
                    }

                    // Cue-referenced tracks are not always among the largest entries, so keep every
                    // non-audio file whose name a cue could reference when cues are present.
                    if (!keep.Contains(entry.Key) && !(cues.Count > 0 && !IsAudioEntry(entry.Key) && IsPlausibleRomEntry(entry.Key)))
                    {
                        continue;
                    }

                    var target = TempPathFor(tempDir, entry.Key);
                    using (var output = CreateTempOutput(target))
                    {
                        reader.WriteEntryTo(output);
                    }

                    written[entry.Key] = target;
                }
            }

            if (cues.Count > 0)
            {
                foreach (var cue in cues)
                {
                    if (written.TryGetValue(cue.Key, out var cuePath))
                    {
                        yield return new ArchiveHashInput(cue.Key, RaHashSource.FromFile(cuePath));
                    }
                }

                yield break;
            }

            foreach (var entry in wanted)
            {
                if (written.TryGetValue(entry.Key, out var path))
                {
                    yield return new ArchiveHashInput(entry.Key, RaHashSource.FromFile(path));
                }
            }
        }

        // Entries named by a cue's FILE lines, resolved relative to the cue's folder inside the archive.
        private static IEnumerable<IArchiveEntry> ReferencedEntries(string cueKey, string extractedCuePath, IReadOnlyList<IArchiveEntry> entries)
        {
            if (!CueSheetParser.TryParseFile(extractedCuePath, out var sheet, out _))
            {
                yield break;
            }

            var cueDir = NormalizeKey(Path.GetDirectoryName(NormalizeKey(cueKey)) ?? string.Empty);
            foreach (var file in sheet.Files)
            {
                if (string.IsNullOrWhiteSpace(file?.FileName))
                {
                    continue;
                }

                var wantedKey = NormalizeKey(string.IsNullOrEmpty(cueDir) ? file.FileName : Path.Combine(cueDir, file.FileName));
                var match = entries.FirstOrDefault(e => string.Equals(NormalizeKey(e.Key), wantedKey, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                {
                    yield return match;
                }
            }
        }

        private static string ExtractTo(IArchiveEntry entry, string tempDir)
        {
            var target = TempPathFor(tempDir, entry.Key);
            try
            {
                using (var input = entry.OpenEntryStream())
                using (var output = CreateTempOutput(target))
                {
                    input.CopyTo(output, ExtractBufferSize);
                }
            }
            catch
            {
                TryDeleteFile(target);
                throw;
            }

            return target;
        }

        private static FileStream CreateTempOutput(string target)
        {
            var dir = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            // Temporary lets the OS keep short-lived extracts in the cache instead of flushing them to disk.
            using (File.Create(target))
            {
            }
            File.SetAttributes(target, FileAttributes.Temporary);
            return new FileStream(target, FileMode.Truncate, FileAccess.Write, FileShare.Read, ExtractBufferSize);
        }

        // Mirrors the entry's relative path so cue FILE references resolve next to the cue.
        private static string TempPathFor(string tempDir, string entryKey)
        {
            var relative = NormalizeKey(entryKey).TrimStart(Path.DirectorySeparatorChar);
            var parts = relative.Split(Path.DirectorySeparatorChar)
                .Where(p => p.Length > 0 && p != "." && p != "..")
                .Select(p => string.Concat(p.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)))
                .ToArray();
            return parts.Length == 0
                ? Path.Combine(tempDir, Guid.NewGuid().ToString("N"))
                : Path.Combine(tempDir, Path.Combine(parts));
        }

        private static string NormalizeKey(string key)
        {
            return (key ?? string.Empty).Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        }

        private static string EntryHint(string archivePath, string entryKey)
        {
            return Path.Combine(archivePath, NormalizeKey(entryKey).TrimStart(Path.DirectorySeparatorChar));
        }

        private static bool IsPlausibleRomEntry(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;

            var ext = Path.GetExtension(name);
            if (string.IsNullOrWhiteSpace(ext)) return true;

            switch (ext.ToLowerInvariant())
            {
                case ".txt":
                case ".nfo":
                case ".diz":
                case ".cue":
                case ".m3u":
                case ".sfv":
                case ".md5":
                case ".sha1":
                case ".sha256":
                case ".png":
                case ".jpg":
                case ".jpeg":
                case ".gif":
                case ".webp":
                    return false;
                default:
                    return true;
            }
        }

        // Audio tracks of a disc image never carry the data a disc hasher reads.
        private static bool IsAudioEntry(string name)
        {
            switch ((Path.GetExtension(name) ?? string.Empty).ToLowerInvariant())
            {
                case ".wav":
                case ".ogg":
                case ".flac":
                case ".mp3":
                case ".ape":
                    return true;
                default:
                    return false;
            }
        }

        private static IArchive OpenArchive(string archivePath)
        {
            if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
            {
                return null;
            }

            var ext = Path.GetExtension(archivePath);

            if (ext.Equals(".zip", StringComparison.OrdinalIgnoreCase))
            {
                return ZipArchive.Open(archivePath);
            }

            if (ext.Equals(".7z", StringComparison.OrdinalIgnoreCase))
            {
                return SevenZipArchive.Open(archivePath);
            }

            return ArchiveFactory.Open(archivePath);
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.SetAttributes(path, FileAttributes.Normal);
                    File.Delete(path);
                }
            }
            catch
            {
                // ignore cleanup failures
            }
        }

        private static void TryDeleteDirectory(string path)
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    if (Directory.Exists(path))
                    {
                        Directory.Delete(path, recursive: true);
                    }

                    return;
                }
                catch (IOException)
                {
                    Thread.Sleep(50);
                }
                catch (UnauthorizedAccessException)
                {
                    Thread.Sleep(50);
                }
            }

            PluginLogger.GetLogger(nameof(ArchiveUtils)).Warn($"Failed to delete temp directory '{path}'");
        }

        /// <summary>One archive entry ready to hash, plus the temporary file to remove afterwards.</summary>
        internal sealed class ArchiveHashInput : IDisposable
        {
            private readonly string _deleteOnDispose;

            public ArchiveHashInput(string entryKey, RaHashSource source, string deleteOnDispose = null)
            {
                EntryKey = entryKey;
                Source = source;
                _deleteOnDispose = deleteOnDispose;
            }

            public string EntryKey { get; }
            public RaHashSource Source { get; }

            public void Dispose()
            {
                Source.Dispose();
                if (_deleteOnDispose != null)
                {
                    TryDeleteFile(_deleteOnDispose);
                }
            }
        }

        internal sealed class TempFile : IDisposable
        {
            public string Path { get; }

            public TempFile(string path)
            {
                Path = path;
            }

            public void Dispose()
            {
                if (string.IsNullOrWhiteSpace(Path)) return;

                Exception lastError = null;
                for (var attempt = 0; attempt < 5; attempt++)
                {
                    try
                    {
                        if (File.Exists(Path))
                        {
                            File.Delete(Path);
                        }
                        return;
                    }
                    catch (IOException ex)
                    {
                        lastError = ex;
                        if (attempt < 4)
                        {
                            Thread.Sleep(50 * (1 << attempt)); // 50ms, 100ms, 200ms, 400ms
                        }
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        lastError = ex;
                        if (attempt < 4)
                        {
                            Thread.Sleep(50 * (1 << attempt));
                        }
                    }
                    catch (Exception ex)
                    {
                        lastError = ex;
                        break; // Don't retry for other exceptions
                    }
                }

                // Log failure after all retries exhausted
                if (lastError != null)
                {
                    var logger = PluginLogger.GetLogger(nameof(ArchiveUtils));
                    logger.Warn(lastError, $"Failed to delete temp file '{Path}' after 5 attempts");
                }
            }
        }
    }
}
