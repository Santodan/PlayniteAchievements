using Newtonsoft.Json;
using Playnite.SDK;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Images;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

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
    /// Everything a style package carries, read for a preview render: the style with its slot
    /// images extracted to a scratch folder (see
    /// <see cref="NotificationStylePortableStore.ReadForPreview"/>), which parts the package
    /// has, and the raw template XAML for each surface (null when the package carries none).
    /// </summary>
    public sealed class NotificationStylePreviewPackage
    {
        /// <summary>The style with every bundled slot path rewritten to an absolute scratch file.</summary>
        public NotificationStyleSettings Style { get; set; }

        /// <summary>Which optional parts the package carries, as <see cref="NotificationStylePortableStore.InspectPackage"/> reports them.</summary>
        public NotificationStylePackageContents Contents { get; set; }

        /// <summary>The toast template entry's text, or null when the package has none.</summary>
        public string ToastTemplateXaml { get; set; }

        /// <summary>The frame template entry's text, or null when the package has none.</summary>
        public string FrameTemplateXaml { get; set; }
    }

    /// <summary>
    /// Exports and imports notification appearance styles as shareable zip packages that
    /// bundle the style's background and badge images under an <c>images/</c> folder so the
    /// look transfers intact: <c>.panotif</c> carries the toast surface and <c>.paframe</c> the
    /// screenshot frame (flagged in the manifest). A bundle (<c>.pabundle</c>) is how both
    /// travel together. Files with the retired <c>.pastyle</c> extension, which carried both
    /// surfaces, are still read. Reading a package extracts its images to a scratch folder
    /// (<see cref="ReadForPreview"/>); the manifest's own image paths are never trusted, and the
    /// library adapter copies the images it takes into managed storage.
    /// </summary>
    public sealed class NotificationStylePortableStore
    {
        /// <summary>Retired both-surfaces extension; still accepted on import and for presets saved before 4.1.</summary>
        public const string LegacyPackageFileExtension = ".pastyle";
        public const string ToastPackageFileExtension = ".panotif";
        public const string FramePackageFileExtension = ".paframe";
        /// <summary>The manifest entry inside every style package; the name predates the extension split and stays for compatibility.</summary>
        public const string ManifestEntryName = "notification-style.pastyle";

        // Optional full-template XAML entries a package may carry, independently, alongside the
        // data-style manifest. Installed into the plugin-owned custom-template tier on import.
        public const string ToastTemplateEntryName = "template-toast.xaml";
        public const string FrameTemplateEntryName = "template-frame.xaml";

        // v2 added the optional template-toast.xaml / template-frame.xaml entries. v3 made badge
        // images and header texts per-surface (toast keeps the legacy entry stems, the frame gets
        // frame_badge_* entries) with no backwards compatibility for the old shared shape. The
        // Kind discriminator is unchanged. v4 added the optional toast motion fields
        // (EntranceMotion, ExitMotion, MotionFeel, MotionSpeed) and Position; older readers
        // ignore them.
        public const int CurrentVersion = 4;

        private const string ImagesFolderName = "images";

        // Ordered longest-first so ".pastyle.zip" is matched whole instead of being read as
        // ".pastyle" followed by a stray ".zip".
        private static readonly string[] RecognizedFileSuffixes =
        {
            LegacyPackageFileExtension + ".zip",
            ToastPackageFileExtension + ".zip",
            FramePackageFileExtension + ".zip",
            LegacyPackageFileExtension,
            ToastPackageFileExtension,
            FramePackageFileExtension,
            ".zip",
            ".json"
        };

        /// <summary>The extension a surface's package carries.</summary>
        public static string SurfaceExtension(bool isFrame) =>
            isFrame ? FramePackageFileExtension : ToastPackageFileExtension;

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
                [NotificationImageSlot.FrameBadgeCompletion] = "frame_badge_completion",
                [NotificationImageSlot.FrameBackground] = "frame_background"
            };

        // Null omitted (null == "use default" for these fields), but NOT DefaultValueHandling.Ignore:
        // the surface-style booleans default to true, so ignoring default values would silently drop
        // every explicit "false" and the importer would flip it back to true.
        private readonly JsonSerializerSettings _writeSettings = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            NullValueHandling = NullValueHandling.Ignore
        };

        /// <summary>
        /// Writes both surfaces to one package, the shape the retired <c>.pastyle</c> files have.
        /// Nothing in the plugin exports this any more (a bundle carries both surfaces);
        /// it remains so the tests can produce the files the importer must keep reading.
        /// </summary>
        internal void ExportLegacyBothSurfacesPackage(
            NotificationStyleSettings style,
            string destinationPath,
            string toastTemplateXaml = null,
            string frameTemplateXaml = null)
        {
            ExportPackageCore(style, destinationPath, toastTemplateXaml, frameTemplateXaml,
                hasToast: true, hasFrame: true);
        }

        /// <summary>
        /// Writes only the given surface of the style (plus that surface's background image)
        /// to a surface package (<c>.panotif</c>/<c>.paframe</c>, or a preset file). The other surface is left at factory defaults and flagged absent
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

            var pruned = PruneToSurface(style, isFrame);

            // A pack carries the whole look for the surface: the shared style and every kind
            // that has been given its own, each pruned the same way.
            foreach (var pair in style.KindStyles)
            {
                if (pair.Value != null)
                {
                    pruned.KindStyles[pair.Key] = PruneToSurface(pair.Value, isFrame);
                }
            }

            ExportPackageCore(
                pruned,
                destinationPath,
                toastTemplateXaml: isFrame ? null : templateXamlOrNull,
                frameTemplateXaml: isFrame ? templateXamlOrNull : null,
                hasToast: !isFrame,
                hasFrame: isFrame);
        }

        /// <summary>
        /// Installs a pack's surface onto the target style, and the pack's separately styled
        /// kinds along with it, so a pack carries the whole look rather than one surface of it. A
        /// kind the pack does not carry is left as it is rather than being overwritten with the
        /// shared look. The pack's image paths must already point at managed storage (the result
        /// of <see cref="ReadForPreview"/> copied into managed storage).
        /// </summary>
        public static void ApplyPackSurfaces(
            NotificationStyleSettings target,
            NotificationStyleSettings pack,
            bool isFrame)
        {
            if (target == null || pack == null)
            {
                return;
            }

            CopySurface(target, pack, isFrame);
            if (target.KindStyles == null)
            {
                return;
            }

            foreach (var pair in pack.KindStyles)
            {
                if (pair.Value == null ||
                    !Enum.TryParse<NotificationKind>(pair.Key, ignoreCase: true, result: out var kind) ||
                    kind == NotificationKind.Base)
                {
                    continue;
                }

                CopySurface(target.EnableKindStyle(kind), pair.Value, isFrame);
            }
        }

        private static void CopySurface(
            NotificationStyleSettings target,
            NotificationStyleSettings source,
            bool isFrame)
        {
            if (isFrame)
            {
                target.Frame = source.Frame;
                target.FrameBackgroundImagePath = source.FrameBackgroundImagePath;
                return;
            }

            target.Toast = source.Toast;
            target.ToastBackgroundImagePath = source.ToastBackgroundImagePath;
        }

        /// <summary>
        /// A copy of the style holding only the requested surface and that surface's background
        /// path, with no kind styles of its own.
        /// </summary>
        private static NotificationStyleSettings PruneToSurface(
            NotificationStyleSettings style,
            bool isFrame)
        {
            var pruned = new NotificationStyleSettings();
            if (isFrame)
            {
                pruned.Frame = style.Frame.Clone();
                pruned.FrameBackgroundImagePath = style.FrameBackgroundImagePath;
            }
            else
            {
                pruned.Toast = style.Toast.Clone();
                pruned.ToastBackgroundImagePath = style.ToastBackgroundImagePath;
            }

            return pruned;
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
            BundleImages(copy, NotificationKind.Base, imageSources);

            // Each separately styled kind carries its own image overrides under its own entry
            // stems, so a pack restores the whole look rather than one shared set of images.
            foreach (var pair in copy.KindStyles)
            {
                if (pair.Value != null &&
                    NotificationImageStore.TryParseNotificationKind(pair.Key, out var notificationKind))
                {
                    BundleImages(pair.Value, notificationKind, imageSources);
                }
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
                    using (var destination = imageEntry.Open())
                    {
                        PackageImageFormat.CopyPortable(pair.Value, destination);
                    }
                }

                WriteTemplateEntry(archive, ToastTemplateEntryName, toastTemplateXaml);
                WriteTemplateEntry(archive, FrameTemplateEntryName, frameTemplateXaml);
            }
        }

        /// <summary>
        /// Rewrites one style's slot paths to archive entry names and records which local file
        /// each entry comes from. Slots whose file is missing are dropped from the copy.
        /// </summary>
        private static void BundleImages(
            NotificationStyleSettings styleCopy,
            NotificationKind notificationKind,
            IDictionary<string, string> imageSources)
        {
            foreach (var slot in NotificationImageSlotMap.Slots)
            {
                var path = NormalizeText(NotificationImageSlotMap.GetPath(styleCopy, slot));
                if (path == null || !File.Exists(path))
                {
                    NotificationImageSlotMap.SetPath(styleCopy, slot, null);
                    continue;
                }

                var extension = PackageImageFormat.EntryExtension(path, NormalizeImageExtension(Path.GetExtension(path)));
                var entryName = ImagesFolderName + "/" + BuildEntryStem(notificationKind, slot) + extension;
                imageSources[entryName] = path;
                NotificationImageSlotMap.SetPath(styleCopy, slot, entryName);
            }
        }

        /// <summary>
        /// The archive stem for a slot. Kind entries stay flat in the images folder (the kind
        /// is folded into the stem) so the package path guard can keep rejecting every nested
        /// path outright.
        /// </summary>
        private static string BuildEntryStem(NotificationKind notificationKind, NotificationImageSlot slot)
        {
            return notificationKind == NotificationKind.Base
                ? EntryStems[slot]
                : "kind_" + notificationKind.ToString().ToLowerInvariant() + "__" + EntryStems[slot];
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
        /// The style a package carries, read from its manifest alone: no images are
        /// extracted, so bundled image paths stay package-relative. Enough for a preview render
        /// of a preset, a composed theme part, or the parts a package owns; applying a package
        /// reads it through <see cref="ReadForPreview"/>.
        /// </summary>
        public NotificationStyleSettings ReadStyle(string sourcePath)
        {
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            {
                throw new FileNotFoundException("File not found.", sourcePath);
            }

            if (!IsZipContent(sourcePath))
            {
                throw new InvalidOperationException(
                    ResourceProvider.GetString("LOCPlayAch_Settings_Style_ImportNotPackage"));
            }

            using (var archive = ZipFile.OpenRead(sourcePath))
            {
                var manifestEntry = archive.Entries.FirstOrDefault(entry =>
                    string.Equals(NormalizeArchiveEntryName(entry.FullName), ManifestEntryName, StringComparison.OrdinalIgnoreCase));
                if (manifestEntry == null)
                {
                    throw new InvalidOperationException(
                        ResourceProvider.GetString("LOCPlayAch_Settings_Style_ImportMissingManifest"));
                }

                NotificationStylePortableFile portable;
                using (var reader = new StreamReader(manifestEntry.Open()))
                {
                    portable = JsonConvert.DeserializeObject<NotificationStylePortableFile>(reader.ReadToEnd());
                }

                return ExtractStyleOrThrow(portable);
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
                    ResourceProvider.GetString("LOCPlayAch_Settings_Style_ImportUnsupportedFile"));
            }

            if (!IsZipContent(sourcePath))
            {
                throw new InvalidOperationException(
                    ResourceProvider.GetString("LOCPlayAch_Settings_Style_ImportNotPackage"));
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
                        ResourceProvider.GetString("LOCPlayAch_Settings_Style_ImportMissingManifest"));
                }

                // Read the manifest for the surface flags (and to validate the Kind up front).
                NotificationStylePortableFile portable;
                using (var reader = new StreamReader(manifestEntry.Open()))
                {
                    portable = JsonConvert.DeserializeObject<NotificationStylePortableFile>(reader.ReadToEnd());
                }

                ExtractStyleOrThrow(portable);

                return BuildContents(portable, names);
            }
        }

        private static NotificationStylePackageContents BuildContents(
            NotificationStylePortableFile portable,
            IEnumerable<string> normalizedEntryNames)
        {
            var names = normalizedEntryNames.ToList();
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

        /// <summary>
        /// Reads everything a package carries for a preview render: the style with each bundled
        /// slot image extracted into <paramref name="scratchDirectory"/> and its path rewritten to
        /// that absolute file, the package contents, and the template XAML of each surface. Bundled
        /// images go through the traversal and decoder checks;
        /// as there, the manifest's own image paths are ignored and a slot without a bundled image
        /// is left empty. Nothing is written to managed image storage or settings. The caller owns
        /// <paramref name="scratchDirectory"/> and deletes it when the preview ends.
        /// </summary>
        public NotificationStylePreviewPackage ReadForPreview(string packagePath, string scratchDirectory)
        {
            if (string.IsNullOrWhiteSpace(scratchDirectory))
            {
                throw new ArgumentException("Scratch directory is required.", nameof(scratchDirectory));
            }

            using (var archive = PortablePackage.OpenRead(
                packagePath,
                ResourceProvider.GetString("LOCPlayAch_Settings_Style_ImportNotPackage")))
            {
                var entriesByName = PortablePackage.IndexEntries(archive);
                if (!entriesByName.TryGetValue(ManifestEntryName, out var manifestEntry))
                {
                    throw new InvalidOperationException(
                        ResourceProvider.GetString("LOCPlayAch_Settings_Style_ImportMissingManifest"));
                }

                var portable = PortablePackage.ReadJson<NotificationStylePortableFile>(manifestEntry);
                var style = ExtractStyleOrThrow(portable);

                Directory.CreateDirectory(scratchDirectory);
                CopyBundledImagesToScratch(style, NotificationKind.Base, entriesByName, scratchDirectory);
                foreach (var pair in style.KindStyles)
                {
                    if (pair.Value != null &&
                        NotificationImageStore.TryParseNotificationKind(pair.Key, out var notificationKind))
                    {
                        CopyBundledImagesToScratch(pair.Value, notificationKind, entriesByName, scratchDirectory);
                    }
                }

                entriesByName.TryGetValue(ToastTemplateEntryName, out var toastTemplateEntry);
                entriesByName.TryGetValue(FrameTemplateEntryName, out var frameTemplateEntry);

                return new NotificationStylePreviewPackage
                {
                    Style = style,
                    Contents = BuildContents(portable, entriesByName.Keys),
                    ToastTemplateXaml = PortablePackage.ReadText(toastTemplateEntry),
                    FrameTemplateXaml = PortablePackage.ReadText(frameTemplateEntry)
                };
            }
        }

        /// <summary>
        /// Each slot's bundled entry (found and validated by <see cref="FindSlotEntry"/>) is extracted to
        /// <c>&lt;scratchDirectory&gt;\&lt;entry stem&gt;&lt;ext&gt;</c> and the slot points at
        /// that file; a slot without an entry is cleared.
        /// </summary>
        private static void CopyBundledImagesToScratch(
            NotificationStyleSettings style,
            NotificationKind notificationKind,
            IReadOnlyDictionary<string, ZipArchiveEntry> entriesByName,
            string scratchDirectory)
        {
            foreach (var slot in NotificationImageSlotMap.Slots)
            {
                var stem = BuildEntryStem(notificationKind, slot);
                var entry = FindSlotEntry(entriesByName, stem);
                if (entry == null)
                {
                    NotificationImageSlotMap.SetPath(style, slot, null);
                    continue;
                }

                var destination = Path.Combine(
                    scratchDirectory,
                    stem + Path.GetExtension(entry.Name).ToLowerInvariant());
                PortablePackage.ExtractToFile(entry, destination);
                NotificationImageSlotMap.SetPath(style, slot, destination);
            }
        }

        /// <summary>
        /// Reads the embedded template XAML for a surface from a package, or null when the package
        /// carries no template for it. The caller validates and installs it via the resolver.
        /// </summary>
        public string ReadTemplateXaml(string sourcePath, bool isFrame)
        {
            if (!IsPackagePath(sourcePath) || !File.Exists(sourcePath) || !IsZipContent(sourcePath))
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
        /// True when the file actually starts with the zip magic ("PK"). The style extensions are
        /// bare (zip inside, like Playnite's .pext), so the extension alone does not prove the
        /// container: a plain-JSON style from an older build, or the
        /// <see cref="ManifestEntryName"/> manifest a user extracted out of a package, carries a
        /// recognized extension while being unreadable as an archive. Checking here keeps
        /// System.IO.Compression's raw "End of Central Directory record could not be found" out of
        /// the user-facing error.
        /// </summary>
        private static bool IsZipContent(string path)
        {
            try
            {
                using (var stream = File.OpenRead(path))
                {
                    return stream.ReadByte() == 0x50 && stream.ReadByte() == 0x4B;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static bool IsPackagePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            // All style files are canonically bare zip packages (.panotif/.paframe, zip inside
            // like Playnite's .pext); a ".zip"-suffixed rename, the retired .pastyle, or a legacy
            // .pastyle.zip export still imports.
            return path.EndsWith(LegacyPackageFileExtension, StringComparison.OrdinalIgnoreCase) ||
                   path.EndsWith(ToastPackageFileExtension, StringComparison.OrdinalIgnoreCase) ||
                   path.EndsWith(FramePackageFileExtension, StringComparison.OrdinalIgnoreCase) ||
                   path.EndsWith(LegacyPackageFileExtension + ".zip", StringComparison.OrdinalIgnoreCase) ||
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

            return StripRecognizedSuffix(path.Trim()) + extension;
        }

        /// <summary>
        /// Strips whichever recognized style or container suffix <paramref name="value"/> actually
        /// ends with, leaving it unchanged when none matches. Shared by export-path normalization
        /// and preset-name derivation so a legacy <c>.pastyle.zip</c> name is handled identically
        /// by both instead of being truncated to a partial stem.
        /// </summary>
        public static string StripRecognizedSuffix(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return value;
            }

            foreach (var suffix in RecognizedFileSuffixes)
            {
                // Longer than the suffix, so a file named after nothing but an extension keeps a
                // usable stem rather than collapsing to an empty string.
                if (value.Length > suffix.Length &&
                    value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    return value.Substring(0, value.Length - suffix.Length);
                }
            }

            return value;
        }

        private static ZipArchiveEntry FindSlotEntry(
            IReadOnlyDictionary<string, ZipArchiveEntry> entriesByName,
            string stem)
        {
            var prefix = ImagesFolderName + "/" + stem + ".";
            foreach (var pair in entriesByName)
            {
                if (pair.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                    ImageFormats.IsSupportedExtension(Path.GetExtension(pair.Key)))
                {
                    // Guard against traversal / nested paths beyond images/<stem>.<ext>.
                    NormalizePackageImagePathOrThrow(pair.Key);
                    EnsureBundledImageDecodableOrThrow(pair.Key);
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
                    "Destination path must end with .panotif or .paframe.");
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

        /// <summary>
        /// The extension an exported image keeps. Judged against every recognized format rather
        /// than only the decodable ones: an image already configured on this machine must round-trip
        /// with its own bytes and extension, even if the codec that reads it is no longer installed.
        /// </summary>
        private static string NormalizeImageExtension(string extension)
        {
            return ImageFormats.IsSupportedExtension(extension)
                ? extension.Trim().ToLowerInvariant()
                : ".png";
        }

        /// <summary>
        /// Rejects a bundled image this machine could not render. Without this the file would import
        /// cleanly, materialize into managed storage, and then throw when the surface draws it,
        /// because the templates bind the path straight to <c>Image.Source</c>.
        /// </summary>
        private static void EnsureBundledImageDecodableOrThrow(string entryName)
        {
            if (ImageFormats.IsWebpExtension(ImageFormats.GetExtension(entryName)) &&
                !WebpCodecProbe.IsSupported)
            {
                throw new InvalidOperationException(
                    $"The bundled image '{entryName}' is a WebP, which this system has no decoder for. " +
                    "Install the WebP Image Extension from the Microsoft Store, then import again.");
            }

            if (ImageFormats.IsWebmExtension(ImageFormats.GetExtension(entryName)) &&
                !WebmCodecProbe.IsSupported)
            {
                throw new InvalidOperationException(
                    $"The bundled image '{entryName}' is a WebM, which this system has no decoder for. " +
                    "Install the VP9 Video Extensions from the Microsoft Store, then import again.");
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

    }
}
