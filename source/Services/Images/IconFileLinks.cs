using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace PlayniteAchievements.Services.Images
{
    /// <summary>
    /// Hard links between byte-identical cached icons, so an image a provider reuses for many
    /// achievements is stored once while every achievement keeps its own file name.
    /// </summary>
    /// <remarks>
    /// Linked names share one file: a write that opens an existing name (FileMode.Create,
    /// File.Copy with overwrite) changes every name. Such writers call <see cref="Detach"/> first,
    /// which deletes a linked name so the write lands in a file of its own. Deletes and
    /// File.Replace act on the name alone and need nothing.
    /// </remarks>
    internal static class IconFileLinks
    {
        internal const string TempSuffix = ".link.tmp";

        private const int ErrorInvalidFunction = 1;
        private const int ErrorNotSameDevice = 17;
        private const int ErrorNotSupported = 50;
        private const int ErrorTooManyLinks = 1142;
        private const uint MoveFileReplaceExisting = 0x1;

        internal enum LinkResult
        {
            Linked,
            Skipped,
            KeeperFull,
            Unsupported
        }

        internal struct FileIdentity : IEquatable<FileIdentity>
        {
            public uint Volume;
            public ulong Index;

            public bool Equals(FileIdentity other) => Volume == other.Volume && Index == other.Index;
            public override bool Equals(object obj) => obj is FileIdentity other && Equals(other);
            public override int GetHashCode() => unchecked((int)Volume * 397) ^ Index.GetHashCode();
        }

        /// <summary>A file as it was when it was hashed, to tell whether it changed since.</summary>
        internal sealed class FileSnapshot
        {
            public string Path;
            public FileIdentity Identity;
            public long Length;
            public DateTime WriteTimeUtc;

            internal static FileSnapshot Take(string path)
            {
                if (!TryGetIdentity(path, out var identity, out _))
                {
                    return null;
                }

                var info = new FileInfo(path);
                return info.Exists
                    ? new FileSnapshot { Path = path, Identity = identity, Length = info.Length, WriteTimeUtc = info.LastWriteTimeUtc }
                    : null;
            }

            internal bool IsCurrent()
            {
                var now = Take(Path);
                return now != null &&
                       now.Identity.Equals(Identity) &&
                       now.Length == Length &&
                       now.WriteTimeUtc == WriteTimeUtc;
            }
        }

        /// <summary>One file to keep and the names to point at it.</summary>
        internal sealed class IdenticalFiles
        {
            public FileSnapshot Keeper;
            public List<FileSnapshot> Others;
        }

        internal static bool TryGetIdentity(string path, out FileIdentity identity, out int linkCount)
        {
            identity = default(FileIdentity);
            linkCount = 0;
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (!GetFileInformationByHandle(stream.SafeFileHandle, out var info))
                    {
                        return false;
                    }

                    identity = new FileIdentity
                    {
                        Volume = info.VolumeSerialNumber,
                        Index = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow
                    };
                    linkCount = (int)info.NumberOfLinks;
                    return true;
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return false;
            }
        }

        /// <summary>
        /// Deletes <paramref name="path"/> when other names share its file, so the caller's write
        /// creates a new file instead of changing theirs. Throws when the delete fails, so the write
        /// does not go ahead.
        /// </summary>
        internal static void Detach(string path)
        {
            if (!string.IsNullOrWhiteSpace(path) &&
                File.Exists(path) &&
                TryGetIdentity(path, out _, out var linkCount) &&
                linkCount > 1)
            {
                File.Delete(path);
            }
        }

        /// <summary>
        /// Points <paramref name="path"/> at <paramref name="keeperPath"/>'s file. The link is made
        /// under a temporary name and renamed over the path, so the path never goes missing.
        /// </summary>
        internal static LinkResult ReplaceWithLink(string path, string keeperPath)
        {
            var temp = path + TempSuffix;
            TryDelete(temp);
            if (!CreateHardLink(temp, keeperPath, IntPtr.Zero))
            {
                var error = Marshal.GetLastWin32Error();
                if (error == ErrorTooManyLinks)
                {
                    return LinkResult.KeeperFull;
                }

                return error == ErrorInvalidFunction || error == ErrorNotSupported || error == ErrorNotSameDevice
                    ? LinkResult.Unsupported
                    : LinkResult.Skipped;
            }

            if (MoveFileEx(temp, path, MoveFileReplaceExisting))
            {
                return LinkResult.Linked;
            }

            TryDelete(temp);
            return LinkResult.Skipped;
        }

        /// <summary>
        /// Groups the files under <paramref name="folders"/> that hold the same bytes but are not
        /// yet one file. Only files sharing a length are hashed, and names already linked are hashed
        /// once. Leftover link temp files are deleted on the way.
        /// </summary>
        internal static List<IdenticalFiles> FindIdenticalFiles(IEnumerable<string> folders, CancellationToken cancel)
        {
            var files = new List<FileInfo>();
            foreach (var folder in folders ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                {
                    continue;
                }

                foreach (var path in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                {
                    if (path.EndsWith(TempSuffix, StringComparison.OrdinalIgnoreCase))
                    {
                        TryDelete(path);
                        continue;
                    }

                    if (path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    files.Add(new FileInfo(path));
                }
            }

            var result = new List<IdenticalFiles>();
            using (var sha = SHA256.Create())
            {
                foreach (var sameLength in files.Where(f => f.Length > 0).GroupBy(f => f.Length).Where(g => g.Count() > 1))
                {
                    cancel.ThrowIfCancellationRequested();
                    var shared = sameLength
                        .Select(f => FileSnapshot.Take(f.FullName))
                        .Where(s => s != null)
                        .GroupBy(s => s.Identity)
                        .ToList();
                    if (shared.Count < 2)
                    {
                        continue;
                    }

                    var sameBytes = shared
                        .Select(names => new { Names = names.ToList(), Hash = Hash(sha, names.First().Path) })
                        .Where(x => x.Hash != null)
                        .GroupBy(x => x.Hash, StringComparer.Ordinal)
                        .Where(g => g.Count() > 1);
                    foreach (var group in sameBytes)
                    {
                        // The file with the most names stays, so the fewest names move.
                        var ordered = group
                            .OrderByDescending(x => x.Names.Count)
                            .ThenBy(x => x.Names[0].Path, StringComparer.OrdinalIgnoreCase)
                            .ToList();
                        result.Add(new IdenticalFiles
                        {
                            Keeper = ordered[0].Names[0],
                            Others = ordered.Skip(1).SelectMany(x => x.Names).ToList()
                        });
                    }
                }
            }

            return result;
        }

        private static string Hash(HashAlgorithm sha, string path)
        {
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    return Convert.ToBase64String(sha.ComputeHash(stream));
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return null;
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ByHandleFileInformation
        {
            public uint FileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint NumberOfLinks;
            public uint FileIndexHigh;
            public uint FileIndexLow;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation info);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool MoveFileEx(string existingFileName, string newFileName, uint flags);
    }
}
