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
    /// foreign files. Carries layout and appearance only: no pin collections, profile data,
    /// or control-bar search and filter state.
    /// </summary>
    public sealed class ShowcasePagePortableFile
    {
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
    /// JSON manifest. The <see cref="BuildPortable"/> and <see cref="ApplyPortable"/> transforms
    /// are pure over the settings objects; <see cref="Write"/> and <see cref="Read"/> do the IO.
    /// An <c>images/</c> folder is reserved for bundling images in a later version.
    /// </summary>
    public static class ShowcasePagePortableStore
    {
        public const string PackageFileExtension = ".pashowcase";
        public const string ManifestEntryName = "showcase-page.json";
        public const int CurrentVersion = 1;

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
                portable.Widgets.Add(copy);
                CaptureGridSurface(gridOptions, copy, portable);
            }

            return portable;
        }

        /// <summary>
        /// Inserts the file's page after <paramref name="insertAfterPageId"/> under fresh ids and
        /// writes its grid settings onto the new widget instances. The caller persists the
        /// result through the normal save path, which normalizes the page.
        /// </summary>
        public static ShowcasePageSettings ApplyPortable(
            ShowcaseSettings settings,
            GridOptionsCatalog gridOptions,
            ShowcasePagePortableFile portable,
            string insertAfterPageId)
        {
            Validate(portable);
            return ShowcaseLayoutService.ImportPage(
                settings,
                portable.Page,
                portable.Widgets,
                insertAfterPageId,
                (source, imported) => RestoreGridSurface(gridOptions, portable, source, imported));
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
                        candidate.FullName.Replace('\\', '/').TrimStart('/'),
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

            Validate(portable);
            return portable;
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
