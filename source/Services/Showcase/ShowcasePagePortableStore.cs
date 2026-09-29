using Newtonsoft.Json;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace PlayniteAchievements.Services.Showcase
{
    /// <summary>
    /// A portable showcase page: one page's grid, its widgets, and the column settings of its
    /// grid widgets, wrapped with a <see cref="Kind"/> discriminator so import can reject
    /// foreign files. Carries layout and appearance only: no pin collections, no control-bar
    /// search and filter state, and of a profile card only its background image (name,
    /// subtitle, avatar and links stay with the exporting user).
    /// </summary>
    public sealed class ShowcasePagePortableFile
    {
        /// <summary>
        /// Archive image entries (<c>images/&lt;name&gt;</c>) and the local file each one is read
        /// from on export or was extracted to on import. Not part of the manifest.
        /// </summary>
        [JsonIgnore]
        public Dictionary<string, string> BundledImages { get; set; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The temporary folder <see cref="ShowcasePagePortableStore.Read"/> extracted bundled
        /// images into; the importer deletes it through
        /// <see cref="ShowcasePagePortableStore.DeleteExtractedImages"/> once they are stored.
        /// </summary>
        [JsonIgnore]
        public string ExtractedDirectory { get; set; }

        public const string ShowcasePageKind = "PlayniteAchievements.ShowcasePage";

        public string Kind { get; set; }

        public int Version { get; set; }

        /// <summary><see cref="ShowcaseSettings.CurrentLayoutVersion"/> at export time.</summary>
        public int LayoutVersion { get; set; }

        public ShowcasePageSettings Page { get; set; }

        /// <summary>The widgets the page's blocks reference, keyed back by their exported ids.</summary>
        public List<ShowcaseWidgetInstanceSettings> Widgets { get; set; } =
            new List<ShowcaseWidgetInstanceSettings>();

        /// <summary>Recent achievements grid settings keyed by exported widget instance id.</summary>
        public Dictionary<string, AchievementGridOptions> AchievementGridSurfaces { get; set; } =
            new Dictionary<string, AchievementGridOptions>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Game summaries grid settings keyed by exported widget instance id.</summary>
        public Dictionary<string, GameSummaryGridOptions> GameGridSurfaces { get; set; } =
            new Dictionary<string, GameSummaryGridOptions>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Exports and imports a single showcase page as a <c>.pashowcase</c> zip package holding a
    /// JSON manifest plus an <c>images/</c> folder with the profile backgrounds the page uses.
    /// The <see cref="BuildPortable"/> and <see cref="ApplyPortable"/> transforms are pure over
    /// the settings objects (the manifest refers to images by entry name and the caller supplies
    /// how an extracted file becomes a stored one); <see cref="Write"/> and <see cref="Read"/>
    /// do the IO.
    /// </summary>
    public static class ShowcasePagePortableStore
    {
        public const string PackageFileExtension = ".pashowcase";
        public const string ManifestEntryName = "showcase-page.json";
        public const string ImagesFolderName = "images";

        // Version 2 bundles profile background images and refers to them by archive entry.
        public const int CurrentVersion = 2;

        // Per-user search and filter state written by ShowcaseControlBarStateStore
        // ("ControlBar.Games", "ControlBar.Achievements").
        private const string ControlBarOptionPrefix = "ControlBar.";

        // Ordered longest-first so ".pashowcase.zip" is matched whole.
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

        /// <summary>
        /// Captures the page and the widgets and grid settings it uses, stripped of
        /// user-specific references. Returns null when the page does not exist. Reads without
        /// creating default grid records.
        /// </summary>
        public static ShowcasePagePortableFile BuildPortable(
            ShowcaseSettings settings,
            GridOptionsCatalog gridOptions,
            string pageId)
        {
            var source = settings?.Pages?.FirstOrDefault(page =>
                string.Equals(page?.PageId, pageId, StringComparison.OrdinalIgnoreCase));
            if (source == null)
            {
                return null;
            }

            var page = source.Clone();
            var referenced = new HashSet<string>(
                page.Blocks
                    .Select(block => block.WidgetInstanceId)
                    .Where(id => !string.IsNullOrWhiteSpace(id)),
                StringComparer.OrdinalIgnoreCase);

            var portable = new ShowcasePagePortableFile
            {
                Kind = ShowcasePagePortableFile.ShowcasePageKind,
                Version = CurrentVersion,
                LayoutVersion = ShowcaseSettings.CurrentLayoutVersion,
                Page = page
            };

            foreach (var widget in settings.WidgetInstances ?? new List<ShowcaseWidgetInstanceSettings>())
            {
                if (widget == null || !referenced.Remove(widget.InstanceId ?? string.Empty))
                {
                    continue;
                }

                var copy = widget.Clone();
                StripUserOptions(copy);
                BundleProfileBackground(copy, portable);
                portable.Widgets.Add(copy);
                CaptureGridSurface(gridOptions, copy, portable);
            }

            return portable;
        }

        /// <summary>
        /// Inserts the file's page after <paramref name="insertAfterPageId"/> under fresh ids and
        /// writes its grid settings onto the new widget instances. A profile background the file
        /// bundles is handed to <paramref name="storeImage"/> as the extracted file's path and
        /// replaced by what it returns (null drops the image). The caller persists the result
        /// through the normal save path, which normalizes the page.
        /// </summary>
        public static ShowcasePageSettings ApplyPortable(
            ShowcaseSettings settings,
            GridOptionsCatalog gridOptions,
            ShowcasePagePortableFile portable,
            string insertAfterPageId,
            Func<string, string> storeImage = null)
        {
            Validate(portable);
            return ShowcaseLayoutService.ImportPage(
                settings,
                portable.Page,
                portable.Widgets,
                insertAfterPageId,
                (source, imported) =>
                {
                    RestoreGridSurface(gridOptions, portable, source, imported);
                    RestoreProfileBackground(portable, imported, storeImage);
                });
        }

        public static void Write(string destinationPath, ShowcasePagePortableFile portable)
        {
            if (string.IsNullOrWhiteSpace(destinationPath))
            {
                throw new ArgumentException("Destination path is required.", nameof(destinationPath));
            }

            Validate(portable);
            var directory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Build beside the destination and swap in, so a failed write leaves any
            // existing file intact.
            var tempPath = destinationPath + ".tmp";
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }

            try
            {
                using (var archive = ZipFile.Open(tempPath, ZipArchiveMode.Create))
                {
                    var entry = archive.CreateEntry(ManifestEntryName, CompressionLevel.Optimal);
                    using (var writer = new StreamWriter(entry.Open()))
                    {
                        writer.Write(JsonConvert.SerializeObject(portable, WriteSettings));
                    }

                    foreach (var pair in (portable.BundledImages ?? new Dictionary<string, string>())
                        .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
                    {
                        if (string.IsNullOrWhiteSpace(pair.Value) || !File.Exists(pair.Value))
                        {
                            continue;
                        }

                        var imageEntry = archive.CreateEntry(pair.Key, CompressionLevel.Optimal);
                        using (var source = File.OpenRead(pair.Value))
                        using (var destination = imageEntry.Open())
                        {
                            source.CopyTo(destination);
                        }
                    }
                }

                File.Copy(tempPath, destinationPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
        }

        /// <summary>
        /// Reads the manifest and extracts the bundled images the page's profile widgets refer
        /// to into a temporary folder (<see cref="ShowcasePagePortableFile.ExtractedDirectory"/>),
        /// which the caller removes with <see cref="DeleteExtractedImages"/> after
        /// <see cref="ApplyPortable"/> has stored them.
        /// </summary>
        public static ShowcasePagePortableFile Read(string sourcePath)
        {
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            {
                throw new FileNotFoundException("File not found.", sourcePath);
            }

            ShowcasePagePortableFile portable;
            try
            {
                using (var archive = ZipFile.OpenRead(sourcePath))
                {
                    var entry = archive.Entries.FirstOrDefault(candidate => string.Equals(
                        NormalizeEntryName(candidate.FullName),
                        ManifestEntryName,
                        StringComparison.OrdinalIgnoreCase));
                    if (entry == null)
                    {
                        throw new InvalidOperationException(
                            "This file is not a Playnite Achievements showcase page.");
                    }

                    using (var reader = new StreamReader(entry.Open()))
                    {
                        portable = JsonConvert.DeserializeObject<ShowcasePagePortableFile>(reader.ReadToEnd());
                    }

                    Validate(portable);
                    ExtractBundledImages(archive, portable);
                }
            }
            catch (InvalidDataException ex)
            {
                throw new InvalidOperationException(
                    "This file is not a Playnite Achievements showcase page.", ex);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(
                    "This showcase page file is damaged and could not be read.", ex);
            }

            return portable;
        }

        /// <summary>Removes the folder <see cref="Read"/> extracted bundled images into.</summary>
        public static void DeleteExtractedImages(ShowcasePagePortableFile portable)
        {
            var directory = portable?.ExtractedDirectory;
            if (string.IsNullOrWhiteSpace(directory))
            {
                return;
            }

            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch (IOException)
            {
                // A locked temp file is left for the OS temp cleanup; nothing depends on it.
            }
            catch (UnauthorizedAccessException)
            {
            }
            finally
            {
                portable.ExtractedDirectory = null;
            }
        }

        public static bool IsPackagePath(string path)
        {
            return !string.IsNullOrWhiteSpace(path) &&
                   (path.EndsWith(PackageFileExtension, StringComparison.OrdinalIgnoreCase) ||
                    path.EndsWith(PackageFileExtension + ".zip", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Swaps any recognized suffix for the canonical <c>.pashowcase</c>.</summary>
        public static string NormalizeExportPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return path;
            }

            var value = path.Trim();
            foreach (var suffix in RecognizedFileSuffixes)
            {
                if (value.Length > suffix.Length &&
                    value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    value = value.Substring(0, value.Length - suffix.Length);
                    break;
                }
            }

            return value + PackageFileExtension;
        }

        /// <summary>A file name for the page with characters Windows rejects removed.</summary>
        public static string SuggestFileName(string pageName)
        {
            var invalid = new HashSet<char>(Path.GetInvalidFileNameChars());
            var stem = new string((pageName ?? string.Empty)
                .Where(character => !invalid.Contains(character))
                .ToArray())
                .Trim()
                .TrimEnd('.');
            return (stem.Length == 0 ? "Showcase" : stem) + PackageFileExtension;
        }

        private static void Validate(ShowcasePagePortableFile portable)
        {
            if (portable == null ||
                !string.Equals(portable.Kind, ShowcasePagePortableFile.ShowcasePageKind, StringComparison.Ordinal) ||
                portable.Page == null)
            {
                throw new InvalidOperationException(
                    "This file is not a Playnite Achievements showcase page.");
            }

            if (portable.Version > CurrentVersion)
            {
                throw new InvalidOperationException(
                    "This showcase page was exported by a newer version of Playnite Achievements. Update the extension to import it.");
            }
        }

        private static void StripUserOptions(ShowcaseWidgetInstanceSettings widget)
        {
            ShowcaseWidgetOptions.SetPinCollectionId(widget, null);
            foreach (var key in widget.Options.Keys
                .Where(key => key != null &&
                              key.StartsWith(ControlBarOptionPrefix, StringComparison.OrdinalIgnoreCase))
                .ToList())
            {
                widget.Options.Remove(key);
            }

            // Of the profile card only the background travels; the rest identifies the user.
            if (widget.Profile != null)
            {
                widget.Profile = new ShowcaseProfileSettings
                {
                    BackgroundPath = widget.Profile.BackgroundPath
                };
            }
        }

        // Rewrites the profile background to an archive entry name and records the file it is
        // read from; a background whose file is gone is dropped.
        private static void BundleProfileBackground(
            ShowcaseWidgetInstanceSettings widget,
            ShowcasePagePortableFile portable)
        {
            var profile = widget.Profile;
            if (profile == null)
            {
                return;
            }

            var path = profile.BackgroundPath?.Trim();
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                profile.BackgroundPath = null;
                return;
            }

            // Stored images are named by content, so equal names are the same file; a raw path
            // that happens to share a name with a different file gets a numbered entry.
            var fileName = Path.GetFileName(path);
            var entryName = ImagesFolderName + "/" + fileName;
            for (var suffix = 2;
                 portable.BundledImages.TryGetValue(entryName, out var existing) &&
                 !string.Equals(existing, path, StringComparison.OrdinalIgnoreCase);
                 suffix++)
            {
                entryName = ImagesFolderName + "/" +
                            Path.GetFileNameWithoutExtension(fileName) + "-" + suffix + Path.GetExtension(fileName);
            }

            portable.BundledImages[entryName] = path;
            profile.BackgroundPath = entryName;
        }

        private static void RestoreProfileBackground(
            ShowcasePagePortableFile portable,
            ShowcaseWidgetInstanceSettings imported,
            Func<string, string> storeImage)
        {
            var profile = imported.Profile;
            if (profile == null || string.IsNullOrWhiteSpace(profile.BackgroundPath))
            {
                return;
            }

            // Every background in a portable file names an archive entry; anything else (or an
            // entry the archive did not carry) cannot be shown on this machine.
            string extracted = null;
            var hasEntry = portable.BundledImages != null &&
                           portable.BundledImages.TryGetValue(profile.BackgroundPath.Trim(), out extracted);
            profile.BackgroundPath = hasEntry && !string.IsNullOrWhiteSpace(extracted)
                ? storeImage?.Invoke(extracted)
                : null;
        }

        private static void ExtractBundledImages(ZipArchive archive, ShowcasePagePortableFile portable)
        {
            var wanted = new HashSet<string>(
                (portable.Widgets ?? new List<ShowcaseWidgetInstanceSettings>())
                    .Select(widget => widget?.Profile?.BackgroundPath?.Trim())
                    .Where(name => !string.IsNullOrWhiteSpace(name)),
                StringComparer.OrdinalIgnoreCase);
            if (wanted.Count == 0)
            {
                return;
            }

            foreach (var name in wanted)
            {
                ValidateImageEntryName(name);
            }

            var directory = Path.Combine(
                Path.GetTempPath(),
                "PlayniteAchievements",
                "ShowcasePageImports",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            portable.ExtractedDirectory = directory;

            foreach (var entry in archive.Entries)
            {
                var name = NormalizeEntryName(entry.FullName);
                if (!wanted.Contains(name))
                {
                    continue;
                }

                var target = Path.Combine(directory, name.Substring(ImagesFolderName.Length + 1));
                using (var source = entry.Open())
                using (var destination = File.Create(target))
                {
                    source.CopyTo(destination);
                }

                portable.BundledImages[name] = target;
            }
        }

        private static string NormalizeEntryName(string value)
        {
            return (value ?? string.Empty).Replace('\\', '/').TrimStart('/');
        }

        // Bundled images live flat under images/, so a manifest can never point outside the
        // extraction folder.
        private static void ValidateImageEntryName(string value)
        {
            var normalized = NormalizeEntryName(value);
            var prefix = ImagesFolderName + "/";
            var fileName = normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? normalized.Substring(prefix.Length)
                : null;
            if (string.IsNullOrWhiteSpace(fileName) ||
                fileName.IndexOf('/') >= 0 ||
                fileName.Contains("..") ||
                fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                throw new InvalidOperationException(
                    "This showcase page file is damaged and could not be read.");
            }
        }

        private static void CaptureGridSurface(
            GridOptionsCatalog gridOptions,
            ShowcaseWidgetInstanceSettings widget,
            ShowcasePagePortableFile portable)
        {
            var key = ShowcaseGridSurfaces.ResolveWidgetSurface(widget.Kind, widget.InstanceId);
            if (gridOptions == null || string.IsNullOrWhiteSpace(key))
            {
                return;
            }

            if (ShowcaseGridSurfaces.IsAchievementSurface(key) &&
                gridOptions.Achievement.TryGetValue(key, out var achievement) &&
                achievement != null)
            {
                portable.AchievementGridSurfaces[widget.InstanceId] = achievement.Clone();
            }
            else if (ShowcaseGridSurfaces.IsGameSurface(key) &&
                     gridOptions.GameSummaries.TryGetValue(key, out var games) &&
                     games != null)
            {
                portable.GameGridSurfaces[widget.InstanceId] = games.Clone();
            }
        }

        private static void RestoreGridSurface(
            GridOptionsCatalog gridOptions,
            ShowcasePagePortableFile portable,
            ShowcaseWidgetInstanceSettings source,
            ShowcaseWidgetInstanceSettings imported)
        {
            var key = ShowcaseGridSurfaces.ResolveWidgetSurface(imported.Kind, imported.InstanceId);
            var sourceId = source.InstanceId?.Trim();
            if (gridOptions == null || string.IsNullOrWhiteSpace(key) || string.IsNullOrEmpty(sourceId))
            {
                return;
            }

            if (ShowcaseGridSurfaces.IsAchievementSurface(key) &&
                portable.AchievementGridSurfaces != null &&
                portable.AchievementGridSurfaces.TryGetValue(sourceId, out var achievement))
            {
                gridOptions.SetAchievement(key, achievement);
            }
            else if (ShowcaseGridSurfaces.IsGameSurface(key) &&
                     portable.GameGridSurfaces != null &&
                     portable.GameGridSurfaces.TryGetValue(sourceId, out var games))
            {
                gridOptions.SetGameSummaries(key, games);
            }
        }
    }
}
