using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.Images;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace PlayniteAchievements.Services.GameCustomData
{
    /// <summary>
    /// The custom-achievements form of the .pa package: a zip holding one CSV of custom
    /// achievement definitions (<see cref="CustomAchievementCsvFormat"/>) and, when any
    /// definition has an icon, the bundled icon files under the package images folder. It has
    /// no <see cref="PortablePackageManifestEntryName"/> entry, which is what tells it apart from
    /// a whole-game package. A template is the same package with a header-only CSV. Neither
    /// direction carries unlock state or progress (<see cref="PortablePersonalState"/>).
    /// </summary>
    public sealed partial class GameCustomDataStore
    {
        public const string CustomAchievementsPackageCsvEntryName = "custom-achievements.csv";

        /// <summary>
        /// Whether a .pa package carries custom achievements only (the CSV and no manifest), and
        /// so merges into the editor instead of replacing the game's custom data.
        /// </summary>
        public bool IsCustomAchievementsPackage(string sourcePath)
        {
            if (!File.Exists(sourcePath))
            {
                throw new FileNotFoundException("Package file not found.", sourcePath);
            }

            using (var archive = ZipFile.OpenRead(sourcePath))
            {
                return IsCustomAchievementsPackage(
                    archive.Entries.Select(entry => NormalizeArchiveEntryName(entry.FullName)));
            }
        }

        private static bool IsCustomAchievementsPackage(IEnumerable<string> entryNames)
        {
            var names = new HashSet<string>(
                entryNames.Where(name => !string.IsNullOrWhiteSpace(name)),
                StringComparer.OrdinalIgnoreCase);
            return !names.Contains(PortablePackageManifestEntryName) &&
                   names.Contains(CustomAchievementsPackageCsvEntryName);
        }

        public void ExportCustomAchievementsPackage(
            Guid playniteGameId,
            IReadOnlyList<CustomAchievementDefinition> definitions,
            string destinationPath)
        {
            EnsurePortablePackageExtension(destinationPath);

            var clones = (definitions ?? Array.Empty<CustomAchievementDefinition>())
                .Where(definition => definition != null)
                .Select(definition => definition.Clone())
                .ToList();
            clones.ForEach(PortablePersonalState.Strip);
            var fileStems = AchievementIconCachePathBuilder.BuildFileStems(
                clones.Select(definition => CustomAchievementProjectionService.BuildApiName(definition.Id)));
            var imageSources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            RewritePortableCustomAchievementIconsForPackage(playniteGameId, clones, fileStems, imageSources);

            EnsureDestinationDirectory(destinationPath);
            if (File.Exists(destinationPath))
            {
                File.Delete(destinationPath);
            }

            using (var archive = ZipFile.Open(destinationPath, ZipArchiveMode.Create))
            {
                var csvEntry = archive.CreateEntry(CustomAchievementsPackageCsvEntryName, CompressionLevel.Optimal);
                using (var writer = new StreamWriter(csvEntry.Open()))
                {
                    foreach (var line in CustomAchievementCsvFormat.BuildLines(clones))
                    {
                        writer.WriteLine(line);
                    }
                }

                foreach (var pair in imageSources.OrderBy(a => a.Key, StringComparer.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrWhiteSpace(pair.Value) || !File.Exists(pair.Value))
                    {
                        throw new InvalidOperationException($"Missing bundled icon file: {pair.Value ?? pair.Key}");
                    }

                    var imageEntry = archive.CreateEntry(pair.Key, CompressionLevel.Optimal);
                    using (var source = File.OpenRead(pair.Value))
                    using (var destination = imageEntry.Open())
                    {
                        source.CopyTo(destination);
                    }
                }
            }
        }

        /// <summary>
        /// Reads the package CSV and copies any bundled icons into this game's managed custom
        /// icon folder, returning definitions whose icon paths point at those managed files.
        /// Icon values that are URLs or rooted local paths pass through unchanged.
        /// </summary>
        public CustomAchievementTextImportResult ImportCustomAchievementsPackage(Guid playniteGameId, string sourcePath)
        {
            if (!File.Exists(sourcePath))
            {
                throw new FileNotFoundException("Package file not found.", sourcePath);
            }

            using (var archive = ZipFile.OpenRead(sourcePath))
            {
                var entriesByName = archive.Entries
                    .Select(entry => new { Entry = entry, Name = NormalizeArchiveEntryName(entry.FullName) })
                    .Where(item => !string.IsNullOrWhiteSpace(item.Entry.Name) && !string.IsNullOrWhiteSpace(item.Name))
                    .GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.First().Entry, StringComparer.OrdinalIgnoreCase);

                if (!entriesByName.TryGetValue(CustomAchievementsPackageCsvEntryName, out var csvEntry))
                {
                    throw new InvalidOperationException(
                        "Package does not contain " + CustomAchievementsPackageCsvEntryName + ".");
                }

                string csvText;
                using (var reader = new StreamReader(csvEntry.Open()))
                {
                    csvText = reader.ReadToEnd();
                }

                var result = new CustomAchievementTextImportService().Import(csvText);
                if (result.HasErrors || result.Definitions.Count == 0)
                {
                    return result;
                }

                foreach (var definition in result.Definitions)
                {
                    PortablePersonalState.Strip(definition);
                }

                var fileStems = AchievementIconCachePathBuilder.BuildFileStems(
                    result.Definitions.Select(definition => CustomAchievementProjectionService.BuildApiName(definition?.Id)));
                foreach (var definition in result.Definitions)
                {
                    var apiName = CustomAchievementProjectionService.BuildApiName(definition?.Id);
                    if (definition == null || string.IsNullOrWhiteSpace(apiName))
                    {
                        continue;
                    }

                    definition.UnlockedIconPath = ImportPackagedCustomAchievementIcon(
                        playniteGameId, entriesByName, fileStems, apiName, definition.UnlockedIconPath, AchievementIconVariant.Unlocked);
                    definition.LockedIconPath = ImportPackagedCustomAchievementIcon(
                        playniteGameId, entriesByName, fileStems, apiName, definition.LockedIconPath, AchievementIconVariant.Locked);
                }

                return result;
            }
        }

        private string ImportPackagedCustomAchievementIcon(
            Guid playniteGameId,
            IReadOnlyDictionary<string, ZipArchiveEntry> entriesByName,
            IReadOnlyDictionary<string, string> fileStems,
            string apiName,
            string value,
            AchievementIconVariant variant)
        {
            var normalizedValue = NormalizeText(value);
            if (string.IsNullOrWhiteSpace(normalizedValue) || Path.IsPathRooted(normalizedValue))
            {
                return normalizedValue;
            }

            return RewritePackageCustomAchievementImage(
                playniteGameId,
                entriesByName,
                fileStems,
                apiName,
                normalizedValue,
                variant);
        }
    }
}
