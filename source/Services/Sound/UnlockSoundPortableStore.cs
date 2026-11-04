using Newtonsoft.Json;
using Playnite.SDK;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.UI;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PlayniteAchievements.Services.Sound
{
    /// <summary>
    /// A portable unlock sound pack: which tiers it carries, each as an archive entry under
    /// <c>sounds/</c>. Tiers it does not carry keep the importing user's current sound.
    /// </summary>
    public sealed class UnlockSoundPackFile
    {
        public const string UnlockSoundsKind = "PlayniteAchievements.UnlockSounds";

        public string Kind { get; set; }

        public int Version { get; set; }

        /// <summary>Tier name (<see cref="UnlockSoundTier"/>) to archive entry name.</summary>
        public Dictionary<string, string> Slots { get; set; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Exports and imports the per-tier unlock sounds as a <c>.pasounds</c> zip package. Export
    /// bundles what the user actually hears for each tier that is not the bundled default (their
    /// own file or the theme's). Import copies the files into plugin-managed storage under
    /// <c>&lt;UserData&gt;\sounds\&lt;pack id&gt;\</c> and points the settings at those copies,
    /// never at the package or the exporter's paths.
    /// </summary>
    public sealed class UnlockSoundPortableStore
    {
        public const string PackageFileExtension = ".pasounds";
        public const string ManifestEntryName = "unlock-sounds.json";
        public const string SoundsFolderName = "sounds";
        public const string ManagedDirectoryName = "sounds";
        public const int CurrentVersion = 1;

        /// <summary>Upper bound per sound file; a notification chime is seconds long.</summary>
        public const long MaxSoundBytes = 16L * 1024 * 1024;

        private static readonly string[] RecognizedFileSuffixes =
        {
            PackageFileExtension + ".zip",
            PackageFileExtension,
            ".zip"
        };

        private static readonly JsonSerializerSettings WriteSettings = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            NullValueHandling = NullValueHandling.Ignore
        };

        private readonly string _managedRoot;
        private readonly ILogger _logger;

        /// <param name="pluginUserDataPath">The plugin's user-data folder; managed copies live under its <c>sounds</c> subfolder.</param>
        public UnlockSoundPortableStore(string pluginUserDataPath, ILogger logger = null)
        {
            _managedRoot = string.IsNullOrWhiteSpace(pluginUserDataPath)
                ? null
                : Path.Combine(pluginUserDataPath, ManagedDirectoryName);
            _logger = logger;
        }

        public string ManagedRoot => _managedRoot;

        public static bool IsPackagePath(string path)
        {
            return !string.IsNullOrWhiteSpace(path) &&
                   (path.EndsWith(PackageFileExtension, StringComparison.OrdinalIgnoreCase) ||
                    path.EndsWith(PackageFileExtension + ".zip", StringComparison.OrdinalIgnoreCase));
        }

        public static string NormalizeExportPath(string path)
        {
            return PortablePackage.NormalizeExportPath(path, PackageFileExtension, RecognizedFileSuffixes);
        }

        public static string BuildFileDialogFilter()
        {
            return $"Playnite Achievements Sound Pack (*{PackageFileExtension})|*{PackageFileExtension};*{PackageFileExtension}.zip";
        }

        /// <summary>
        /// Writes the tiers in <paramref name="resolved"/> whose source is the user's own file or a
        /// theme file. Bundled defaults are left out: every install already has them, and a blank
        /// slot falls through to them on import.
        /// </summary>
        public void Export(IEnumerable<ResolvedUnlockSound> resolved, string destinationPath)
        {
            if (resolved == null)
            {
                throw new ArgumentNullException(nameof(resolved));
            }

            if (!IsPackagePath(destinationPath))
            {
                throw new InvalidOperationException($"Destination path must end with {PackageFileExtension}.");
            }

            var manifest = new UnlockSoundPackFile
            {
                Kind = UnlockSoundPackFile.UnlockSoundsKind,
                Version = CurrentVersion
            };
            var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var sound in resolved.Where(sound => sound != null))
            {
                if (sound.Source != UnlockSoundSource.Custom && sound.Source != UnlockSoundSource.Theme)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(sound.Path) || !File.Exists(sound.Path))
                {
                    continue;
                }

                var extension = Path.GetExtension(sound.Path)?.ToLowerInvariant();
                if (!IsSupportedExtension(extension))
                {
                    continue;
                }

                EnsureAudioContentOrThrow(sound.Path, Path.GetFileName(sound.Path));
                var entryName = SoundsFolderName + "/" + sound.Tier.ToFileBaseName() + extension;
                manifest.Slots[sound.Tier.ToString()] = entryName;
                files[entryName] = sound.Path;
            }

            if (manifest.Slots.Count == 0)
            {
                throw new InvalidOperationException("No custom or theme unlock sounds are set, so there is nothing to export.");
            }

            PortablePackage.Write(destinationPath, archive =>
            {
                PortablePackage.AddJson(archive, ManifestEntryName, manifest, WriteSettings);
                PortablePackage.AddFiles(archive, files);
            });
        }

        /// <summary>Reads the manifest and reports which tiers the package carries.</summary>
        public IReadOnlyList<UnlockSoundTier> Inspect(string sourcePath)
        {
            using (var archive = PortablePackage.OpenRead(sourcePath, NotPackageMessage))
            {
                var entries = PortablePackage.IndexEntries(archive);
                var manifest = ReadManifestOrThrow(entries);
                return ResolveSlots(manifest, entries).Keys.ToList();
            }
        }

        /// <summary>
        /// Copies the package's sounds into a fresh managed folder and points
        /// <paramref name="target"/>'s carried tiers at them. Tiers the package does not carry are
        /// untouched. Returns the tiers that were set.
        /// </summary>
        public IReadOnlyList<UnlockSoundTier> Import(string sourcePath, UnlockSoundSettings target)
        {
            if (target == null)
            {
                throw new ArgumentNullException(nameof(target));
            }

            if (_managedRoot == null)
            {
                throw new InvalidOperationException("No managed sounds directory is configured.");
            }

            using (var archive = PortablePackage.OpenRead(sourcePath, NotPackageMessage))
            {
                var entries = PortablePackage.IndexEntries(archive);
                var manifest = ReadManifestOrThrow(entries);
                var slots = ResolveSlots(manifest, entries);
                if (slots.Count == 0)
                {
                    throw new InvalidOperationException("The sound pack does not contain any sound files.");
                }

                var packDirectory = Path.Combine(_managedRoot, Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(packDirectory);

                var imported = new List<UnlockSoundTier>();
                try
                {
                    foreach (var pair in slots)
                    {
                        var entry = pair.Value;
                        var extension = Path.GetExtension(entry.FullName).ToLowerInvariant();
                        var destination = Path.Combine(packDirectory, pair.Key.ToFileBaseName() + extension);
                        PortablePackage.ExtractToFile(entry, destination);
                        EnsureAudioContentOrThrow(destination, entry.FullName);
                        target.SetPath(pair.Key, destination);
                        imported.Add(pair.Key);
                    }
                }
                catch
                {
                    PortablePackage.TryDeleteDirectory(packDirectory);
                    EmptyFolders.RemoveUpTo(_managedRoot, _managedRoot);
                    throw;
                }

                return imported;
            }
        }

        /// <summary>
        /// Copies one picked sound file into a fresh managed folder and returns the copy's path, so
        /// a tier keeps playing after the original is moved or deleted. The copy keeps the original
        /// file name and passes the same format, size and content checks as <see cref="Import"/>.
        /// A path already in managed storage is returned unchanged.
        /// </summary>
        public string ImportFile(string sourcePath)
        {
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            {
                throw new FileNotFoundException("The sound file does not exist.", sourcePath);
            }

            if (IsManagedPath(sourcePath))
            {
                return sourcePath;
            }

            if (_managedRoot == null)
            {
                throw new InvalidOperationException("No managed sounds directory is configured.");
            }

            var fileName = Path.GetFileName(sourcePath);
            if (!IsSupportedExtension(Path.GetExtension(fileName)))
            {
                throw new InvalidOperationException(
                    $"The sound '{fileName}' is not a supported format (use {string.Join(", ", UnlockSoundResolver.ProbedExtensions)}).");
            }

            if (new FileInfo(sourcePath).Length > MaxSoundBytes)
            {
                throw new InvalidOperationException($"The sound '{fileName}' is larger than {MaxSoundBytes / (1024 * 1024)} MB.");
            }

            var packDirectory = Path.Combine(_managedRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(packDirectory);
            var destination = Path.Combine(packDirectory, fileName);
            try
            {
                File.Copy(sourcePath, destination);
                EnsureAudioContentOrThrow(destination, fileName);
                return destination;
            }
            catch
            {
                PortablePackage.TryDeleteDirectory(packDirectory);
                EmptyFolders.RemoveUpTo(_managedRoot, _managedRoot);
                throw;
            }
        }

        /// <summary>
        /// Extracts the package's sounds into <paramref name="directory"/> for a preview, with the
        /// same manifest, entry and content checks as <see cref="Import"/>, but without copying
        /// into managed storage or touching any settings. Each file is named after its tier and
        /// keeps its original extension. The caller owns <paramref name="directory"/> and deletes
        /// it when the preview ends; on failure the files extracted so far are removed.
        /// </summary>
        public IReadOnlyDictionary<UnlockSoundTier, string> ExtractForPreview(string packagePath, string directory)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                throw new ArgumentException("Directory is required.", nameof(directory));
            }

            using (var archive = PortablePackage.OpenRead(packagePath, NotPackageMessage))
            {
                var entries = PortablePackage.IndexEntries(archive);
                var manifest = ReadManifestOrThrow(entries);
                var slots = ResolveSlots(manifest, entries);
                if (slots.Count == 0)
                {
                    throw new InvalidOperationException("The sound pack does not contain any sound files.");
                }

                Directory.CreateDirectory(directory);

                var extracted = new Dictionary<UnlockSoundTier, string>();
                try
                {
                    foreach (var pair in slots)
                    {
                        var entry = pair.Value;
                        var extension = Path.GetExtension(entry.FullName).ToLowerInvariant();
                        var destination = Path.Combine(directory, pair.Key.ToFileBaseName() + extension);
                        extracted[pair.Key] = destination;
                        PortablePackage.ExtractToFile(entry, destination);
                        EnsureAudioContentOrThrow(destination, entry.FullName);
                    }
                }
                catch
                {
                    foreach (var path in extracted.Values)
                    {
                        TryDeleteFile(path);
                    }

                    throw;
                }

                return extracted;
            }
        }

        /// <summary>
        /// Deletes managed pack folders no slot in <paramref name="settings"/> points into, so a
        /// replaced pack does not leave its files behind.
        /// </summary>
        public void PruneUnreferenced(UnlockSoundSettings settings)
        {
            PruneUnreferenced(new[] { settings });
        }

        /// <summary>
        /// Deletes managed pack folders that no slot of any of <paramref name="settings"/> points
        /// into, for callers that must also keep what another copy of the settings references
        /// (the settings edit snapshot a Cancel restores).
        /// </summary>
        public void PruneUnreferenced(IEnumerable<UnlockSoundSettings> settings)
        {
            if (_managedRoot == null || !Directory.Exists(_managedRoot))
            {
                return;
            }

            var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var sounds in settings ?? Enumerable.Empty<UnlockSoundSettings>())
            {
                foreach (var tier in UnlockSoundTierExtensions.All)
                {
                    var path = sounds?.GetPath(tier);
                    var directory = SafeDirectoryName(path);
                    if (directory != null)
                    {
                        referenced.Add(directory);
                    }
                }
            }

            foreach (var directory in Directory.GetDirectories(_managedRoot))
            {
                if (referenced.Contains(Path.GetFullPath(directory)))
                {
                    continue;
                }

                PortablePackage.TryDeleteDirectory(directory);
            }

            // The folder exists only while a pack points into it; the next import creates it again.
            EmptyFolders.RemoveUpTo(_managedRoot, _managedRoot);
        }

        /// <summary>True when the path points into this store's managed folder.</summary>
        public bool IsManagedPath(string path)
        {
            if (_managedRoot == null || string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            try
            {
                var root = Path.GetFullPath(_managedRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static bool IsSupportedExtension(string extension)
        {
            return !string.IsNullOrWhiteSpace(extension) &&
                   UnlockSoundResolver.ProbedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
        }

        private const string NotPackageMessage =
            "This file is not a sound pack. Import the original .pasounds file, not a file extracted from it.";

        private static UnlockSoundPackFile ReadManifestOrThrow(
            IReadOnlyDictionary<string, System.IO.Compression.ZipArchiveEntry> entries)
        {
            if (!entries.TryGetValue(ManifestEntryName, out var manifestEntry))
            {
                throw new InvalidOperationException("The package does not contain a sound pack manifest.");
            }

            UnlockSoundPackFile manifest;
            try
            {
                manifest = PortablePackage.ReadJson<UnlockSoundPackFile>(manifestEntry);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException("This sound pack file is damaged and could not be read.", ex);
            }

            if (manifest == null ||
                !string.Equals(manifest.Kind, UnlockSoundPackFile.UnlockSoundsKind, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("This file is not a Playnite Achievements sound pack.");
            }

            if (manifest.Version > CurrentVersion)
            {
                throw new InvalidOperationException(
                    "This sound pack was exported by a newer version of Playnite Achievements. Update the extension to import it.");
            }

            return manifest;
        }

        /// <summary>
        /// The tiers the manifest names that are backed by a flat <c>sounds/</c> entry with a
        /// supported extension and an acceptable size. Anything else is rejected rather than
        /// skipped, so a tampered pack fails loudly.
        /// </summary>
        private static Dictionary<UnlockSoundTier, System.IO.Compression.ZipArchiveEntry> ResolveSlots(
            UnlockSoundPackFile manifest,
            IReadOnlyDictionary<string, System.IO.Compression.ZipArchiveEntry> entries)
        {
            var result = new Dictionary<UnlockSoundTier, System.IO.Compression.ZipArchiveEntry>();
            foreach (var pair in manifest.Slots ?? new Dictionary<string, string>())
            {
                if (!Enum.TryParse(pair.Key, ignoreCase: true, out UnlockSoundTier tier))
                {
                    throw new InvalidOperationException($"The sound pack names an unknown tier '{pair.Key}'.");
                }

                var entryName = PortablePackage.NormalizeEntryName(pair.Value);
                PortablePackage.EnsureFlatEntryUnderOrThrow(entryName, SoundsFolderName, "sound");
                if (!IsSupportedExtension(Path.GetExtension(entryName)))
                {
                    throw new InvalidOperationException(
                        $"The sound '{entryName}' is not a supported format (use {string.Join(", ", UnlockSoundResolver.ProbedExtensions)}).");
                }

                if (!entries.TryGetValue(entryName, out var entry))
                {
                    throw new InvalidOperationException($"The sound pack is missing the file '{entryName}'.");
                }

                if (entry.Length > MaxSoundBytes)
                {
                    throw new InvalidOperationException($"The sound '{entryName}' is larger than {MaxSoundBytes / (1024 * 1024)} MB.");
                }

                result[tier] = entry;
            }

            return result;
        }

        /// <summary>
        /// Checks the extracted file's leading bytes against its extension (RIFF/WAVE, fLaC, or an
        /// MP3 frame or ID3 tag), so a renamed executable or script never lands in managed storage.
        /// </summary>
        private static void EnsureAudioContentOrThrow(string path, string entryName)
        {
            byte[] head;
            using (var stream = File.OpenRead(path))
            {
                head = new byte[12];
                var read = stream.Read(head, 0, head.Length);
                if (read < 4)
                {
                    throw new InvalidOperationException($"The sound '{entryName}' is empty.");
                }
            }

            var extension = Path.GetExtension(path).ToLowerInvariant();
            bool ok;
            switch (extension)
            {
                case ".wav":
                    ok = head[0] == (byte)'R' && head[1] == (byte)'I' && head[2] == (byte)'F' && head[3] == (byte)'F' &&
                         head[8] == (byte)'W' && head[9] == (byte)'A' && head[10] == (byte)'V' && head[11] == (byte)'E';
                    break;
                case ".flac":
                    ok = head[0] == (byte)'f' && head[1] == (byte)'L' && head[2] == (byte)'a' && head[3] == (byte)'C';
                    break;
                case ".mp3":
                    ok = (head[0] == (byte)'I' && head[1] == (byte)'D' && head[2] == (byte)'3') ||
                         (head[0] == 0xFF && (head[1] & 0xE0) == 0xE0);
                    break;
                default:
                    ok = false;
                    break;
            }

            if (!ok)
            {
                throw new InvalidOperationException($"The sound '{entryName}' is not a valid {extension} file.");
            }
        }

        private void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"Failed to delete extracted preview sound: {path}");
            }
        }

        private static string SafeDirectoryName(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            try
            {
                var directory = Path.GetDirectoryName(Path.GetFullPath(path));
                return string.IsNullOrWhiteSpace(directory) ? null : directory;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
