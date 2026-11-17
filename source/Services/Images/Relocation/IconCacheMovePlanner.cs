using System;
using System.IO;
using System.Linq;

namespace PlayniteAchievements.Services.Images.Relocation
{
    public enum IconCacheMoveProblem
    {
        None = 0,
        NoTarget,
        SameFolder,
        TargetInsideCache,
        CacheInsideTarget,
        TargetIsDriveRoot,
        TargetNotEmpty,
        TargetUnreachable,
        NotEnoughSpace
    }

    public enum IconCacheMoveStrategy
    {
        /// <summary>Nothing to move: the target is the folder the cache already lives in.</summary>
        RelinkOnly = 0,

        /// <summary>One rename of the whole folder, on the same volume.</summary>
        Rename,

        /// <summary>File by file, across volumes, keeping hard links.</summary>
        PerFile
    }

    /// <summary>What a move will do, or why it cannot.</summary>
    public sealed class IconCacheMovePlan
    {
        public IconCacheMoveProblem Problem { get; set; }

        public IconCacheMoveStrategy Strategy { get; set; }

        /// <summary>The cache folder as the plugin names it, which may be a junction.</summary>
        public string CurrentRoot { get; set; }

        /// <summary>The folder holding the files: the current root, or the junction's target.</summary>
        public string SourceFolder { get; set; }

        /// <summary>A junction or symbolic link to remove once the files moved, or null.</summary>
        public string LinkToRemove { get; set; }

        public string TargetFolder { get; set; }

        public bool IsValid => Problem == IconCacheMoveProblem.None;
    }

    /// <summary>
    /// Checks a requested icon cache move and picks how to do it. File-system facts come in
    /// through <see cref="IconCacheMoveEnvironment"/>, so the rules are testable on their own.
    /// </summary>
    public static class IconCacheMovePlanner
    {
        public static IconCacheMovePlan Plan(string currentRoot, string target, IconCacheMoveEnvironment environment)
        {
            var plan = new IconCacheMovePlan
            {
                CurrentRoot = IconCacheLocation.NormalizeFolder(currentRoot)
            };

            if (string.IsNullOrWhiteSpace(target))
            {
                return Fail(plan, IconCacheMoveProblem.NoTarget);
            }

            plan.TargetFolder = IconCacheLocation.NormalizeFolder(target);

            // A junction's files live in the folder it points at; that folder is what moves.
            var source = plan.CurrentRoot;
            if (environment.IsLink(source))
            {
                var resolved = environment.ResolveLink(source);
                if (!string.IsNullOrWhiteSpace(resolved))
                {
                    plan.LinkToRemove = source;
                    source = IconCacheLocation.NormalizeFolder(resolved);
                }
            }

            plan.SourceFolder = source;

            if (IconCacheLocation.PathsEqual(plan.TargetFolder, plan.CurrentRoot))
            {
                return Fail(plan, IconCacheMoveProblem.SameFolder);
            }

            if (IsDriveRoot(plan.TargetFolder))
            {
                return Fail(plan, IconCacheMoveProblem.TargetIsDriveRoot);
            }

            if (IconCacheLocation.PathsEqual(plan.TargetFolder, source))
            {
                plan.Strategy = IconCacheMoveStrategy.RelinkOnly;
                return plan;
            }

            if (IsInside(plan.TargetFolder, source) || IsInside(plan.TargetFolder, plan.CurrentRoot))
            {
                return Fail(plan, IconCacheMoveProblem.TargetInsideCache);
            }

            if (IsInside(source, plan.TargetFolder) || IsInside(plan.CurrentRoot, plan.TargetFolder))
            {
                return Fail(plan, IconCacheMoveProblem.CacheInsideTarget);
            }

            if (environment.FolderExists(plan.TargetFolder))
            {
                if (!environment.FolderIsEmpty(plan.TargetFolder))
                {
                    return Fail(plan, IconCacheMoveProblem.TargetNotEmpty);
                }
            }

            var targetVolume = environment.VolumeOf(NearestExistingFolder(plan.TargetFolder, environment));
            if (targetVolume == null)
            {
                return Fail(plan, IconCacheMoveProblem.TargetUnreachable);
            }

            var sourceVolume = environment.FolderExists(source) ? environment.VolumeOf(source) : null;
            plan.Strategy = sourceVolume != null && sourceVolume.Value == targetVolume.Value
                ? IconCacheMoveStrategy.Rename
                : IconCacheMoveStrategy.PerFile;
            return plan;
        }

        internal static bool IsInside(string candidate, string folder)
        {
            var normalizedFolder = IconCacheLocation.NormalizeFolder(folder);
            var normalizedCandidate = IconCacheLocation.NormalizeFolder(candidate);
            var prefix = normalizedFolder.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? normalizedFolder
                : normalizedFolder + Path.DirectorySeparatorChar;
            return normalizedCandidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsDriveRoot(string folder)
        {
            var root = Path.GetPathRoot(folder);
            return !string.IsNullOrEmpty(root) &&
                   string.Equals(
                       root.TrimEnd(Path.DirectorySeparatorChar),
                       folder.TrimEnd(Path.DirectorySeparatorChar),
                       StringComparison.OrdinalIgnoreCase);
        }

        private static string NearestExistingFolder(string folder, IconCacheMoveEnvironment environment)
        {
            var current = folder;
            while (!string.IsNullOrEmpty(current) && !environment.FolderExists(current))
            {
                current = Path.GetDirectoryName(current);
            }

            return current;
        }

        private static IconCacheMovePlan Fail(IconCacheMovePlan plan, IconCacheMoveProblem problem)
        {
            plan.Problem = problem;
            return plan;
        }
    }

    /// <summary>The file-system questions the planner asks, answered by the real disk at runtime.</summary>
    public class IconCacheMoveEnvironment
    {
        public virtual bool FolderExists(string folder)
        {
            return Directory.Exists(folder);
        }

        public virtual bool FolderIsEmpty(string folder)
        {
            return !Directory.EnumerateFileSystemEntries(folder).Any();
        }

        public virtual bool IsLink(string folder)
        {
            try
            {
                return Directory.Exists(folder) &&
                       (new DirectoryInfo(folder).Attributes & FileAttributes.ReparsePoint) != 0;
            }
            catch
            {
                return false;
            }
        }

        public virtual string ResolveLink(string folder)
        {
            return IconFileLinks.TryResolveFinalFolder(folder);
        }

        /// <summary>The volume serial number of an existing folder, or null.</summary>
        public virtual uint? VolumeOf(string folder)
        {
            return !string.IsNullOrEmpty(folder) && IconFileLinks.TryGetFolderIdentity(folder, out var identity)
                ? identity.Volume
                : (uint?)null;
        }
    }
}
