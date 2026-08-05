using Newtonsoft.Json;
using Playnite.SDK;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Images;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Services.Notifications
{
    /// <summary>
    /// A portable notification style file: the appearance <see cref="NotificationStyleSettings"/>
    /// wrapped with a <see cref="Kind"/> discriminator so import can reject foreign files (e.g.
    /// per-game custom data). Image paths on the embedded style are machine-specific and are not
    /// trusted on import; the package's <c>images/</c> entries are the source of truth instead.
    /// </summary>
    public sealed class NotificationStylePortableFile
    {
        /// <summary>Discriminator identifying this as a notification style file.</summary>
        public const string NotificationStyleKind = "PlayniteAchievements.NotificationStyle";

        public string Kind { get; set; }

        public int Version { get; set; }

        /// <summary>
        /// Which surfaces the embedded style actually carries. Both default true so full-style
        /// files (and manifests written before the flags existed) import both surfaces; a
        /// surface package flags only its own surface.
        /// </summary>
        public bool HasToast { get; set; } = true;

        public bool HasFrame { get; set; } = true;

        public NotificationStyleSettings Style { get; set; }
    }

    /// <summary>
    /// Which optional parts a portable style file carries, so the import UI can offer only what is
    /// actually present (per-surface data style, toast template, frame template).
    /// </summary>
    public sealed class NotificationStylePackageContents
    {
        public bool HasStyle { get; set; }

        public bool HasToastStyle { get; set; }

        public bool HasFrameStyle { get; set; }

        public bool HasToastTemplate { get; set; }

        public bool HasFrameTemplate { get; set; }
    }

    /// <summary>
    /// Exports and imports notification appearance styles as shareable zip packages that
    /// bundle the style's background and badge images under an <c>images/</c> folder so the
    /// look transfers intact: <c>.pastyle</c> carries both surfaces, while <c>.panotif</c>
    /// and <c>.paframe</c> carry a single surface (flagged in the manifest). Import
    /// re-materializes bundled images into managed storage via
    /// <see cref="NotificationImageStore"/> so paths are always rewritten to the local machine.
    /// </summary>
    public sealed class NotificationStylePortableStore
    {
        public const string PackageFileExtension = ".pastyle";
        public const string ToastPackageFileExtension = ".panotif";
        public const string FramePackageFileExtension = ".paframe";
        public const string ManifestEntryName = "notification-style.pastyle";

        // Optional full-template XAML entries a package may carry, independently, alongside the
        // data-style manifest. Installed into the plugin-owned custom-template tier on import.
        public const string ToastTemplateEntryName = "template-toast.xaml";
        public const string FrameTemplateEntryName = "template-frame.xaml";

        // v2 added the optional template-toast.xaml / template-frame.xaml entries. v3 made badge
        // images and header texts per-surface (toast keeps the legacy entry stems, the frame gets
        // frame_badge_* entries) with no backwards compatibility for the old shared shape. The
        // Kind discriminator is unchanged.
        public const int CurrentVersion = 3;

        private const string ImagesFolderName = "images";

        /// <summary>
        /// The package entry stem each slot is bundled under (path accessors come from
        /// <see cref="NotificationImageSlotMap"/>). Toast badges keep the legacy unprefixed
        /// stems; frame badges use frame_badge_*.
        /// </summary>
        private static readonly IReadOnlyDictionary<NotificationImageSlot, string> EntryStems =
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

        private readonly NotificationImageStore _imageStore;
        private readonly ILogger _logger;
        // Null omitted (null == "use default" for these fields), but NOT DefaultValueHandling.Ignore:
        // the surface-style booleans default to true, so ignoring default values would silently drop
        // every explicit "false" and the importer would flip it back to true.
        private readonly JsonSerializerSettings _writeSettings = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            NullValueHandling = NullValueHandling.Ignore
        };

        public NotificationStylePortableStore(NotificationImageStore imageStore, ILogger logger = null)
        {
            _imageStore = imageStore ?? throw new ArgumentNullException(nameof(imageStore));
            _logger = logger;
        }

        /// <summary>
        /// Writes the full style (both surfaces) to a <c>.pastyle</c> package, bundling every
        /// referenced image under <c>images/</c> and rewriting the manifest's paths to those
        /// relative entry names. Optionally embeds full-template XAML for the toast and/or frame
        /// surfaces (independently) so a single package can carry the data style, either template,
        /// both, or neither template.
        /// </summary>
        public void ExportPackage(
            NotificationStyleSettings style,
            string destinationPath,
            string toastTemplateXaml = null,
            string frameTemplateXaml = null)
        {
            ExportPackageCore(style, destinationPath, toastTemplateXaml, frameTemplateXaml,
                hasToast: true, hasFrame: true);
        }

        /// <summary>
        /// Writes only the given surface of the style (plus the toast-only background image for
        /// the toast surface) to a surface package (<c>.panotif</c>/<c>.paframe</c>, or a preset file). The other surface is left at factory defaults and flagged absent
        /// in the manifest, so import replaces only the carried surface.
        /// </summary>
        public void ExportSurfacePackage(
            bool isFrame,
            NotificationStyleSettings style,
            string destinationPath,
            string templateXamlOrNull = null)
        {
            if (style == null)
            {
                throw new ArgumentNullException(nameof(style));
            }

            var pruned = new NotificationStyleSettings();
            if (isFrame)
            {
                pruned.Frame = style.Frame.Clone();
            }
            else
            {
                pruned.Toast = style.Toast.Clone();
                pruned.ToastBackgroundImagePath = style.ToastBackgroundImagePath;
            }

            ExportPackageCore(
                pruned,
                destinationPath,
                toastTemplateXaml: isFrame ? null : templateXamlOrNull,
                frameTemplateXaml: isFrame ? templateXamlOrNull : null,
                hasToast: !isFrame,
                hasFrame: isFrame);
        }

        private void ExportPackageCore(
            NotificationStyleSettings style,
            string destinationPath,
            string toastTemplateXaml,
            string frameTemplateXaml,
            bool hasToast,
            bool hasFrame)
        {
            if (style == null)
            {
                throw new ArgumentNullException(nameof(style));
            }

            EnsurePackageExtension(destinationPath);

            var copy = style.Clone();
            var imageSources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var slot in NotificationImageSlotMap.Slots)
            {
                var path = NormalizeText(NotificationImageSlotMap.GetPath(copy, slot));
                if (path == null || !File.Exists(path))
                {
                    NotificationImageSlotMap.SetPath(copy, slot, null);
                    continue;
                }

                var extension = NormalizeImageExtension(Path.GetExtension(path));
                var entryName = ImagesFolderName + "/" + EntryStems[slot] + extension;
                imageSources[entryName] = path;
                NotificationImageSlotMap.SetPath(copy, slot, entryName);
            }

            EnsureDestinationDirectory(destinationPath);
            if (File.Exists(destinationPath))
            {
                File.Delete(destinationPath);
            }

            using (var archive = ZipFile.Open(destinationPath, ZipArchiveMode.Create))
            {
                var manifestEntry = archive.CreateEntry(ManifestEntryName, CompressionLevel.Optimal);
                using (var writer = new StreamWriter(manifestEntry.Open()))
                {
                    writer.Write(JsonConvert.SerializeObject(
                        BuildPortable(copy, hasToast, hasFrame), _writeSettings));
                }

                foreach (var pair in imageSources.OrderBy(a => a.Key, StringComparer.OrdinalIgnoreCase))
                {
                    var imageEntry = archive.CreateEntry(pair.Key, CompressionLevel.Optimal);
                    using (var source = File.OpenRead(pair.Value))
                    using (var destination = imageEntry.Open())
                    {
                        source.CopyTo(destination);
                    }
                }

                WriteTemplateEntry(archive, ToastTemplateEntryName, toastTemplateXaml);
                WriteTemplateEntry(archive, FrameTemplateEntryName, frameTemplateXaml);
            }
        }

        private static void WriteTemplateEntry(ZipArchive archive, string entryName, string xaml)
        {
            if (string.IsNullOrWhiteSpace(xaml))
            {
                return;
            }

            var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
            using (var writer = new StreamWriter(entry.Open()))
            {
                writer.Write(xaml);
            }
        }

        /// <summary>
        /// Reports which optional parts a style package carries so the import UI can offer only
        /// the parts actually present.
        /// </summary>
        public NotificationStylePackageContents InspectPackage(string sourcePath)
        {
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            {
                throw new FileNotFoundException("File not found.", sourcePath);
            }

            if (!IsPackagePath(sourcePath))
            {
                throw new InvalidOperationException(
                    "Only .PASTYLE, .PANOTIF, and .PAFRAME files are supported.");
            }

            using (var archive = ZipFile.OpenRead(sourcePath))
            {
                var names = archive.Entries
                    .Select(entry => NormalizeArchiveEntryName(entry.FullName))
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .ToList();

                var manifestEntry = archive.Entries.FirstOrDefault(entry =>
                    string.Equals(NormalizeArchiveEntryName(entry.FullName), ManifestEntryName, StringComparison.OrdinalIgnoreCase));
                if (manifestEntry == null)
                {
                    throw new InvalidOperationException(
                        "The package does not contain a notification style manifest.");
                }

                // Read the manifest for the surface flags (and to validate the Kind up front).
                NotificationStylePortableFile portable;
                using (var reader = new StreamReader(manifestEntry.Open()))
                {
                    portable = JsonConvert.DeserializeObject<NotificationStylePortableFile>(reader.ReadToEnd());
                }

                ExtractStyleOrThrow(portable);

                return new NotificationStylePackageContents
                {
                    HasStyle = true,
                    HasToastStyle = portable.HasToast,
                    HasFrameStyle = portable.HasFrame,
                    HasToastTemplate = names.Any(name =>
                        string.Equals(name, ToastTemplateEntryName, StringComparison.OrdinalIgnoreCase)),
                    HasFrameTemplate = names.Any(name =>
                        string.Equals(name, FrameTemplateEntryName, StringComparison.OrdinalIgnoreCase))
                };
            }
        }

        /// <summary>
        /// Reads the embedded template XAML for a surface from a package, or null when the package
        /// carries no template for it. The caller validates and installs it via the resolver.
        /// </summary>
        public string ReadTemplateXaml(string sourcePath, bool isFrame)
        {
            if (!IsPackagePath(sourcePath) || !File.Exists(sourcePath))
            {
                return null;
            }

            var entryName = isFrame ? FrameTemplateEntryName : ToastTemplateEntryName;
            using (var archive = ZipFile.OpenRead(sourcePath))
            {
                var entry = archive.Entries.FirstOrDefault(e =>
                    string.Equals(NormalizeArchiveEntryName(e.FullName), entryName, StringComparison.OrdinalIgnoreCase));
                if (entry == null)
                {
                    return null;
                }

                using (var reader = new StreamReader(entry.Open()))
                {
                    return reader.ReadToEnd();
                }
            }
        }

        /// <summary>
        /// Reads a style package and returns a ready-to-apply style whose
        /// image paths point into managed storage for the requested global/provider target.
        /// Bundled images are re-materialized; the manifest's image paths are ignored so
        /// machine-specific paths never leak in.
        /// </summary>
        public async Task<NotificationStyleSettings> ImportAsync(
            string sourcePath,
            string targetProviderKeyOrNull,
            CancellationToken cancel)
        {
            return await ImportAsync(
                sourcePath,
                NotificationImageOwner.ForProvider(targetProviderKeyOrNull),
                cancel).ConfigureAwait(false);
        }

        public async Task<NotificationStyleSettings> ImportAsync(
            string sourcePath,
            NotificationImageOwner targetOwner,
            CancellationToken cancel)
        {
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                throw new ArgumentException("Source path is required.", nameof(sourcePath));
            }

            if (!IsPackagePath(sourcePath))
            {
                throw new InvalidOperationException(
                    "Only .PASTYLE, .PANOTIF, and .PAFRAME files are supported.");
            }

            return await ImportPackageAsync(
                sourcePath,
                targetOwner ?? NotificationImageOwner.Global,
                cancel).ConfigureAwait(false);
        }

        public static bool IsPackagePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            // All style files are canonically bare zip packages (.pastyle/.panotif/.paframe,
            // zip inside like Playnite's .pext); a ".zip"-suffixed rename (or a legacy
            // .pastyle.zip export) still imports.
            return path.EndsWith(PackageFileExtension, StringComparison.OrdinalIgnoreCase) ||
                   path.EndsWith(ToastPackageFileExtension, StringComparison.OrdinalIgnoreCase) ||
                   path.EndsWith(FramePackageFileExtension, StringComparison.OrdinalIgnoreCase) ||
                   path.EndsWith(PackageFileExtension + ".zip", StringComparison.OrdinalIgnoreCase) ||
                   path.EndsWith(ToastPackageFileExtension + ".zip", StringComparison.OrdinalIgnoreCase) ||
                   path.EndsWith(FramePackageFileExtension + ".zip", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Swaps any recognized style suffix on <paramref name="path"/> for
        /// <paramref name="extension"/> so a dialog-chosen name always lands on the canonical
        /// extension (mirrors the per-game custom-data export normalization).
        /// </summary>
        public static string NormalizeExportPath(string path, string extension)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return path;
            }

            var trimmed = path.Trim();
            foreach (var suffix in new[]
                     {
                         PackageFileExtension + ".zip",
                         ToastPackageFileExtension + ".zip",
                         FramePackageFileExtension + ".zip",
                         PackageFileExtension,
                         ToastPackageFileExtension,
                         FramePackageFileExtension,
                         ".zip",
                         ".json"
                     })
            {
                if (trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    trimmed = trimmed.Substring(0, trimmed.Length - suffix.Length);
                    break;
                }
            }

            return trimmed + extension;
        }

        private async Task<NotificationStyleSettings> ImportPackageAsync(
            string sourcePath,
            NotificationImageOwner targetOwner,
            CancellationToken cancel)
        {
            if (!File.Exists(sourcePath))
            {
                throw new FileNotFoundException("Package file not found.", sourcePath);
            }

            using (var archive = ZipFile.OpenRead(sourcePath))
            {
                var entriesByName = archive.Entries
                    .Select(entry => new { Entry = entry, Name = NormalizeArchiveEntryName(entry.FullName) })
                    .Where(item => !string.IsNullOrWhiteSpace(item.Entry.Name) &&
                                   !string.IsNullOrWhiteSpace(item.Name))
                    .GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.First().Entry, StringComparer.OrdinalIgnoreCase);

                if (!entriesByName.TryGetValue(ManifestEntryName, out var manifestEntry))
                {
                    throw new InvalidOperationException(
                        "The package does not contain a notification style manifest.");
                }

                NotificationStylePortableFile portable;
                using (var reader = new StreamReader(manifestEntry.Open()))
                {
                    portable = JsonConvert.DeserializeObject<NotificationStylePortableFile>(reader.ReadToEnd());
                }

                var style = ExtractStyleOrThrow(portable);

                var tempRoot = Path.Combine(Path.GetTempPath(), "PlayniteAchievements", "NotificationStyleImports");
                Directory.CreateDirectory(tempRoot);
                try
                {
                    foreach (var slot in NotificationImageSlotMap.Slots)
                    {
                        NotificationImageSlotMap.SetPath(style, slot, await MaterializeBundledSlotAsync(
                            entriesByName, slot, targetOwner, tempRoot, cancel).ConfigureAwait(false));
                    }
                }
                finally
                {
                    TryDeleteDirectory(tempRoot);
                }

                return style;
            }
        }

        private async Task<string> MaterializeBundledSlotAsync(
            IReadOnlyDictionary<string, ZipArchiveEntry> entriesByName,
            NotificationImageSlot slot,
            NotificationImageOwner targetOwner,
            string tempRoot,
            CancellationToken cancel)
        {
            var entry = FindSlotEntry(entriesByName, EntryStems[slot]);
            if (entry == null)
            {
                return null;
            }

            var tempPath = Path.Combine(tempRoot, Guid.NewGuid().ToString("N") + Path.GetExtension(entry.Name));
            using (var source = entry.Open())
            using (var destination = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                source.CopyTo(destination);
            }

            try
            {
                return await _imageStore
                    .MaterializeAsync(tempPath, targetOwner, slot, cancel)
                    .ConfigureAwait(false);
            }
            finally
            {
                TryDeleteFile(tempPath);
            }
        }

        private static ZipArchiveEntry FindSlotEntry(
            IReadOnlyDictionary<string, ZipArchiveEntry> entriesByName,
            string stem)
        {
            var prefix = ImagesFolderName + "/" + stem + ".";
            foreach (var pair in entriesByName)
            {
                if (pair.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                    IsSupportedImageExtension(Path.GetExtension(pair.Key)))
                {
                    // Guard against traversal / nested paths beyond images/<stem>.<ext>.
                    NormalizePackageImagePathOrThrow(pair.Key);
                    return pair.Value;
                }
            }

            return null;
        }

        private static NotificationStyleSettings ExtractStyleOrThrow(NotificationStylePortableFile portable)
        {
            if (portable == null ||
                !string.Equals(portable.Kind, NotificationStylePortableFile.NotificationStyleKind, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("This file is not a Playnite Achievements notification style.");
            }

            return (portable.Style ?? new NotificationStyleSettings()).Clone();
        }

        private static NotificationStylePortableFile BuildPortable(
            NotificationStyleSettings style,
            bool hasToast = true,
            bool hasFrame = true)
        {
            return new NotificationStylePortableFile
            {
                Kind = NotificationStylePortableFile.NotificationStyleKind,
                Version = CurrentVersion,
                HasToast = hasToast,
                HasFrame = hasFrame,
                Style = style
            };
        }

        private static void EnsurePackageExtension(string path)
        {
            if (!IsPackagePath(path))
            {
                throw new InvalidOperationException(
                    "Destination path must end with .pastyle, .panotif, or .paframe.");
            }
        }

        private static void EnsureDestinationDirectory(string destinationPath)
        {
            var directory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }

        private static string NormalizeImageExtension(string extension)
        {
            return IsSupportedImageExtension(extension) ? extension.Trim().ToLowerInvariant() : ".png";
        }

        private static bool IsSupportedImageExtension(string extension)
        {
            if (string.IsNullOrWhiteSpace(extension))
            {
                return false;
            }

            switch (extension.Trim().ToLowerInvariant())
            {
                case ".png":
                case ".jpg":
                case ".jpeg":
                case ".bmp":
                case ".gif":
                case ".tif":
                case ".tiff":
                case ".webp":
                    return true;
                default:
                    return false;
            }
        }

        private static string NormalizeArchiveEntryName(string value)
        {
            var normalized = NormalizeText(value)?.Replace('\\', '/').TrimStart('/');
            return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
        }

        private static void NormalizePackageImagePathOrThrow(string value)
        {
            var normalized = NormalizeArchiveEntryName(value);
            if (string.IsNullOrWhiteSpace(normalized) ||
                normalized.Contains("..") ||
                !normalized.StartsWith(ImagesFolderName + "/", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Invalid bundled image path '{value}'.");
            }

            var fileName = normalized.Substring((ImagesFolderName + "/").Length);
            if (string.IsNullOrWhiteSpace(fileName) ||
                fileName.Contains("/") ||
                fileName.Contains("\\"))
            {
                throw new InvalidOperationException($"Invalid bundled image path '{value}'.");
            }
        }

        private static string NormalizeText(string value)
        {
            var normalized = (value ?? string.Empty).Trim();
            return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
        }

        private void TryDeleteFile(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"Failed to delete temp notification style image: {path}");
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
                _logger?.Debug(ex, $"Failed to delete temp notification style import directory: {path}");
            }
        }

    }
}
