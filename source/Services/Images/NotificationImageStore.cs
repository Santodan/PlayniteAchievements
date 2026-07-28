using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Playnite.SDK;
using PlayniteAchievements.Models.Settings;

namespace PlayniteAchievements.Services.Images
{
    /// <summary>
    /// The user-image slots a notification style can customize: the toast background and the
    /// five badge replacements.
    /// </summary>
    public enum NotificationImageSlot
    {
        Background,
        BadgeCommon,
        BadgeUncommon,
        BadgeRare,
        BadgeUltraRare,
        BadgeCompletion
    }

    /// <summary>
    /// Manages user-supplied notification images (toast background and badge replacements)
    /// under &lt;PluginUserDataPath&gt;\notification_images. Images are always copied into
    /// managed storage (never referenced in place) with their original bytes and extension
    /// preserved, so animated GIFs stay animated. Global images live in global\; a provider's
    /// whole-style copy owns its own files under providers\&lt;key&gt;\ so later edits to the
    /// global images cannot mutate a customized platform.
    /// </summary>
    public sealed class NotificationImageStore
    {
        private const string RootFolderName = "notification_images";
        private const string GlobalFolderName = "global";
        private const string ProvidersFolderName = "providers";

        private static readonly Dictionary<NotificationImageSlot, string> SlotStems =
            new Dictionary<NotificationImageSlot, string>
            {
                [NotificationImageSlot.Background] = "background",
                [NotificationImageSlot.BadgeCommon] = "badge_common",
                [NotificationImageSlot.BadgeUncommon] = "badge_uncommon",
                [NotificationImageSlot.BadgeRare] = "badge_rare",
                [NotificationImageSlot.BadgeUltraRare] = "badge_ultrarare",
                [NotificationImageSlot.BadgeCompletion] = "badge_completion"
            };

        private readonly DiskImageService _diskImageService;
        private readonly ILogger _logger;

        public NotificationImageStore(DiskImageService diskImageService, ILogger logger = null)
        {
            _diskImageService = diskImageService ?? throw new ArgumentNullException(nameof(diskImageService));
            _logger = logger;
        }

        /// <summary>
        /// Copies (or downloads) the source image into managed storage for the slot,
        /// replacing any previous slot image, and returns the resolved absolute path to
        /// persist. The original file format is preserved, so the resulting extension follows
        /// the source. Returns null when the source is blank, missing, or fails to copy.
        /// </summary>
        public async Task<string> MaterializeAsync(
            string sourcePathOrUrl,
            string providerKeyOrNull,
            NotificationImageSlot slot,
            CancellationToken cancel)
        {
            if (string.IsNullOrWhiteSpace(sourcePathOrUrl))
            {
                return null;
            }

            sourcePathOrUrl = sourcePathOrUrl.Trim();
            var targetPath = Path.Combine(GetSlotDirectory(providerKeyOrNull), SlotStems[slot] + ".png");
            if (string.Equals(sourcePathOrUrl, targetPath, StringComparison.OrdinalIgnoreCase) &&
                File.Exists(targetPath))
            {
                return targetPath;
            }

            string resolvedPath;
            if (IsHttpUrl(sourcePathOrUrl))
            {
                resolvedPath = await _diskImageService
                    .GetOrDownloadIconToPathAsync(
                        sourcePathOrUrl,
                        targetPath,
                        decodeSize: 0,
                        cancel,
                        overwriteExistingTarget: true)
                    .ConfigureAwait(false);
            }
            else if (File.Exists(sourcePathOrUrl))
            {
                resolvedPath = await _diskImageService
                    .GetOrCopyLocalIconToPathAsync(
                        sourcePathOrUrl,
                        targetPath,
                        decodeSize: 0,
                        cancel,
                        overwriteExistingTarget: true)
                    .ConfigureAwait(false);
            }
            else
            {
                return null;
            }

            if (resolvedPath != null)
            {
                // Replacing an image with one of a different format leaves the old file
                // behind under the same stem (the copy may change the target extension to
                // match the source); remove those stale siblings.
                DeleteSlotFiles(providerKeyOrNull, slot, exceptPath: resolvedPath);
            }

            return resolvedPath;
        }

        /// <summary>
        /// Deletes the managed image files for a slot (all extensions). The caller is
        /// responsible for nulling the persisted path.
        /// </summary>
        public void DeleteSlot(string providerKeyOrNull, NotificationImageSlot slot)
        {
            DeleteSlotFiles(providerKeyOrNull, slot, exceptPath: null);
        }

        /// <summary>
        /// Deletes a provider's whole notification-image folder. Used together with
        /// reverting the provider's style copy to the global default.
        /// </summary>
        public void DeleteProviderImages(string providerKey)
        {
            var folder = SanitizeProviderFolderName(providerKey);
            if (folder == null)
            {
                return;
            }

            TryDeleteDirectory(Path.Combine(GetRootDirectory(), ProvidersFolderName, folder));
        }

        /// <summary>
        /// Re-materializes every image referenced by <paramref name="styleCopy"/> into the
        /// provider's own folder and rewrites the copy's paths, so a customized platform owns
        /// its files and later global image edits cannot affect it. Unreadable images are
        /// dropped from the copy.
        /// </summary>
        public async Task CopyImagesForProviderAsync(
            NotificationStyleSettings styleCopy,
            string providerKey,
            CancellationToken cancel)
        {
            if (styleCopy == null || string.IsNullOrWhiteSpace(providerKey))
            {
                return;
            }

            styleCopy.ToastBackgroundImagePath = await MaterializeAsync(
                styleCopy.ToastBackgroundImagePath, providerKey, NotificationImageSlot.Background, cancel)
                .ConfigureAwait(false);

            var badges = styleCopy.BadgeImages;
            badges.CommonPath = await MaterializeAsync(
                badges.CommonPath, providerKey, NotificationImageSlot.BadgeCommon, cancel).ConfigureAwait(false);
            badges.UncommonPath = await MaterializeAsync(
                badges.UncommonPath, providerKey, NotificationImageSlot.BadgeUncommon, cancel).ConfigureAwait(false);
            badges.RarePath = await MaterializeAsync(
                badges.RarePath, providerKey, NotificationImageSlot.BadgeRare, cancel).ConfigureAwait(false);
            badges.UltraRarePath = await MaterializeAsync(
                badges.UltraRarePath, providerKey, NotificationImageSlot.BadgeUltraRare, cancel).ConfigureAwait(false);
            badges.CompletionPath = await MaterializeAsync(
                badges.CompletionPath, providerKey, NotificationImageSlot.BadgeCompletion, cancel).ConfigureAwait(false);
        }

        /// <summary>
        /// Best-effort startup cleanup: removes provider folders with no corresponding style
        /// copy and slot files no persisted path references (covers crashes between a copy
        /// and the settings persist).
        /// </summary>
        public void PruneOrphans(PersistedSettings settings)
        {
            if (settings == null)
            {
                return;
            }

            try
            {
                var providersRoot = Path.Combine(GetRootDirectory(), ProvidersFolderName);
                if (Directory.Exists(providersRoot))
                {
                    var customizedFolders = new HashSet<string>(
                        settings.ProviderNotificationStyles.Keys
                            .Select(SanitizeProviderFolderName)
                            .Where(folder => folder != null),
                        StringComparer.OrdinalIgnoreCase);
                    foreach (var directory in Directory.EnumerateDirectories(providersRoot))
                    {
                        if (!customizedFolders.Contains(Path.GetFileName(directory)))
                        {
                            TryDeleteDirectory(directory);
                        }
                    }
                }

                var referenced = new HashSet<string>(CollectReferencedPaths(settings), StringComparer.OrdinalIgnoreCase);
                var root = GetRootDirectory();
                if (Directory.Exists(root))
                {
                    foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                    {
                        if (!referenced.Contains(Path.GetFullPath(file)))
                        {
                            TryDeleteFile(file);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Failed to prune orphaned notification images.");
            }
        }

        private static IEnumerable<string> CollectReferencedPaths(PersistedSettings settings)
        {
            var styles = new List<NotificationStyleSettings> { settings.NotificationStyle };
            styles.AddRange(settings.ProviderNotificationStyles.Values.Where(style => style != null));
            foreach (var style in styles)
            {
                var paths = new[]
                {
                    style.ToastBackgroundImagePath,
                    style.BadgeImages.CommonPath,
                    style.BadgeImages.UncommonPath,
                    style.BadgeImages.RarePath,
                    style.BadgeImages.UltraRarePath,
                    style.BadgeImages.CompletionPath
                };
                foreach (var path in paths.Where(p => !string.IsNullOrWhiteSpace(p)))
                {
                    string full = null;
                    try
                    {
                        full = Path.GetFullPath(path.Trim());
                    }
                    catch
                    {
                        // Malformed persisted path; nothing to protect.
                    }

                    if (full != null)
                    {
                        yield return full;
                    }
                }
            }
        }

        private void DeleteSlotFiles(string providerKeyOrNull, NotificationImageSlot slot, string exceptPath)
        {
            try
            {
                var directory = GetSlotDirectory(providerKeyOrNull);
                if (!Directory.Exists(directory))
                {
                    return;
                }

                foreach (var file in Directory.EnumerateFiles(directory, SlotStems[slot] + ".*"))
                {
                    if (exceptPath == null ||
                        !string.Equals(Path.GetFullPath(file), Path.GetFullPath(exceptPath), StringComparison.OrdinalIgnoreCase))
                    {
                        TryDeleteFile(file);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"Failed to delete notification image slot files for {slot}.");
            }
        }

        private string GetRootDirectory()
        {
            // The disk image cache root is <PluginUserDataPath>\icon_cache; notification
            // images live beside it so game-cache pruning never touches them.
            return Path.Combine(
                Path.GetDirectoryName(_diskImageService.GetCacheDirectoryPath()) ?? string.Empty,
                RootFolderName);
        }

        private string GetSlotDirectory(string providerKeyOrNull)
        {
            var folder = SanitizeProviderFolderName(providerKeyOrNull);
            return folder == null
                ? Path.Combine(GetRootDirectory(), GlobalFolderName)
                : Path.Combine(GetRootDirectory(), ProvidersFolderName, folder);
        }

        private static string SanitizeProviderFolderName(string providerKey)
        {
            if (string.IsNullOrWhiteSpace(providerKey))
            {
                return null;
            }

            var invalid = Path.GetInvalidFileNameChars();
            var sanitized = new string(providerKey.Trim().Where(c => !invalid.Contains(c)).ToArray());
            return string.IsNullOrWhiteSpace(sanitized) ? null : sanitized.ToLowerInvariant();
        }

        private static bool IsHttpUrl(string value) =>
            value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

        private void TryDeleteFile(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"Failed to delete notification image file: {path}");
            }
        }

        private void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"Failed to delete notification image directory: {path}");
            }
        }
    }
}
