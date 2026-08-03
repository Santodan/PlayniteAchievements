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
    public enum NotificationImageOwnerKind
    {
        Global,
        Provider,
        Game
    }

    /// <summary>
    /// Identifies the independent owner of a notification style's managed image slots.
    /// </summary>
    public sealed class NotificationImageOwner
    {
        private NotificationImageOwner(NotificationImageOwnerKind kind, string providerKey, Guid gameId)
        {
            Kind = kind;
            ProviderKey = providerKey;
            GameId = gameId;
        }

        public NotificationImageOwnerKind Kind { get; }

        public string ProviderKey { get; }

        public Guid GameId { get; }

        public static NotificationImageOwner Global { get; } =
            new NotificationImageOwner(NotificationImageOwnerKind.Global, null, Guid.Empty);

        public static NotificationImageOwner ForProvider(string providerKey)
        {
            if (string.IsNullOrWhiteSpace(providerKey))
            {
                return Global;
            }

            return new NotificationImageOwner(
                NotificationImageOwnerKind.Provider,
                providerKey.Trim(),
                Guid.Empty);
        }

        public static NotificationImageOwner ForGame(Guid gameId)
        {
            if (gameId == Guid.Empty)
            {
                throw new ArgumentException("Game ID is required.", nameof(gameId));
            }

            return new NotificationImageOwner(NotificationImageOwnerKind.Game, null, gameId);
        }
    }

    /// <summary>
    /// The user-image slots a notification style can customize: the toast background and the
    /// five badge replacements per surface (toast slots keep the legacy unprefixed names).
    /// </summary>
    public enum NotificationImageSlot
    {
        Background,
        BadgeCommon,
        BadgeUncommon,
        BadgeRare,
        BadgeUltraRare,
        BadgeCompletion,
        FrameBadgeCommon,
        FrameBadgeUncommon,
        FrameBadgeRare,
        FrameBadgeUltraRare,
        FrameBadgeCompletion
    }

    /// <summary>
    /// Manages user-supplied notification images (toast background and badge replacements)
    /// under &lt;PluginUserDataPath&gt;\notification_images. Images are always copied into
    /// managed storage (never referenced in place) with their original bytes and extension
    /// preserved, so animated GIFs stay animated. Global images live in global\; a provider's
    /// whole-style copy owns its own files under providers\&lt;key&gt;\ so later edits to the
    /// global images cannot mutate a customized platform. Per-game snapshots use isolated
    /// games\&lt;game-guid&gt;\ folders.
    /// </summary>
    public sealed class NotificationImageStore
    {
        private const string RootFolderName = "notification_images";
        private const string GlobalFolderName = "global";
        private const string ProvidersFolderName = "providers";
        private const string GamesFolderName = "games";

        private static readonly Dictionary<NotificationImageSlot, string> SlotStems =
            new Dictionary<NotificationImageSlot, string>
            {
                [NotificationImageSlot.Background] = "background",
                [NotificationImageSlot.BadgeCommon] = "badge_common",
                [NotificationImageSlot.BadgeUncommon] = "badge_uncommon",
                [NotificationImageSlot.BadgeRare] = "badge_rare",
                [NotificationImageSlot.BadgeUltraRare] = "badge_ultrarare",
                [NotificationImageSlot.BadgeCompletion] = "badge_completion",
                [NotificationImageSlot.FrameBadgeCommon] = "frame_badge_common",
                [NotificationImageSlot.FrameBadgeUncommon] = "frame_badge_uncommon",
                [NotificationImageSlot.FrameBadgeRare] = "frame_badge_rare",
                [NotificationImageSlot.FrameBadgeUltraRare] = "frame_badge_ultrarare",
                [NotificationImageSlot.FrameBadgeCompletion] = "frame_badge_completion"
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
            return await MaterializeAsync(
                sourcePathOrUrl,
                NotificationImageOwner.ForProvider(providerKeyOrNull),
                slot,
                cancel).ConfigureAwait(false);
        }

        public async Task<string> MaterializeAsync(
            string sourcePathOrUrl,
            NotificationImageOwner owner,
            NotificationImageSlot slot,
            CancellationToken cancel)
        {
            if (string.IsNullOrWhiteSpace(sourcePathOrUrl))
            {
                return null;
            }

            sourcePathOrUrl = sourcePathOrUrl.Trim();
            owner = owner ?? NotificationImageOwner.Global;
            var targetPath = Path.Combine(GetSlotDirectory(owner), SlotStems[slot] + ".png");
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
                DeleteSlotFiles(owner, slot, exceptPath: resolvedPath);
            }

            return resolvedPath;
        }

        /// <summary>
        /// Deletes the managed image files for a slot (all extensions). The caller is
        /// responsible for nulling the persisted path.
        /// </summary>
        public void DeleteSlot(string providerKeyOrNull, NotificationImageSlot slot)
        {
            DeleteSlot(NotificationImageOwner.ForProvider(providerKeyOrNull), slot);
        }

        public void DeleteSlot(NotificationImageOwner owner, NotificationImageSlot slot)
        {
            DeleteSlotFiles(owner ?? NotificationImageOwner.Global, slot, exceptPath: null);
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

        public void DeleteGameImages(Guid gameId)
        {
            if (gameId == Guid.Empty)
            {
                return;
            }

            TryDeleteDirectory(Path.Combine(
                GetRootDirectory(),
                GamesFolderName,
                gameId.ToString("D")));
        }

        /// <summary>
        /// Removes files in one game's slot folder that are no longer referenced by its current
        /// style. A null style removes the whole folder.
        /// </summary>
        public void PruneGameImages(Guid gameId, NotificationStyleSettings style)
        {
            if (gameId == Guid.Empty)
            {
                return;
            }

            if (style == null)
            {
                DeleteGameImages(gameId);
                return;
            }

            try
            {
                var directory = GetSlotDirectory(NotificationImageOwner.ForGame(gameId));
                if (!Directory.Exists(directory))
                {
                    return;
                }

                var referenced = new HashSet<string>(
                    EnumerateStylePaths(style)
                        .Where(path => !string.IsNullOrWhiteSpace(path))
                        .Select(path => Path.GetFullPath(path.Trim())),
                    StringComparer.OrdinalIgnoreCase);
                foreach (var file in Directory.EnumerateFiles(directory))
                {
                    if (!referenced.Contains(Path.GetFullPath(file)))
                    {
                        TryDeleteFile(file);
                    }
                }

                if (!Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    TryDeleteDirectory(directory);
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"Failed to prune notification images for game {gameId}.");
            }
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
            if (string.IsNullOrWhiteSpace(providerKey))
            {
                return;
            }

            await CopyImagesAsync(
                styleCopy,
                NotificationImageOwner.ForProvider(providerKey),
                cancel).ConfigureAwait(false);
        }

        public Task CopyImagesForGameAsync(
            NotificationStyleSettings styleCopy,
            Guid gameId,
            CancellationToken cancel)
        {
            return CopyImagesAsync(styleCopy, NotificationImageOwner.ForGame(gameId), cancel);
        }

        public async Task CopyImagesAsync(
            NotificationStyleSettings styleCopy,
            NotificationImageOwner owner,
            CancellationToken cancel)
        {
            if (styleCopy == null)
            {
                return;
            }

            owner = owner ?? NotificationImageOwner.Global;
            foreach (var slot in NotificationImageSlotMap.Slots)
            {
                NotificationImageSlotMap.SetPath(styleCopy, slot, await MaterializeAsync(
                    NotificationImageSlotMap.GetPath(styleCopy, slot), owner, slot, cancel)
                    .ConfigureAwait(false));
            }
        }

        /// <summary>
        /// Best-effort startup cleanup: removes provider/game folders with no corresponding
        /// style copy and slot files no persisted path references (covers crashes between a
        /// copy and persistence). Omitting game rows preserves game folders for legacy callers.
        /// </summary>
        public void PruneOrphans(
            PersistedSettings settings,
            IEnumerable<GameCustomDataFile> gameCustomData = null)
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

                var gameRows = (gameCustomData ?? Enumerable.Empty<GameCustomDataFile>())
                    .Where(data => data?.PlayniteGameId != Guid.Empty)
                    .ToList();
                var gamesRoot = Path.Combine(GetRootDirectory(), GamesFolderName);
                if (gameCustomData != null && Directory.Exists(gamesRoot))
                {
                    var customizedGames = new HashSet<string>(
                        gameRows
                            .Where(data => data.NotificationAppearanceOverride?.Style != null)
                            .Select(data => data.PlayniteGameId.ToString("D")),
                        StringComparer.OrdinalIgnoreCase);
                    foreach (var directory in Directory.EnumerateDirectories(gamesRoot))
                    {
                        if (!customizedGames.Contains(Path.GetFileName(directory)))
                        {
                            TryDeleteDirectory(directory);
                        }
                    }
                }

                var referenced = new HashSet<string>(
                    CollectReferencedPaths(settings, gameCustomData == null ? null : gameRows),
                    StringComparer.OrdinalIgnoreCase);
                var root = GetRootDirectory();
                if (Directory.Exists(root))
                {
                    foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                    {
                        if (gameCustomData == null &&
                            IsPathUnderDirectory(file, gamesRoot))
                        {
                            continue;
                        }

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

        private static IEnumerable<string> CollectReferencedPaths(
            PersistedSettings settings,
            IEnumerable<GameCustomDataFile> gameCustomData)
        {
            var styles = new List<NotificationStyleSettings> { settings.NotificationStyle };
            styles.AddRange(settings.ProviderNotificationStyles.Values.Where(style => style != null));
            styles.AddRange((gameCustomData ?? Enumerable.Empty<GameCustomDataFile>())
                .Select(data => data?.NotificationAppearanceOverride?.Style)
                .Where(style => style != null));
            foreach (var style in styles)
            {
                foreach (var path in EnumerateStylePaths(style)
                    .Where(p => !string.IsNullOrWhiteSpace(p)))
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

        private void DeleteSlotFiles(
            NotificationImageOwner owner,
            NotificationImageSlot slot,
            string exceptPath)
        {
            try
            {
                var directory = GetSlotDirectory(owner);
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

        private string GetSlotDirectory(NotificationImageOwner owner)
        {
            owner = owner ?? NotificationImageOwner.Global;
            switch (owner.Kind)
            {
                case NotificationImageOwnerKind.Provider:
                    var providerFolder = SanitizeProviderFolderName(owner.ProviderKey);
                    return providerFolder == null
                        ? Path.Combine(GetRootDirectory(), GlobalFolderName)
                        : Path.Combine(GetRootDirectory(), ProvidersFolderName, providerFolder);

                case NotificationImageOwnerKind.Game:
                    if (owner.GameId == Guid.Empty)
                    {
                        throw new InvalidOperationException("Game image owner requires a game ID.");
                    }

                    return Path.Combine(
                        GetRootDirectory(),
                        GamesFolderName,
                        owner.GameId.ToString("D"));

                default:
                    return Path.Combine(GetRootDirectory(), GlobalFolderName);
            }
        }

        private static bool IsPathUnderDirectory(string path, string directory)
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(directory))
            {
                return false;
            }

            var fullDirectory = Path.GetFullPath(directory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            var fullPath = Path.GetFullPath(path);
            return fullPath.StartsWith(fullDirectory, StringComparison.OrdinalIgnoreCase);
        }

        internal static IEnumerable<string> EnumerateStylePaths(NotificationStyleSettings style)
        {
            if (style == null)
            {
                yield break;
            }

            foreach (var slot in NotificationImageSlotMap.Slots)
            {
                yield return NotificationImageSlotMap.GetPath(style, slot);
            }
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
