using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Playnite.SDK;
using PlayniteAchievements.Models.Settings;

namespace PlayniteAchievements.Services.Images
{
    /// <summary>
    /// The two global achievement icon fallbacks a user can supply from Display settings.
    /// </summary>
    public enum FallbackIconSlot
    {
        Locked = 0,
        Hidden = 1
    }

    /// <summary>
    /// Managed storage for the user's global locked and hidden fallback images. Each slot holds one
    /// file under a fixed stem, materialized from either a local path or an http(s) URL. Mirrors
    /// <see cref="NotificationImageStore"/>: same source-or-URL handling, same fixed-stem layout,
    /// and the same reason for living beside the icon cache rather than inside it.
    /// </summary>
    public sealed class FallbackIconStore
    {
        private const string RootFolderName = "fallback_icons";

        private static readonly Dictionary<FallbackIconSlot, string> SlotStems =
            new Dictionary<FallbackIconSlot, string>
            {
                [FallbackIconSlot.Locked] = "locked",
                [FallbackIconSlot.Hidden] = "hidden"
            };

        private readonly DiskImageService _diskImageService;
        private readonly ILogger _logger;

        public FallbackIconStore(DiskImageService diskImageService, ILogger logger = null)
        {
            _diskImageService = diskImageService ?? throw new ArgumentNullException(nameof(diskImageService));
            _logger = logger;
        }

        /// <summary>Every slot, in a stable order for iteration.</summary>
        public static IReadOnlyList<FallbackIconSlot> Slots { get; } = new[]
        {
            FallbackIconSlot.Locked,
            FallbackIconSlot.Hidden
        };

        /// <summary>
        /// Reads the persisted path for a slot. Keeps the slot-to-property mapping in one place so
        /// materialize, clear, and prune cannot drift from each other.
        /// </summary>
        public static string GetPath(PersistedSettings settings, FallbackIconSlot slot)
        {
            if (settings == null)
            {
                return null;
            }

            switch (slot)
            {
                case FallbackIconSlot.Locked:
                    return settings.LockedFallbackIconPath;
                case FallbackIconSlot.Hidden:
                    return settings.HiddenFallbackIconPath;
                default:
                    return null;
            }
        }

        /// <summary>Writes the persisted path for a slot.</summary>
        public static void SetPath(PersistedSettings settings, FallbackIconSlot slot, string path)
        {
            if (settings == null)
            {
                return;
            }

            switch (slot)
            {
                case FallbackIconSlot.Locked:
                    settings.LockedFallbackIconPath = path;
                    break;
                case FallbackIconSlot.Hidden:
                    settings.HiddenFallbackIconPath = path;
                    break;
            }
        }

        /// <summary>
        /// Copies (or downloads) the source image into managed storage for the slot, replacing any
        /// previous slot image, and returns the resolved absolute path to persist. The original
        /// file format is preserved, so the resulting extension follows the source. Returns null
        /// when the source is blank, missing, or fails to copy.
        /// </summary>
        public async Task<string> MaterializeAsync(
            string sourcePathOrUrl,
            FallbackIconSlot slot,
            CancellationToken cancel)
        {
            if (string.IsNullOrWhiteSpace(sourcePathOrUrl))
            {
                return null;
            }

            sourcePathOrUrl = sourcePathOrUrl.Trim();
            var targetPath = Path.Combine(GetRootDirectory(), SlotStems[slot] + ".png");
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
                // Replacing an image with one of a different format leaves the old file behind
                // under the same stem (the copy may change the target extension to match the
                // source); remove those stale siblings.
                DeleteSlotFiles(slot, exceptPath: resolvedPath);
            }

            return resolvedPath;
        }

        /// <summary>
        /// Deletes the managed image files for a slot (all extensions). The caller is responsible
        /// for nulling the persisted path.
        /// </summary>
        public void DeleteSlot(FallbackIconSlot slot)
        {
            DeleteSlotFiles(slot, exceptPath: null);
        }

        /// <summary>
        /// Deletes managed files for any slot the settings no longer point at. Cancelling the
        /// settings dialog reverts the persisted path but cannot un-copy a file already written
        /// during the edit session, so those files are reclaimed on the next startup.
        /// </summary>
        public void PruneOrphans(PersistedSettings settings)
        {
            if (settings == null)
            {
                return;
            }

            foreach (var slot in Slots)
            {
                var persisted = GetPath(settings, slot);
                if (string.IsNullOrWhiteSpace(persisted))
                {
                    DeleteSlotFiles(slot, exceptPath: null);
                    continue;
                }

                // A path that survived points at exactly one file; drop the other extensions.
                DeleteSlotFiles(slot, exceptPath: persisted);
            }
        }

        /// <summary>The directory holding the managed fallback images.</summary>
        public string GetRootDirectory()
        {
            // The disk image cache root is <PluginUserDataPath>\icon_cache; fallback images live
            // beside it so icon-cache clears and per-game pruning never touch them.
            return Path.Combine(
                Path.GetDirectoryName(_diskImageService.GetCacheDirectoryPath()) ?? string.Empty,
                RootFolderName);
        }

        private void DeleteSlotFiles(FallbackIconSlot slot, string exceptPath)
        {
            try
            {
                var directory = GetRootDirectory();
                if (!Directory.Exists(directory))
                {
                    return;
                }

                foreach (var file in Directory.EnumerateFiles(directory, SlotStems[slot] + ".*"))
                {
                    if (exceptPath == null ||
                        !string.Equals(
                            Path.GetFullPath(file),
                            Path.GetFullPath(exceptPath),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        TryDeleteFile(file);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"Failed to delete fallback icon slot files for {slot}.");
            }
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
                _logger?.Debug(ex, $"Failed to delete fallback icon file: {path}");
            }
        }
    }
}
