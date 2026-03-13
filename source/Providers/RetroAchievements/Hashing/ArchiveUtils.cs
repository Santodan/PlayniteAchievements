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

        public static IReadOnlyList<ArchiveEntryInfo> GetCandidateEntries(string archivePath, int maxEntries = 25)
        {
            using (var archive = OpenArchive(archivePath))
            {
                if (archive == null)
                {
                    return Array.Empty<ArchiveEntryInfo>();
                }

                return archive.Entries
                    .Where(e => e != null && !e.IsDirectory && e.Size > 0)
                    .Select(e => new ArchiveEntryInfo(e.Key, e.Size))
                    .OrderByDescending(e => e.Size)
                    .Where(IsPlausibleRomEntry)
                    .Take(Math.Max(1, maxEntries))
                    .ToList();
            }
        }

        private static bool IsPlausibleRomEntry(ArchiveEntryInfo entry)
        {
            var name = entry?.Key;
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

        public static TempFile ExtractEntryToTempFile(string archivePath, ArchiveEntryInfo entry)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));

            using (var archive = OpenArchive(archivePath))
            {
                if (archive == null)
                {
                    throw new InvalidOperationException("Unable to open archive.");
                }

                var archiveEntry = archive.Entries.FirstOrDefault(e =>
                    !e.IsDirectory &&
                    string.Equals(e.Key, entry.Key, StringComparison.OrdinalIgnoreCase) &&
                    e.Size == entry.Size);

                if (archiveEntry == null)
                {
                    archiveEntry = archive.Entries.FirstOrDefault(e => !e.IsDirectory && string.Equals(e.Key, entry.Key, StringComparison.OrdinalIgnoreCase));
                }

                if (archiveEntry == null)
                {
                    throw new FileNotFoundException("Archive entry not found.", entry.Key);
                }

                var outExt = Path.GetExtension(entry.Key) ?? string.Empty;
                var outPath = Path.Combine(Path.GetTempPath(), $"PlayniteAchievements_ra_{Guid.NewGuid():N}{outExt}");

                using (var outStream = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    archiveEntry.WriteTo(outStream);
                }

                return new TempFile(outPath);
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

        internal sealed class ArchiveEntryInfo
        {
            public string Key { get; }
            public long Size { get; }

            public ArchiveEntryInfo(string key, long size)
            {
                Key = key;
                Size = size;
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

