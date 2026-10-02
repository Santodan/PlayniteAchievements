using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Playnite.SDK;
using PlayniteAchievements.Models.Settings;

namespace PlayniteAchievements.Services.Showcase
{
    /// <summary>
    /// Managed storage for the images showcase profile widgets show (avatar and background).
    /// A picked file is copied under <c>&lt;PluginUserData&gt;\showcase\images</c> named by the
    /// hash of its bytes, so the widget keeps working when the original moves, two widgets that
    /// pick the same file share one copy, and a duplicated page shares its source's files
    /// safely: a file is deleted only when no profile widget references it (<see cref="Prune"/>).
    /// Content-addressed names never change content, so no bitmap cache eviction is needed.
    /// </summary>
    public sealed class ShowcaseImageStore
    {
        public const string RootFolderName = "showcase";
        public const string ImagesFolderName = "images";

        // Where the previous version kept the layout-wide avatar and background under fixed names.
        private const string LegacyProfileFolderName = "profile";

        private static readonly string[] AcceptedExtensions =
        {
            ".png",
            ".jpg",
            ".jpeg",
            ".bmp",
            ".gif",
            ".webp"
        };

        private readonly string _pluginUserDataPath;
        private readonly ILogger _logger;

        public ShowcaseImageStore(string pluginUserDataPath, ILogger logger = null)
        {
            if (string.IsNullOrWhiteSpace(pluginUserDataPath))
            {
                throw new ArgumentException("Plugin user data path is required.", nameof(pluginUserDataPath));
            }

            _pluginUserDataPath = pluginUserDataPath;
            _logger = logger;
        }

        public string RootDirectory => Path.Combine(_pluginUserDataPath, RootFolderName, ImagesFolderName);

        private string LegacyProfileDirectory => Path.Combine(_pluginUserDataPath, RootFolderName, LegacyProfileFolderName);

        public static bool IsAcceptedExtension(string extension)
        {
            return !string.IsNullOrWhiteSpace(extension) &&
                   AcceptedExtensions.Contains(extension.Trim(), StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>True when the path points at a file inside this store.</summary>
        public bool IsManaged(string path)
        {
            var full = TryGetFullPath(path);
            return full != null && IsUnder(full, RootDirectory);
        }

        /// <summary>
        /// Copies a local image into the store and returns the stored path, or the path itself
        /// when it is already stored. Returns null for a blank, missing, unreadable, or
        /// unsupported file: profile art is optional and must never block a settings save.
        /// </summary>
        public string Import(string sourcePath)
        {
            try
            {
                var source = TryGetFullPath(sourcePath);
                if (source == null || !File.Exists(source))
                {
                    return null;
                }

                if (IsUnder(source, RootDirectory))
                {
                    return source;
                }

                var extension = (Path.GetExtension(source) ?? string.Empty).ToLowerInvariant();
                if (!IsAcceptedExtension(extension))
                {
                    return null;
                }

                Directory.CreateDirectory(RootDirectory);
                var destination = Path.Combine(RootDirectory, ComputeHash(source) + extension);
                if (File.Exists(destination))
                {
                    return destination;
                }

                // Copy beside the destination and move into place, so a torn copy never sits
                // under the name a widget will load.
                var temp = destination + ".tmp-" + Guid.NewGuid().ToString("N");
                try
                {
                    File.Copy(source, temp, overwrite: true);
                    if (File.Exists(destination))
                    {
                        File.Delete(temp);
                    }
                    else
                    {
                        File.Move(temp, destination);
                    }
                }
                finally
                {
                    if (File.Exists(temp))
                    {
                        File.Delete(temp);
                    }
                }

                return destination;
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"Failed to import showcase image '{sourcePath}'.");
                return null;
            }
        }

        /// <summary>
        /// Brings every profile widget's images into the store (paths saved before the store
        /// existed, or copied into the old fixed slots), removes the old slot folder once nothing
        /// points at it, and deletes stored files no widget references. Returns true when any
        /// path in <paramref name="settings"/> changed, so the caller persists.
        /// </summary>
        public bool MigrateAndPrune(ShowcaseSettings settings)
        {
            var changed = false;
            foreach (var profile in EnumerateProfiles(settings))
            {
                var avatar = Rehome(profile.AvatarPath);
                if (avatar != profile.AvatarPath)
                {
                    profile.AvatarPath = avatar;
                    changed = true;
                }

                var background = Rehome(profile.BackgroundPath);
                if (background != profile.BackgroundPath)
                {
                    profile.BackgroundPath = background;
                    changed = true;
                }
            }

            DeleteLegacyProfileFolder(settings);
            Prune(settings);
            return changed;
        }

        /// <summary>Deletes stored files that no profile widget references.</summary>
        public void Prune(ShowcaseSettings settings)
        {
            try
            {
                if (!Directory.Exists(RootDirectory))
                {
                    return;
                }

                var referenced = new HashSet<string>(
                    ReferencedPaths(settings).Select(TryGetFullPath).Where(path => path != null),
                    StringComparer.OrdinalIgnoreCase);
                foreach (var file in Directory.EnumerateFiles(RootDirectory))
                {
                    if (!referenced.Contains(Path.GetFullPath(file)))
                    {
                        File.Delete(file);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Failed to prune showcase images.");
            }
        }

        /// <summary>Every image path the layout's profile widgets hold, blanks removed.</summary>
        public static IEnumerable<string> ReferencedPaths(ShowcaseSettings settings)
        {
            foreach (var profile in EnumerateProfiles(settings))
            {
                if (!string.IsNullOrWhiteSpace(profile.AvatarPath))
                {
                    yield return profile.AvatarPath;
                }

                if (!string.IsNullOrWhiteSpace(profile.BackgroundPath))
                {
                    yield return profile.BackgroundPath;
                }
            }
        }

        private static IEnumerable<ShowcaseProfileSettings> EnumerateProfiles(ShowcaseSettings settings)
        {
            var widgets = (settings?.WidgetInstances ?? Enumerable.Empty<ShowcaseWidgetInstanceSettings>())
                .Concat(settings?.StartPageInstances?.Values ?? Enumerable.Empty<ShowcaseWidgetInstanceSettings>());
            return widgets
                .Where(widget => widget?.Kind == ShowcaseWidgetKind.Profile && widget.Profile != null)
                .Select(widget => widget.Profile);
        }

        // An unmanaged path that still resolves is imported; one that does not is left alone,
        // since the store has nothing to copy and the widget already shows nothing for it.
        private string Rehome(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || IsManaged(path))
            {
                return path;
            }

            return Import(path) ?? path;
        }

        private void DeleteLegacyProfileFolder(ShowcaseSettings settings)
        {
            try
            {
                var legacy = LegacyProfileDirectory;
                if (!Directory.Exists(legacy))
                {
                    return;
                }

                var stillReferenced = ReferencedPaths(settings)
                    .Select(TryGetFullPath)
                    .Any(path => path != null && IsUnder(path, legacy));
                if (!stillReferenced)
                {
                    Directory.Delete(legacy, recursive: true);
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Failed to remove the legacy showcase profile folder.");
            }
        }

        private static string ComputeHash(string path)
        {
            using (var sha = SHA1.Create())
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920))
            {
                var hash = sha.ComputeHash(stream);
                var builder = new System.Text.StringBuilder(hash.Length * 2);
                foreach (var b in hash)
                {
                    builder.Append(b.ToString("x2"));
                }

                return builder.ToString();
            }
        }

        private static string TryGetFullPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            try
            {
                return Path.GetFullPath(path.Trim());
            }
            catch
            {
                return null;
            }
        }

        private static bool IsUnder(string fullPath, string directory)
        {
            var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                       Path.DirectorySeparatorChar;
            return fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
    }
}
