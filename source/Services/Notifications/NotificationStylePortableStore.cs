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

        public NotificationStyleSettings Style { get; set; }
    }

    /// <summary>
    /// Which optional parts a portable style file carries, so the import UI can offer only what is
    /// actually present (data style, toast template, frame template).
    /// </summary>
    public sealed class NotificationStylePackageContents
    {
        public bool HasStyle { get; set; }

        public bool HasToastTemplate { get; set; }

        public bool HasFrameTemplate { get; set; }
    }

    /// <summary>
    /// Exports and imports a single notification appearance style to a shareable file. Plain
    /// <c>.pastyle</c> is JSON only and cannot carry local images; <c>.pastyle.zip</c> bundles the
    /// style's background and badge images under an <c>images/</c> folder so the look transfers
    /// intact. Import re-materializes bundled images into managed storage via
    /// <see cref="NotificationImageStore"/> so paths are always rewritten to the local machine.
    /// </summary>
    public sealed class NotificationStylePortableStore
    {
        public const string FileExtension = ".pastyle";
        public const string PackageFileExtension = ".pastyle.zip";
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
        /// The fixed set of image slots a style can carry, each paired with the accessors used to
        /// read/write its path on a <see cref="NotificationStyleSettings"/> and the package entry
        /// stem it is bundled under. This is the single source of truth for export and import.
        /// </summary>
        private static readonly IReadOnlyList<ImageSlotBinding> SlotBindings = new[]
        {
            new ImageSlotBinding(
                NotificationImageSlot.Background,
                "background",
                style => style.ToastBackgroundImagePath,
                (style, path) => style.ToastBackgroundImagePath = path),
            new ImageSlotBinding(
                NotificationImageSlot.BadgeCommon,
                "badge_common",
                style => style.Toast.BadgeImages.CommonPath,
                (style, path) => style.Toast.BadgeImages.CommonPath = path),
            new ImageSlotBinding(
                NotificationImageSlot.BadgeUncommon,
                "badge_uncommon",
                style => style.Toast.BadgeImages.UncommonPath,
                (style, path) => style.Toast.BadgeImages.UncommonPath = path),
            new ImageSlotBinding(
                NotificationImageSlot.BadgeRare,
                "badge_rare",
                style => style.Toast.BadgeImages.RarePath,
                (style, path) => style.Toast.BadgeImages.RarePath = path),
            new ImageSlotBinding(
                NotificationImageSlot.BadgeUltraRare,
                "badge_ultrarare",
                style => style.Toast.BadgeImages.UltraRarePath,
                (style, path) => style.Toast.BadgeImages.UltraRarePath = path),
            new ImageSlotBinding(
                NotificationImageSlot.BadgeCompletion,
                "badge_completion",
                style => style.Toast.BadgeImages.CompletionPath,
                (style, path) => style.Toast.BadgeImages.CompletionPath = path),
            new ImageSlotBinding(
                NotificationImageSlot.FrameBadgeCommon,
                "frame_badge_common",
                style => style.Frame.BadgeImages.CommonPath,
                (style, path) => style.Frame.BadgeImages.CommonPath = path),
            new ImageSlotBinding(
                NotificationImageSlot.FrameBadgeUncommon,
                "frame_badge_uncommon",
                style => style.Frame.BadgeImages.UncommonPath,
                (style, path) => style.Frame.BadgeImages.UncommonPath = path),
            new ImageSlotBinding(
                NotificationImageSlot.FrameBadgeRare,
                "frame_badge_rare",
                style => style.Frame.BadgeImages.RarePath,
                (style, path) => style.Frame.BadgeImages.RarePath = path),
            new ImageSlotBinding(
                NotificationImageSlot.FrameBadgeUltraRare,
                "frame_badge_ultrarare",
                style => style.Frame.BadgeImages.UltraRarePath,
                (style, path) => style.Frame.BadgeImages.UltraRarePath = path),
            new ImageSlotBinding(
                NotificationImageSlot.FrameBadgeCompletion,
                "frame_badge_completion",
                style => style.Frame.BadgeImages.CompletionPath,
                (style, path) => style.Frame.BadgeImages.CompletionPath = path),
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
        /// Writes the style to a plain <c>.pastyle</c> JSON file. Styles that reference local
        /// images cannot be represented this way; the caller is directed to use
        /// <see cref="ExportPackage"/> instead.
        /// </summary>
        public void ExportPa(NotificationStyleSettings style, string destinationPath)
        {
            if (style == null)
            {
                throw new ArgumentNullException(nameof(style));
            }

            EnsureFileExtension(destinationPath);

            var copy = style.Clone();
            if (SlotBindings.Any(binding => IsLocalImagePath(binding.GetPath(copy))))
            {
                throw new InvalidOperationException(
                    "This style uses local images, which a plain .PASTYLE cannot store. Use .PASTYLE.ZIP to bundle images.");
            }

            // Any residual image paths are non-local (blank/missing); drop them so the file is clean.
            foreach (var binding in SlotBindings)
            {
                binding.SetPath(copy, null);
            }

            EnsureDestinationDirectory(destinationPath);
            File.WriteAllText(destinationPath, JsonConvert.SerializeObject(BuildPortable(copy), _writeSettings));
        }

        /// <summary>
        /// Writes the style to a <c>.pastyle.zip</c> package, bundling every referenced image under
        /// <c>images/</c> and rewriting the manifest's paths to those relative entry names.
        /// Optionally embeds full-template XAML for the toast and/or frame surfaces (independently)
        /// so a single package can carry the data style, either template, both, or neither template.
        /// </summary>
        public void ExportPackage(
            NotificationStyleSettings style,
            string destinationPath,
            string toastTemplateXaml = null,
            string frameTemplateXaml = null)
        {
            if (style == null)
            {
                throw new ArgumentNullException(nameof(style));
            }

            EnsurePackageExtension(destinationPath);

            var copy = style.Clone();
            var imageSources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var binding in SlotBindings)
            {
                var path = NormalizeText(binding.GetPath(copy));
                if (path == null || !File.Exists(path))
                {
                    binding.SetPath(copy, null);
                    continue;
                }

                var extension = NormalizeImageExtension(Path.GetExtension(path));
                var entryName = ImagesFolderName + "/" + binding.EntryStem + extension;
                imageSources[entryName] = path;
                binding.SetPath(copy, entryName);
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
                    writer.Write(JsonConvert.SerializeObject(BuildPortable(copy), _writeSettings));
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
        /// Reports which optional parts a <c>.pastyle</c>/<c>.pastyle.zip</c> carries so the import
        /// UI can offer only the parts actually present. A plain <c>.pastyle</c> is always
        /// style-only.
        /// </summary>
        public NotificationStylePackageContents InspectPackage(string sourcePath)
        {
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            {
                throw new FileNotFoundException("File not found.", sourcePath);
            }

            if (!IsPackagePath(sourcePath))
            {
                // A plain .pastyle is JSON only; validate the Kind so foreign files are rejected here too.
                var portable = JsonConvert.DeserializeObject<NotificationStylePortableFile>(File.ReadAllText(sourcePath));
                ExtractStyleOrThrow(portable);
                return new NotificationStylePackageContents { HasStyle = true };
            }

            using (var archive = ZipFile.OpenRead(sourcePath))
            {
                var names = archive.Entries
                    .Select(entry => NormalizeArchiveEntryName(entry.FullName))
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .ToList();

                var hasManifest = names.Any(name =>
                    string.Equals(name, ManifestEntryName, StringComparison.OrdinalIgnoreCase));
                if (!hasManifest)
                {
                    throw new InvalidOperationException(
                        "The .PASTYLE.ZIP does not contain a notification style manifest.");
                }

                return new NotificationStylePackageContents
                {
                    HasStyle = true,
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
        /// Reads a <c>.pastyle</c> or <c>.pastyle.zip</c> and returns a ready-to-apply style whose
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

            if (IsPackagePath(sourcePath))
            {
                return await ImportPackageAsync(
                    sourcePath,
                    targetOwner ?? NotificationImageOwner.Global,
                    cancel).ConfigureAwait(false);
            }

            if (!IsFilePath(sourcePath))
            {
                throw new InvalidOperationException("Only .PASTYLE and .PASTYLE.ZIP files are supported.");
            }

            var portable = JsonConvert.DeserializeObject<NotificationStylePortableFile>(File.ReadAllText(sourcePath));
            var style = ExtractStyleOrThrow(portable);

            // A plain file cannot carry images; clear any stray paths from a hand-edited file.
            foreach (var binding in SlotBindings)
            {
                binding.SetPath(style, null);
            }

            return style;
        }

        public static bool IsFilePath(string path)
        {
            return !string.IsNullOrWhiteSpace(path) &&
                   path.EndsWith(FileExtension, StringComparison.OrdinalIgnoreCase) &&
                   !path.EndsWith(PackageFileExtension, StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsPackagePath(string path)
        {
            return !string.IsNullOrWhiteSpace(path) &&
                   path.EndsWith(PackageFileExtension, StringComparison.OrdinalIgnoreCase);
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
            foreach (var suffix in new[] { PackageFileExtension, FileExtension, ".zip", ".json" })
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
                        "The .PASTYLE.ZIP does not contain a notification style manifest.");
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
                    foreach (var binding in SlotBindings)
                    {
                        binding.SetPath(style, await MaterializeBundledSlotAsync(
                            entriesByName, binding, targetOwner, tempRoot, cancel).ConfigureAwait(false));
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
            ImageSlotBinding binding,
            NotificationImageOwner targetOwner,
            string tempRoot,
            CancellationToken cancel)
        {
            var entry = FindSlotEntry(entriesByName, binding.EntryStem);
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
                    .MaterializeAsync(tempPath, targetOwner, binding.Slot, cancel)
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

        private static NotificationStylePortableFile BuildPortable(NotificationStyleSettings style)
        {
            return new NotificationStylePortableFile
            {
                Kind = NotificationStylePortableFile.NotificationStyleKind,
                Version = CurrentVersion,
                Style = style
            };
        }

        private static void EnsureFileExtension(string path)
        {
            if (!IsFilePath(path))
            {
                throw new InvalidOperationException("Destination path must end with .pastyle.");
            }
        }

        private static void EnsurePackageExtension(string path)
        {
            if (!IsPackagePath(path))
            {
                throw new InvalidOperationException("Destination path must end with .pastyle.zip.");
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

        private static bool IsLocalImagePath(string path)
        {
            var normalized = NormalizeText(path);
            return normalized != null &&
                   !normalized.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                   !normalized.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
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

        private sealed class ImageSlotBinding
        {
            public ImageSlotBinding(
                NotificationImageSlot slot,
                string entryStem,
                Func<NotificationStyleSettings, string> getPath,
                Action<NotificationStyleSettings, string> setPath)
            {
                Slot = slot;
                EntryStem = entryStem;
                GetPath = getPath;
                SetPath = setPath;
            }

            public NotificationImageSlot Slot { get; }

            public string EntryStem { get; }

            public Func<NotificationStyleSettings, string> GetPath { get; }

            public Action<NotificationStyleSettings, string> SetPath { get; }
        }
    }
}
