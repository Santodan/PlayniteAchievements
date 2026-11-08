using System;
using System.IO;
using PlayniteAchievements.Services.Images;

namespace PlayniteAchievements.Services.Database
{
    /// <summary>
    /// Maps the file paths the cache database stores to and from absolute paths. A path under the
    /// icon cache is stored in the logical <c>icon_cache\...</c> form, so it follows the cache when
    /// the cache moves; another path under the plugin data folder is stored relative to it; any
    /// other path or URL is stored as given.
    /// </summary>
    internal static class StoredPathMapping
    {
        public static string ToStored(string absolutePath, string pluginDataRoot, string iconCacheRoot)
        {
            if (string.IsNullOrWhiteSpace(absolutePath) ||
                !Path.IsPathRooted(absolutePath) ||
                absolutePath.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                return absolutePath;
            }

            try
            {
                if (!string.IsNullOrWhiteSpace(iconCacheRoot) &&
                    IconCacheLocation.TryMakeLogical(absolutePath, iconCacheRoot, out var logical))
                {
                    return logical;
                }

                if (string.IsNullOrWhiteSpace(pluginDataRoot))
                {
                    return absolutePath;
                }

                var fullBasePath = Path.GetFullPath(pluginDataRoot).TrimEnd(Path.DirectorySeparatorChar);
                var fullPath = Path.GetFullPath(absolutePath);
                if (fullPath.StartsWith(fullBasePath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    return fullPath.Substring(fullBasePath.Length + 1);
                }
            }
            catch
            {
                // Path operations can fail with invalid characters - return original
            }

            return absolutePath;
        }

        public static string FromStored(string storedPath, string pluginDataRoot, string iconCacheRoot)
        {
            if (string.IsNullOrWhiteSpace(storedPath) ||
                Path.IsPathRooted(storedPath) ||
                storedPath.StartsWith("http", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(pluginDataRoot))
            {
                return storedPath;
            }

            try
            {
                return IconCacheLocation.ResolveLogical(
                    storedPath,
                    string.IsNullOrWhiteSpace(iconCacheRoot) ? IconCacheLocation.DefaultRoot(pluginDataRoot) : iconCacheRoot,
                    pluginDataRoot);
            }
            catch
            {
                // Path operations can fail with invalid characters - return original
                return storedPath;
            }
        }
    }
}
