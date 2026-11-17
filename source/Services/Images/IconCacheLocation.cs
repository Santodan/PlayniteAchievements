using System;
using System.IO;

namespace PlayniteAchievements.Services.Images
{
    /// <summary>
    /// Raised when the icon cache root moves to another folder.
    /// </summary>
    public sealed class IconCacheRootChangedEventArgs : EventArgs
    {
        public IconCacheRootChangedEventArgs(string oldRoot, string newRoot)
        {
            OldRoot = oldRoot;
            NewRoot = newRoot;
        }

        public string OldRoot { get; }

        public string NewRoot { get; }
    }

    /// <summary>
    /// The one owner of the icon cache folder. The folder is <c>icon_cache</c> in the plugin data
    /// folder unless the user chose another one, in which case the chosen folder is the cache itself.
    ///
    /// Stored paths use <see cref="LogicalPrefix"/> as their first segment to mean "relative to the
    /// current root", whatever the root's own folder name is, so a stored path survives a move.
    /// </summary>
    public sealed class IconCacheLocation
    {
        public const string LogicalPrefix = AchievementIconCachePathBuilder.IconCacheFolderName;

        private static IconCacheLocation _current;

        private readonly object _sync = new object();
        private volatile string _root;
        private volatile bool _isAvailable = true;

        public IconCacheLocation(string pluginDataRoot, string customRoot = null)
        {
            if (string.IsNullOrWhiteSpace(pluginDataRoot))
            {
                throw new ArgumentNullException(nameof(pluginDataRoot));
            }

            PluginDataRoot = NormalizeFolder(pluginDataRoot);
            _root = string.IsNullOrWhiteSpace(customRoot)
                ? DefaultRoot(PluginDataRoot)
                : NormalizeFolder(customRoot);
        }

        /// <summary>
        /// The location the plugin runs with. Null outside the plugin (tests, tools), where path
        /// classification falls back to the <see cref="LogicalPrefix"/> folder name.
        /// </summary>
        public static IconCacheLocation Current => _current;

        public static void SetCurrent(IconCacheLocation location)
        {
            _current = location;
        }

        /// <summary>
        /// The current icon cache folder, or the default one under <paramref name="pluginDataRoot"/>
        /// outside the plugin. For code that only has the plugin data path at hand.
        /// </summary>
        public static string CurrentRootOrDefault(string pluginDataRoot)
        {
            return _current?.Root ?? DefaultRoot(pluginDataRoot);
        }

        /// <summary>
        /// False while the current icon cache folder takes no writes: its custom folder was missing
        /// at startup. Code that writes into the cache itself rather than through the disk image
        /// service checks this first.
        /// </summary>
        public static bool CurrentAcceptsWrites => _current?.IsAvailable ?? true;

        public event EventHandler<IconCacheRootChangedEventArgs> RootChanged;

        public string PluginDataRoot { get; }

        /// <summary>The absolute icon cache folder, without a trailing separator.</summary>
        public string Root => _root;

        public string DefaultRootPath => DefaultRoot(PluginDataRoot);

        public bool IsCustom => !PathsEqual(_root, DefaultRootPath);

        /// <summary>
        /// False for a session that started with a custom root that does not exist. Nothing is
        /// written to the cache while false, so no icons land on the wrong drive.
        /// </summary>
        public bool IsAvailable => _isAvailable;

        public static string DefaultRoot(string pluginDataRoot)
        {
            return Path.Combine(NormalizeFolder(pluginDataRoot), LogicalPrefix);
        }

        public void MarkUnavailable()
        {
            _isAvailable = false;
        }

        /// <summary>
        /// Points the cache at a new folder and tells subscribers. The caller has already moved the
        /// files and rewritten the stored paths.
        /// </summary>
        public void ChangeRoot(string newRoot)
        {
            string oldRoot;
            lock (_sync)
            {
                oldRoot = _root;
                _root = string.IsNullOrWhiteSpace(newRoot) ? DefaultRootPath : NormalizeFolder(newRoot);
                _isAvailable = true;
            }

            if (!PathsEqual(oldRoot, _root))
            {
                RootChanged?.Invoke(this, new IconCacheRootChangedEventArgs(oldRoot, _root));
            }
        }

        /// <summary>
        /// The absolute path of a stored path. A path starting with <see cref="LogicalPrefix"/> is
        /// placed under the current root; any other relative path is placed under the plugin data
        /// folder; an absolute path is returned as is.
        /// </summary>
        public string ResolveLogical(string path)
        {
            return ResolveLogical(path, _root, PluginDataRoot);
        }

        public bool TryMakeLogical(string absolutePath, out string logicalPath)
        {
            return TryMakeLogical(absolutePath, _root, out logicalPath);
        }

        public bool IsUnderRoot(string absolutePath)
        {
            return TryMakeLogical(absolutePath, out _);
        }

        internal static string ResolveLogical(string path, string root, string pluginDataRoot)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return path;
            }

            var trimmed = path.Trim();
            if (TryStripLogicalPrefix(trimmed, out var rest))
            {
                return rest.Length == 0 ? root : Path.Combine(root, rest);
            }

            if (IsRooted(trimmed))
            {
                return trimmed;
            }

            return Path.Combine(pluginDataRoot, trimmed);
        }

        internal static bool TryMakeLogical(string absolutePath, string root, out string logicalPath)
        {
            logicalPath = null;
            if (string.IsNullOrWhiteSpace(absolutePath) || string.IsNullOrWhiteSpace(root))
            {
                return false;
            }

            var trimmed = absolutePath.Trim();
            if (!IsRooted(trimmed))
            {
                if (TryStripLogicalPrefix(trimmed, out var rest))
                {
                    logicalPath = rest.Length == 0 ? LogicalPrefix : Path.Combine(LogicalPrefix, rest);
                    return true;
                }

                return false;
            }

            string full;
            try
            {
                full = Path.GetFullPath(trimmed);
            }
            catch
            {
                return false;
            }

            var normalizedRoot = NormalizeFolder(root);
            if (string.Equals(full.TrimEnd(Path.DirectorySeparatorChar), normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                logicalPath = LogicalPrefix;
                return true;
            }

            var prefix = normalizedRoot.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? normalizedRoot
                : normalizedRoot + Path.DirectorySeparatorChar;
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            logicalPath = Path.Combine(LogicalPrefix, full.Substring(prefix.Length));
            return true;
        }

        /// <summary>
        /// Rewrites a path under <paramref name="oldRoot"/> to the same place under
        /// <paramref name="newRoot"/>. Returns false, leaving the value alone, for any other path.
        /// </summary>
        internal static bool TryRebase(string path, string oldRoot, string newRoot, out string rebased)
        {
            rebased = path;
            if (string.IsNullOrWhiteSpace(path) || !IsRooted(path.Trim()))
            {
                return false;
            }

            if (!TryMakeLogical(path, oldRoot, out var logical))
            {
                return false;
            }

            rebased = ResolveLogical(logical, NormalizeFolder(newRoot), NormalizeFolder(newRoot));
            return !string.Equals(rebased, path, StringComparison.Ordinal);
        }

        internal static bool TryStripLogicalPrefix(string relativePath, out string rest)
        {
            rest = null;
            if (string.IsNullOrEmpty(relativePath) || IsRooted(relativePath))
            {
                return false;
            }

            var normalized = relativePath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            if (string.Equals(normalized.TrimEnd(Path.DirectorySeparatorChar), LogicalPrefix, StringComparison.OrdinalIgnoreCase))
            {
                rest = string.Empty;
                return true;
            }

            var prefix = LogicalPrefix + Path.DirectorySeparatorChar;
            if (!normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            rest = normalized.Substring(prefix.Length);
            return true;
        }

        internal static string NormalizeFolder(string folder)
        {
            var trimmed = (folder ?? string.Empty).Trim();
            try
            {
                trimmed = Path.GetFullPath(trimmed);
            }
            catch
            {
            }

            var withoutTrailing = trimmed.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var root = IsRooted(trimmed) ? Path.GetPathRoot(trimmed) ?? string.Empty : string.Empty;
            return withoutTrailing.Length < root.TrimEnd(Path.DirectorySeparatorChar).Length + 1 && root.Length > 0
                ? root
                : withoutTrailing;
        }

        // Path.IsPathRooted throws on characters a path cannot hold, such as a quote in a note.
        // Stored text that is not a path is never rooted.
        internal static bool IsRooted(string value)
        {
            if (string.IsNullOrEmpty(value) || value.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            {
                return false;
            }

            return Path.IsPathRooted(value);
        }

        internal static bool PathsEqual(string a, string b)
        {
            return string.Equals(NormalizeFolder(a), NormalizeFolder(b), StringComparison.OrdinalIgnoreCase);
        }
    }
}
