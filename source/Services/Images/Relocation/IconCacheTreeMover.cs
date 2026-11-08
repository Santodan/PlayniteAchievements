using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace PlayniteAchievements.Services.Images.Relocation
{
    /// <summary>
    /// Moves a folder tree to another folder one file at a time, so the extra space a move across
    /// volumes needs is one file, not the whole tree. Names that share one file through a hard link
    /// stay one file at the target when the target volume supports links. A run that stopped
    /// part way is finished by running it again.
    /// </summary>
    internal static class IconCacheTreeMover
    {
        // Links refused in a row before the move stops trying, as on a volume without hard links.
        private const int MaxLinkFailures = 8;

        // A file another program holds open is tried again this many times, this far apart.
        private const int LockedRetries = 5;
        private const int LockedRetryDelayMs = 200;

        /// <summary>What a run did: the files it moved and the source files it could only copy.</summary>
        internal sealed class MoveTreeResult
        {
            public int Moved;

            /// <summary>
            /// Source files held open by another program: copied to the target, but still in the
            /// source until whatever holds them lets go.
            /// </summary>
            public List<string> Leftovers = new List<string>();
        }

        /// <summary>Totals for a tree, counting each hard-linked file once in UniqueBytes.</summary>
        internal sealed class TreeSize
        {
            public int FileCount;
            public long TotalBytes;
            public long UniqueBytes;
        }

        internal static TreeSize Measure(string source, CancellationToken cancel)
        {
            var size = new TreeSize();
            if (!Directory.Exists(source))
            {
                return size;
            }

            var seen = new HashSet<IconFileLinks.FileIdentity>();
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                cancel.ThrowIfCancellationRequested();
                long length;
                try
                {
                    length = new FileInfo(file).Length;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    continue;
                }

                size.FileCount++;
                size.TotalBytes += length;
                if (!IconFileLinks.TryGetIdentity(file, out var identity, out var linkCount) ||
                    linkCount <= 1 ||
                    seen.Add(identity))
                {
                    size.UniqueBytes += length;
                }
            }

            return size;
        }

        /// <summary>
        /// Moves every file under <paramref name="source"/> to the same place under
        /// <paramref name="target"/>, then removes the folders left empty, <paramref name="source"/>
        /// included. <paramref name="onFile"/> is told after each file. Throws
        /// <see cref="OperationCanceledException"/> between files when cancelled.
        /// A file another program holds open is tried again, then copied when it can be read,
        /// and listed in <see cref="MoveTreeResult.Leftovers"/>.
        /// </summary>
        internal static MoveTreeResult MoveTree(
            string source,
            string target,
            Action<int, int> onFile,
            CancellationToken cancel)
        {
            var result = new MoveTreeResult();
            if (!Directory.Exists(source))
            {
                return result;
            }

            Directory.CreateDirectory(target);
            var sourcePrefix = IconCacheLocation.NormalizeFolder(source) + Path.DirectorySeparatorChar;
            var files = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).ToList();
            var movedByIdentity = new Dictionary<IconFileLinks.FileIdentity, string>();
            var linkFailures = 0;

            for (var i = 0; i < files.Count; i++)
            {
                cancel.ThrowIfCancellationRequested();
                var file = files[i];
                if (file.EndsWith(IconFileLinks.TempSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    TryDeleteFile(file);
                    onFile?.Invoke(i + 1, files.Count);
                    continue;
                }

                var relative = file.StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase)
                    ? file.Substring(sourcePrefix.Length)
                    : Path.GetFileName(file);
                var destination = Path.Combine(target, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination));

                if (!MoveFile(file, destination, movedByIdentity, ref linkFailures))
                {
                    result.Leftovers.Add(file);
                }

                result.Moved++;
                onFile?.Invoke(i + 1, files.Count);
            }

            DeleteEmptyFolders(source);
            return result;
        }

        /// <summary>Deletes the files still there; returns the ones that are still held open.</summary>
        internal static List<string> DeleteLeftovers(IEnumerable<string> leftovers)
        {
            var remaining = new List<string>();
            foreach (var file in leftovers ?? Enumerable.Empty<string>())
            {
                try
                {
                    if (File.Exists(file))
                    {
                        File.Delete(file);
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    remaining.Add(file);
                }
            }

            return remaining;
        }

        // False when the source file had to stay behind because another program holds it open.
        private static bool MoveFile(
            string file,
            string destination,
            Dictionary<IconFileLinks.FileIdentity, string> movedByIdentity,
            ref int linkFailures)
        {
            var hasIdentity = IconFileLinks.TryGetIdentity(file, out var identity, out var linkCount);
            var isLinked = hasIdentity && linkCount > 1;

            if (File.Exists(destination))
            {
                // A run that stopped after copying but before deleting the source left the copy;
                // one that stopped mid-copy left a short file, which is replaced.
                if (SameLength(file, destination))
                {
                    if (isLinked && !movedByIdentity.ContainsKey(identity))
                    {
                        movedByIdentity[identity] = destination;
                    }

                    return TryDeleteWithRetry(file);
                }

                File.Delete(destination);
            }

            if (isLinked && linkFailures < MaxLinkFailures && movedByIdentity.TryGetValue(identity, out var keeper))
            {
                if (IconFileLinks.TryCreateLink(destination, keeper))
                {
                    linkFailures = 0;
                    return TryDeleteWithRetry(file);
                }

                // A volume without hard links refuses every link; a file with too many names
                // refuses more. The moved copy below becomes the group's next keeper.
                linkFailures++;
            }

            var leftBehind = !MoveOrCopy(file, destination);
            if (isLinked)
            {
                movedByIdentity[identity] = destination;
            }

            return !leftBehind;
        }

        // Moves the file, trying again while another program holds it. A file still held is copied
        // when it can be read, and false says the source stayed; one that cannot be read throws.
        private static bool MoveOrCopy(string file, string destination)
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    File.Move(file, destination);
                    return true;
                }
                catch (IOException) when (attempt < LockedRetries && File.Exists(file))
                {
                    if (File.Exists(destination))
                    {
                        TryDeleteFile(destination);
                    }

                    Thread.Sleep(LockedRetryDelayMs);
                }
                catch (IOException) when (File.Exists(file))
                {
                    break;
                }
            }

            File.Copy(file, destination, overwrite: true);
            return TryDeleteWithRetry(file);
        }

        private static bool TryDeleteWithRetry(string file)
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    File.Delete(file);
                    return true;
                }
                catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) && attempt < LockedRetries)
                {
                    Thread.Sleep(LockedRetryDelayMs);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    return false;
                }
            }
        }

        private static bool SameLength(string a, string b)
        {
            try
            {
                return new FileInfo(a).Length == new FileInfo(b).Length;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return false;
            }
        }

        /// <summary>Removes the empty folders under <paramref name="root"/>, deepest first, and the root.</summary>
        internal static void DeleteEmptyFolders(string root)
        {
            if (!Directory.Exists(root))
            {
                return;
            }

            foreach (var folder in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                .OrderByDescending(folder => folder.Length)
                .ToList())
            {
                TryDeleteEmptyFolder(folder);
            }

            TryDeleteEmptyFolder(root);
        }

        private static void TryDeleteEmptyFolder(string folder)
        {
            try
            {
                if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
                {
                    Directory.Delete(folder, recursive: false);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
            }
        }

        private static void TryDeleteFile(string file)
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
            }
        }
    }
}
