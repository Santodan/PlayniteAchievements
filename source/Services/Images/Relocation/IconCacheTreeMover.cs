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
        /// </summary>
        internal static int MoveTree(
            string source,
            string target,
            Action<int, int> onFile,
            CancellationToken cancel)
        {
            if (!Directory.Exists(source))
            {
                return 0;
            }

            Directory.CreateDirectory(target);
            var sourcePrefix = IconCacheLocation.NormalizeFolder(source) + Path.DirectorySeparatorChar;
            var files = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).ToList();
            var movedByIdentity = new Dictionary<IconFileLinks.FileIdentity, string>();
            var linkFailures = 0;
            var moved = 0;

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

                MoveFile(file, destination, movedByIdentity, ref linkFailures);
                moved++;
                onFile?.Invoke(i + 1, files.Count);
            }

            DeleteEmptyFolders(source);
            return moved;
        }

        private static void MoveFile(
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
                    File.Delete(file);
                    if (isLinked && !movedByIdentity.ContainsKey(identity))
                    {
                        movedByIdentity[identity] = destination;
                    }

                    return;
                }

                File.Delete(destination);
            }

            if (isLinked && linkFailures < MaxLinkFailures && movedByIdentity.TryGetValue(identity, out var keeper))
            {
                if (IconFileLinks.TryCreateLink(destination, keeper))
                {
                    linkFailures = 0;
                    File.Delete(file);
                    return;
                }

                // A volume without hard links refuses every link; a file with too many names
                // refuses more. The moved copy below becomes the group's next keeper.
                linkFailures++;
            }

            File.Move(file, destination);
            if (isLinked)
            {
                movedByIdentity[identity] = destination;
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
