using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.GameCustomData;
using PlayniteAchievements.Services.Images;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace PlayniteAchievements.Services.Tests
{
    [TestClass]
    [DoNotParallelize]
    public class GameCustomDataStorePortablePackageReadTests
    {
        private const string PngBase64 =
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIW2NkYGD4DwABBAEAgh8sXQAAAABJRU5ErkJggg==";

        [TestMethod]
        public void ReadPortablePackage_Manifest_ExtractsEveryImageToScratchAndPersistsNothing()
        {
            var tempDir = CreateTempDirectory();
            var gameId = Guid.NewGuid();
            const string apiName = "ach_one";
            const string customId = "my-custom";
            const string category = "DLC";

            try
            {
                var store = new GameCustomDataStore(Path.Combine(tempDir, "store"));
                var diskImageService = new DiskImageService(logger: null, cacheRoot: Path.Combine(tempDir, "cache"));
                var icons = new ManagedCustomIconService(diskImageService, logger: null);
                store.AttachManagedCustomIconService(icons);
                var gameIdText = gameId.ToString("D");

                var fileStem = AchievementIconCachePathBuilder.BuildFileStems(new[] { apiName })[apiName];
                var unlockedPath = icons.GetAchievementCustomIconPath(gameIdText, fileStem, AchievementIconVariant.Unlocked);
                var lockedPath = icons.GetAchievementCustomIconPath(gameIdText, fileStem, AchievementIconVariant.Locked);
                var customApiName = CustomAchievementProjectionService.BuildApiName(customId);
                var customStem = AchievementIconCachePathBuilder.BuildFileStems(new[] { customApiName })[customApiName];
                var customIconPath = icons.GetAchievementCustomIconPath(gameIdText, customStem, AchievementIconVariant.Unlocked);
                var categoryStem = AchievementIconCachePathBuilder.BuildCategoryFileStems(new[] { category })[category];
                var categoryPath = icons.GetCategoryCustomImagePath(gameIdText, categoryStem);
                foreach (var path in new[] { unlockedPath, lockedPath, customIconPath, categoryPath })
                {
                    WritePngFile(path);
                }

                store.Save(gameId, new GameCustomDataFile
                {
                    PlayniteGameId = gameId,
                    AchievementUnlockedIconOverrides = new Dictionary<string, string> { [apiName] = unlockedPath },
                    AchievementLockedIconOverrides = new Dictionary<string, string> { [apiName] = lockedPath },
                    AchievementNotes = new Dictionary<string, string> { [apiName] = "package note" },
                    CustomAchievements = new List<CustomAchievementDefinition>
                    {
                        new CustomAchievementDefinition
                        {
                            Id = customId,
                            DisplayName = "Custom one",
                            UnlockedIconPath = customIconPath
                        }
                    },
                    AchievementCategoryImageOverrides = new Dictionary<string, CategoryImageOverrideData>(StringComparer.OrdinalIgnoreCase)
                    {
                        [category] = new CategoryImageOverrideData { Art = categoryPath }
                    }
                });

                var packagePath = Path.Combine(tempDir, "portable.pa");
                store.ExportPortablePackage(gameId, packagePath);

                var storedBefore = store.LoadAll().Count;
                var iconFilesBefore = ListFiles(diskImageService.GetCacheDirectoryPath());
                var scratch = Path.Combine(tempDir, "scratch");

                var package = store.ReadPortablePackage(packagePath, scratch);

                Assert.AreEqual(GameCustomDataPackageShape.Manifest, package.Shape);
                Assert.IsNull(package.CustomAchievements);
                Assert.AreEqual(0, package.ImageOnlyEntries.Count);
                var manifest = package.Manifest;
                Assert.IsNotNull(manifest);
                Assert.AreEqual(Guid.Empty, manifest.PlayniteGameId);
                Assert.AreEqual("package note", manifest.AchievementNotes[apiName]);

                AssertInScratch(scratch, manifest.AchievementUnlockedIconOverrides[apiName]);
                AssertInScratch(scratch, manifest.AchievementLockedIconOverrides[apiName]);
                Assert.AreEqual(
                    manifest.AchievementUnlockedIconOverrides[apiName],
                    manifest.AchievementOverrides[apiName].UnlockedIconPath,
                    "the record and the legacy mirror map agree after normalization");
                Assert.AreEqual(
                    manifest.AchievementLockedIconOverrides[apiName],
                    manifest.AchievementOverrides[apiName].LockedIconPath);
                AssertInScratch(scratch, manifest.CustomAchievements.Single().UnlockedIconPath);
                AssertInScratch(scratch, manifest.AchievementCategoryImageOverrides[category].Art);

                Assert.AreEqual(4, package.ExtractedImages.Count);
                foreach (var path in package.ExtractedImages.Values)
                {
                    AssertInScratch(scratch, path);
                }

                Assert.AreEqual(storedBefore, store.LoadAll().Count, "no record is written");
                Assert.IsTrue(store.TryLoad(gameId, out var source));
                Assert.AreEqual(unlockedPath, AchievementOverride.Project(source.AchievementOverrides, o => o.UnlockedIconPath)[apiName], "the source record is untouched");
                CollectionAssert.AreEqual(iconFilesBefore, ListFiles(diskImageService.GetCacheDirectoryPath()), "the managed icon cache gains nothing");
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public void ReadPortablePackage_Manifest_RejectsWhatImportRejects()
        {
            var tempDir = CreateTempDirectory();
            try
            {
                var store = new GameCustomDataStore(Path.Combine(tempDir, "store"));
                var diskImageService = new DiskImageService(logger: null, cacheRoot: Path.Combine(tempDir, "cache"));
                store.AttachManagedCustomIconService(new ManagedCustomIconService(diskImageService, logger: null));

                var packagePath = Path.Combine(tempDir, "broken.pa");
                using (var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create))
                {
                    var manifest = new GameCustomDataPortableFile
                    {
                        AchievementUnlockedIconOverrides = new Dictionary<string, string> { ["ach_one"] = "images/missing.png" }
                    };
                    using (var writer = new StreamWriter(archive.CreateEntry(GameCustomDataStore.PortablePackageManifestEntryName).Open()))
                    {
                        writer.Write(JsonConvert.SerializeObject(manifest));
                    }
                }

                var importError = Assert.ThrowsException<InvalidOperationException>(
                    () => store.ImportReplacePortable(Guid.NewGuid(), packagePath));
                var previewError = Assert.ThrowsException<InvalidOperationException>(
                    () => store.ReadPortablePackage(packagePath, Path.Combine(tempDir, "scratch")));
                Assert.AreEqual(importError.Message, previewError.Message);
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public void ReadPortablePackage_CustomAchievementsCsv_ExtractsIconsToScratch()
        {
            var tempDir = CreateTempDirectory();
            var gameId = Guid.NewGuid();
            const string customId = "first-win";
            try
            {
                var store = new GameCustomDataStore(Path.Combine(tempDir, "store"));
                var diskImageService = new DiskImageService(logger: null, cacheRoot: Path.Combine(tempDir, "cache"));
                var icons = new ManagedCustomIconService(diskImageService, logger: null);
                store.AttachManagedCustomIconService(icons);

                var customApiName = CustomAchievementProjectionService.BuildApiName(customId);
                var customStem = AchievementIconCachePathBuilder.BuildFileStems(new[] { customApiName })[customApiName];
                var iconEntry = "images/" + customStem + ".png";

                var packagePath = Path.Combine(tempDir, "custom.pa");
                using (var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create))
                {
                    using (var writer = new StreamWriter(archive.CreateEntry(GameCustomDataStore.CustomAchievementsPackageCsvEntryName).Open()))
                    {
                        writer.WriteLine(CustomAchievementCsvFormat.Header);
                        writer.WriteLine(customId + ",First win,,,,,,,,,,," + iconEntry + ",");
                        writer.WriteLine("second,Second,,,,,,,,,,,,");
                    }

                    WritePackageImageEntry(archive, iconEntry);
                }

                var iconFilesBefore = ListFiles(diskImageService.GetCacheDirectoryPath());
                var scratch = Path.Combine(tempDir, "scratch");
                var package = store.ReadPortablePackage(packagePath, scratch);

                Assert.AreEqual(GameCustomDataPackageShape.CustomAchievementsCsv, package.Shape);
                Assert.IsNull(package.Manifest);
                Assert.IsNotNull(package.CustomAchievements);
                Assert.IsFalse(package.CustomAchievements.HasErrors, string.Join("; ", package.CustomAchievements.Errors));
                Assert.AreEqual(2, package.CustomAchievements.Definitions.Count);
                AssertInScratch(scratch, package.CustomAchievements.Definitions[0].UnlockedIconPath);
                Assert.IsNull(package.CustomAchievements.Definitions[1].UnlockedIconPath);
                Assert.AreEqual(1, package.ExtractedImages.Count);
                Assert.AreEqual(0, store.LoadAll().Count);
                CollectionAssert.AreEqual(iconFilesBefore, ListFiles(diskImageService.GetCacheDirectoryPath()));
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public void ReadPortablePackage_ImageOnly_ListsIconsByApiName()
        {
            var tempDir = CreateTempDirectory();
            try
            {
                var store = new GameCustomDataStore(Path.Combine(tempDir, "store"));
                var packagePath = Path.Combine(tempDir, "icons.pa");
                using (var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create))
                {
                    WritePackageImageEntry(archive, "images/ach_one.png");
                    WritePackageImageEntry(archive, "images/ach_one.locked.png");
                    WritePackageImageEntry(archive, "readme.txt");
                }

                var scratch = Path.Combine(tempDir, "scratch");
                var package = store.ReadPortablePackage(packagePath, scratch);

                Assert.AreEqual(GameCustomDataPackageShape.ImageOnly, package.Shape);
                Assert.IsNull(package.Manifest);
                Assert.IsNull(package.CustomAchievements);
                Assert.AreEqual(2, package.ImageOnlyEntries.Count);
                var locked = package.ImageOnlyEntries.Single(entry => entry.Variant == AchievementIconVariant.Locked);
                var unlocked = package.ImageOnlyEntries.Single(entry => entry.Variant == AchievementIconVariant.Unlocked);
                Assert.AreEqual("ach_one", locked.ApiName);
                Assert.AreEqual("ach_one", unlocked.ApiName);
                AssertInScratch(scratch, locked.Path);
                AssertInScratch(scratch, unlocked.Path);
                Assert.AreNotEqual(locked.Path, unlocked.Path);
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        private static void AssertInScratch(string scratch, string path)
        {
            Assert.IsFalse(string.IsNullOrWhiteSpace(path), "expected an extracted path");
            var full = Path.GetFullPath(path);
            StringAssert.StartsWith(full, Path.GetFullPath(scratch) + Path.DirectorySeparatorChar);
            Assert.IsTrue(File.Exists(full), full);
        }

        private static List<string> ListFiles(string directory)
        {
            return Directory.Exists(directory)
                ? Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToList()
                : new List<string>();
        }

        private static string CreateTempDirectory()
        {
            var path = Path.Combine(Path.GetTempPath(), "PlayniteAchievementsTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        private static void DeleteDirectory(string path)
        {
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }

        private static void WritePngFile(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, Convert.FromBase64String(PngBase64));
        }

        private static void WritePackageImageEntry(ZipArchive archive, string entryName)
        {
            using (var stream = archive.CreateEntry(entryName, CompressionLevel.Optimal).Open())
            {
                var pngBytes = Convert.FromBase64String(PngBase64);
                stream.Write(pngBytes, 0, pngBytes.Length);
            }
        }
    }
}
