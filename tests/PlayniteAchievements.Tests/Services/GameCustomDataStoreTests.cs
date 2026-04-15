using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Providers.Exophase;
using PlayniteAchievements.Providers.Manual;
using PlayniteAchievements.Providers.RetroAchievements;
using PlayniteAchievements.Services;
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
    public class GameCustomDataStoreTests
    {
        [TestMethod]
        public void Save_BlankPayloadDoesNotCreateDatabase()
        {
            var tempDir = CreateTempDirectory();
            var gameId = Guid.NewGuid();

            try
            {
                var store = new GameCustomDataStore(tempDir);

                store.Save(gameId, new GameCustomDataFile
                {
                    PlayniteGameId = Guid.NewGuid(),
                    ManualCapstoneApiName = "   ",
                    AchievementOrder = new List<string> { " ", null },
                    AchievementCategoryOverrides = new Dictionary<string, string>
                    {
                        [" "] = " "
                    },
                    AchievementUnlockedIconOverrides = new Dictionary<string, string>
                    {
                        [" "] = " "
                    },
                    AchievementLockedIconOverrides = new Dictionary<string, string>
                    {
                        ["ach_one"] = " "
                    },
                    UseSeparateLockedIconsOverride = false,
                    ForceUseExophase = false
                });

                Assert.IsFalse(File.Exists(store.DatabasePath));
                Assert.IsFalse(store.TryLoad(gameId, out _));
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public void CustomDataChanged_RaisesForSaveAndDelete()
        {
            var tempDir = CreateTempDirectory();
            var gameId = Guid.NewGuid();

            try
            {
                var store = new GameCustomDataStore(tempDir);
                var changedGameIds = new List<Guid>();
                store.CustomDataChanged += (_, args) => changedGameIds.Add(args.PlayniteGameId);

                store.Save(gameId, new GameCustomDataFile
                {
                    PlayniteGameId = gameId,
                    ManualCapstoneApiName = "capstone"
                });

                store.Delete(gameId);

                CollectionAssert.AreEqual(
                    new List<Guid> { gameId, gameId },
                    changedGameIds);
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public void Save_ManualLinkOnly_IsVisibleCustomization()
        {
            var tempDir = CreateTempDirectory();
            var gameId = Guid.NewGuid();

            try
            {
                var store = new GameCustomDataStore(tempDir);
                store.Save(gameId, new GameCustomDataFile
                {
                    PlayniteGameId = gameId,
                    ManualLink = new ManualAchievementLink
                    {
                        SourceKey = "Steam",
                        SourceGameId = "123"
                    }
                });

                Assert.IsTrue(store.TryLoad(gameId, out var loaded));
                Assert.IsTrue(GameCustomDataNormalizer.HasVisibleCustomization(loaded));
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public void Save_RetroAchievementsOverrideOnly_IsVisibleCustomization()
        {
            var tempDir = CreateTempDirectory();
            var gameId = Guid.NewGuid();

            try
            {
                var store = new GameCustomDataStore(tempDir);
                store.Save(gameId, new GameCustomDataFile
                {
                    PlayniteGameId = gameId,
                    RetroAchievementsGameIdOverride = 12345
                });

                Assert.IsTrue(store.TryLoad(gameId, out var loaded));
                Assert.IsTrue(GameCustomDataNormalizer.HasVisibleCustomization(loaded));
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public void Save_XeniaTitleIdOverrideOnly_IsVisibleCustomization()
        {
            var tempDir = CreateTempDirectory();
            var gameId = Guid.NewGuid();

            try
            {
                var store = new GameCustomDataStore(tempDir);
                store.Save(gameId, new GameCustomDataFile
                {
                    PlayniteGameId = gameId,
                    XeniaTitleIdOverride = "0x4d5307e6"
                });

                Assert.IsTrue(store.TryLoad(gameId, out var loaded));
                Assert.AreEqual("4D5307E6", loaded.XeniaTitleIdOverride);
                Assert.IsTrue(GameCustomDataNormalizer.HasVisibleCustomization(loaded));
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public void Save_ShadPS4MatchIdOverrideOnly_IsVisibleCustomization()
        {
            var tempDir = CreateTempDirectory();
            var gameId = Guid.NewGuid();

            try
            {
                var store = new GameCustomDataStore(tempDir);
                store.Save(gameId, new GameCustomDataFile
                {
                    PlayniteGameId = gameId,
                    ShadPS4MatchIdOverride = "npwr12345_00"
                });

                Assert.IsTrue(store.TryLoad(gameId, out var loaded));
                Assert.AreEqual("NPWR12345_00", loaded.ShadPS4MatchIdOverride);
                Assert.IsTrue(GameCustomDataNormalizer.HasVisibleCustomization(loaded));
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public void Save_ExophaseIncludeOnly_IsVisibleCustomization()
        {
            var tempDir = CreateTempDirectory();
            var gameId = Guid.NewGuid();

            try
            {
                var store = new GameCustomDataStore(tempDir);
                store.Save(gameId, new GameCustomDataFile
                {
                    PlayniteGameId = gameId,
                    ForceUseExophase = true
                });

                Assert.IsTrue(store.TryLoad(gameId, out var loaded));
                Assert.IsTrue(GameCustomDataNormalizer.HasVisibleCustomization(loaded));
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public void Save_ExophaseSlugOverrideOnly_IsVisibleCustomization()
        {
            var tempDir = CreateTempDirectory();
            var gameId = Guid.NewGuid();

            try
            {
                var store = new GameCustomDataStore(tempDir);
                store.Save(gameId, new GameCustomDataFile
                {
                    PlayniteGameId = gameId,
                    ExophaseSlugOverride = "test-slug"
                });

                Assert.IsTrue(store.TryLoad(gameId, out var loaded));
                Assert.IsTrue(GameCustomDataNormalizer.HasVisibleCustomization(loaded));
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public void Export_OmitsInternalExclusionFlags()
        {
            var tempDir = CreateTempDirectory();
            var gameId = Guid.NewGuid();
            var exportPath = Path.Combine(tempDir, "portable-export.json");

            try
            {
                var store = new GameCustomDataStore(tempDir);
                store.Save(gameId, new GameCustomDataFile
                {
                    PlayniteGameId = gameId,
                    ExcludedFromRefreshes = true,
                    ExcludedFromSummaries = true,
                    UseSeparateLockedIconsOverride = true,
                    ManualCapstoneApiName = " capstone_one ",
                    AchievementOrder = new List<string> { "ach_one", " ACH_ONE ", "ach_two" },
                    AchievementUnlockedIconOverrides = new Dictionary<string, string>
                    {
                        [" ach_one "] = " https://example.com/unlocked.png ",
                        ["ach_blank"] = " "
                    },
                    AchievementLockedIconOverrides = new Dictionary<string, string>
                    {
                        ["ach_one"] = " https://example.com/locked.png "
                    },
                    RetroAchievementsGameIdOverride = 12345,
                    XeniaTitleIdOverride = "0x4d5307e6",
                    ShadPS4MatchIdOverride = "npwr12345_00"
                });

                store.Export(gameId, exportPath);

                var exportedJson = File.ReadAllText(exportPath);
                Assert.IsFalse(exportedJson.Contains(nameof(GameCustomDataFile.ExcludedFromRefreshes)));
                Assert.IsFalse(exportedJson.Contains(nameof(GameCustomDataFile.ExcludedFromSummaries)));

                var portable = JsonConvert.DeserializeObject<GameCustomDataPortableFile>(exportedJson);
                Assert.IsNotNull(portable);
                Assert.AreEqual(gameId, portable.PlayniteGameId);
                Assert.AreEqual("capstone_one", portable.ManualCapstoneApiName);
                CollectionAssert.AreEqual(new[] { "ach_one", "ach_two" }, portable.AchievementOrder);
                Assert.AreEqual("https://example.com/unlocked.png", portable.AchievementUnlockedIconOverrides["ach_one"]);
                Assert.AreEqual("https://example.com/locked.png", portable.AchievementLockedIconOverrides["ach_one"]);
                Assert.AreEqual(1, portable.AchievementUnlockedIconOverrides.Count);
                Assert.AreEqual(1, portable.AchievementLockedIconOverrides.Count);
                Assert.AreEqual(12345, portable.RetroAchievementsGameIdOverride);
                Assert.AreEqual("4D5307E6", portable.XeniaTitleIdOverride);
                Assert.AreEqual("NPWR12345_00", portable.ShadPS4MatchIdOverride);
                Assert.IsTrue(portable.UseSeparateLockedIconsOverride == true);
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public void Save_UrlOverride_PrunesCustomCacheButKeepsExpectedManagedCustomFile()
        {
            var tempDir = CreateTempDirectory();
            var gameId = Guid.NewGuid();
            const string apiName = "ach_one";

            try
            {
                var store = new GameCustomDataStore(tempDir);
                var diskImageService = new DiskImageService(logger: null, cacheRoot: tempDir);
                var managedCustomIconService = new ManagedCustomIconService(diskImageService, logger: null);
                store.AttachManagedCustomIconService(managedCustomIconService);

                var fileStem = AchievementIconCachePathBuilder.BuildFileStems(new[] { apiName })[apiName];
                var expectedManagedPath = managedCustomIconService.GetAchievementCustomIconPath(
                    gameId.ToString("D"),
                    fileStem,
                    AchievementIconVariant.Unlocked);
                WritePlaceholderFile(expectedManagedPath);

                store.Save(gameId, new GameCustomDataFile
                {
                    PlayniteGameId = gameId,
                    AchievementUnlockedIconOverrides = new Dictionary<string, string>
                    {
                        [apiName] = "https://example.com/unlocked.png"
                    }
                });

                Assert.IsTrue(File.Exists(expectedManagedPath));
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public void ExportPortablePa_OmitsManagedLocalIconOverrides()
        {
            var tempDir = CreateTempDirectory();
            var gameId = Guid.NewGuid();
            const string apiName = "ach_one";

            try
            {
                var store = new GameCustomDataStore(tempDir);
                var diskImageService = new DiskImageService(logger: null, cacheRoot: tempDir);
                var managedCustomIconService = new ManagedCustomIconService(diskImageService, logger: null);
                store.AttachManagedCustomIconService(managedCustomIconService);

                var fileStem = AchievementIconCachePathBuilder.BuildFileStems(new[] { apiName })[apiName];
                var managedPath = managedCustomIconService.GetAchievementCustomIconPath(
                    gameId.ToString("D"),
                    fileStem,
                    AchievementIconVariant.Unlocked);
                WritePlaceholderFile(managedPath);

                store.Save(gameId, new GameCustomDataFile
                {
                    PlayniteGameId = gameId,
                    AchievementUnlockedIconOverrides = new Dictionary<string, string>
                    {
                        [apiName] = managedPath
                    },
                    AchievementLockedIconOverrides = new Dictionary<string, string>
                    {
                        [apiName] = "https://example.com/locked.png"
                    }
                });

                var exportPath = Path.Combine(tempDir, "portable.pa");
                var result = store.ExportPortablePa(gameId, exportPath);
                var portable = JsonConvert.DeserializeObject<GameCustomDataPortableFile>(File.ReadAllText(exportPath));

                Assert.IsTrue(result.HasOmittedLocalIconOverrides);
                Assert.AreEqual(1, result.OmittedLocalIconOverrideCount);
                Assert.IsNull(portable.AchievementUnlockedIconOverrides);
                Assert.AreEqual("https://example.com/locked.png", portable.AchievementLockedIconOverrides[apiName]);
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public void ImportReplacePortable_RejectsLocalPathsInPa()
        {
            var tempDir = CreateTempDirectory();
            var gameId = Guid.NewGuid();
            var importPath = Path.Combine(tempDir, "bad.pa");

            try
            {
                var store = new GameCustomDataStore(tempDir);
                File.WriteAllText(
                    importPath,
                    JsonConvert.SerializeObject(
                        new GameCustomDataPortableFile
                        {
                            PlayniteGameId = Guid.NewGuid(),
                            AchievementUnlockedIconOverrides = new Dictionary<string, string>
                            {
                                ["ach_one"] = @"C:\temp\custom.png"
                            }
                        }));

                Assert.ThrowsException<InvalidOperationException>(() => store.ImportReplacePortable(gameId, importPath));
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public void ExportPortablePackage_AndImportReplacePortable_RoundTripBundledImages()
        {
            var tempDir = CreateTempDirectory();
            var gameId = Guid.NewGuid();
            var importedGameId = Guid.NewGuid();
            const string apiName = "ach_one";

            try
            {
                var store = new GameCustomDataStore(tempDir);
                var diskImageService = new DiskImageService(logger: null, cacheRoot: tempDir);
                var managedCustomIconService = new ManagedCustomIconService(diskImageService, logger: null);
                store.AttachManagedCustomIconService(managedCustomIconService);

                var fileStem = AchievementIconCachePathBuilder.BuildFileStems(new[] { apiName })[apiName];
                var unlockedManagedPath = managedCustomIconService.GetAchievementCustomIconPath(
                    gameId.ToString("D"),
                    fileStem,
                    AchievementIconVariant.Unlocked);
                var lockedManagedPath = managedCustomIconService.GetAchievementCustomIconPath(
                    gameId.ToString("D"),
                    fileStem,
                    AchievementIconVariant.Locked);
                WritePlaceholderFile(unlockedManagedPath);
                WritePlaceholderFile(lockedManagedPath);

                store.Save(gameId, new GameCustomDataFile
                {
                    PlayniteGameId = gameId,
                    AchievementUnlockedIconOverrides = new Dictionary<string, string>
                    {
                        [apiName] = unlockedManagedPath
                    },
                    AchievementLockedIconOverrides = new Dictionary<string, string>
                    {
                        [apiName] = lockedManagedPath
                    }
                });

                var packagePath = Path.Combine(tempDir, "portable.pa.zip");
                store.ExportPortablePackage(gameId, packagePath);

                using (var archive = ZipFile.OpenRead(packagePath))
                {
                    var entryNames = archive.Entries.Select(entry => entry.FullName).ToList();
                    CollectionAssert.Contains(entryNames, GameCustomDataStore.PortablePackageManifestEntryName);
                    CollectionAssert.Contains(entryNames, "images/" + fileStem + ".png");
                    CollectionAssert.Contains(entryNames, "images/" + fileStem + ".locked.png");

                    using (var reader = new StreamReader(archive.GetEntry(GameCustomDataStore.PortablePackageManifestEntryName).Open()))
                    {
                        var portable = JsonConvert.DeserializeObject<GameCustomDataPortableFile>(reader.ReadToEnd());
                        Assert.AreEqual("images/" + fileStem + ".png", portable.AchievementUnlockedIconOverrides[apiName]);
                        Assert.AreEqual("images/" + fileStem + ".locked.png", portable.AchievementLockedIconOverrides[apiName]);
                    }
                }

                var importResult = store.ImportReplacePortable(importedGameId, packagePath);
                var imported = importResult.ImportedData;
                Assert.IsNotNull(imported);
                Assert.IsFalse(importResult.HasIgnoredPackageImages);
                Assert.IsTrue(imported.AchievementUnlockedIconOverrides[apiName].EndsWith(Path.Combine("icon_cache", importedGameId.ToString("D"), "custom", fileStem + ".png")));
                Assert.IsTrue(imported.AchievementLockedIconOverrides[apiName].EndsWith(Path.Combine("icon_cache", importedGameId.ToString("D"), "custom", fileStem + ".locked.png")));
                Assert.IsTrue(File.Exists(imported.AchievementUnlockedIconOverrides[apiName]));
                Assert.IsTrue(File.Exists(imported.AchievementLockedIconOverrides[apiName]));
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public void ImportReplace_PreservesInternalExclusionsAndRewritesGameId()
        {
            var tempDir = CreateTempDirectory();
            var gameId = Guid.NewGuid();
            var foreignGameId = Guid.NewGuid();
            var importPath = Path.Combine(tempDir, "custom-name.json");

            try
            {
                var store = new GameCustomDataStore(tempDir);
                store.Save(gameId, new GameCustomDataFile
                {
                    PlayniteGameId = gameId,
                    ExcludedFromRefreshes = true,
                    ExcludedFromSummaries = true,
                    UseSeparateLockedIconsOverride = true,
                    ManualCapstoneApiName = "old-capstone",
                    RetroAchievementsGameIdOverride = 11
                });

                File.WriteAllText(
                    importPath,
                    JsonConvert.SerializeObject(
                        new GameCustomDataPortableFile
                        {
                            PlayniteGameId = foreignGameId,
                            ManualCapstoneApiName = " imported-capstone ",
                            AchievementUnlockedIconOverrides = new Dictionary<string, string>
                            {
                                [" ach_one "] = " https://example.com/new-unlocked.png "
                            },
                            AchievementLockedIconOverrides = new Dictionary<string, string>
                            {
                                ["ach_one"] = " https://example.com/new-locked.png "
                            },
                            RetroAchievementsGameIdOverride = 444,
                            XeniaTitleIdOverride = "0x4d5307e6",
                            ShadPS4MatchIdOverride = "npwr12345_00",
                            ForceUseExophase = true
                        }));

                store.ImportReplace(gameId, importPath);

                Assert.IsTrue(store.TryLoad(gameId, out var imported));
                Assert.AreEqual(gameId, imported.PlayniteGameId);
                Assert.IsTrue(imported.ExcludedFromRefreshes == true);
                Assert.IsTrue(imported.ExcludedFromSummaries == true);
                Assert.AreEqual("imported-capstone", imported.ManualCapstoneApiName);
                Assert.AreEqual("https://example.com/new-unlocked.png", imported.AchievementUnlockedIconOverrides["ach_one"]);
                Assert.AreEqual("https://example.com/new-locked.png", imported.AchievementLockedIconOverrides["ach_one"]);
                Assert.AreEqual(444, imported.RetroAchievementsGameIdOverride);
                Assert.AreEqual("4D5307E6", imported.XeniaTitleIdOverride);
                Assert.AreEqual("NPWR12345_00", imported.ShadPS4MatchIdOverride);
                Assert.IsTrue(imported.ForceUseExophase == true);
                Assert.IsNull(imported.UseSeparateLockedIconsOverride);
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public void ImportReplacePortable_ImageOnlyPackage_ImportsMatchingApiNameImages_AndWarnsOnMismatches()
        {
            var tempDir = CreateTempDirectory();
            var gameId = Guid.NewGuid();
            var packagePath = Path.Combine(tempDir, "image-only.pa.zip");

            try
            {
                var store = new GameCustomDataStore(tempDir);
                var diskImageService = new DiskImageService(logger: null, cacheRoot: tempDir);
                var managedCustomIconService = new ManagedCustomIconService(diskImageService, logger: null);
                var achievementDataService = new AchievementDataService();
                achievementDataService.GameDataById[gameId] = new GameAchievementData
                {
                    PlayniteGameId = gameId,
                    Achievements = new List<AchievementDetail>
                    {
                        new AchievementDetail { ApiName = "ach_one" },
                        new AchievementDetail { ApiName = "ach_two" }
                    }
                };

                store.AttachManagedCustomIconService(managedCustomIconService);
                store.AttachAchievementDataService(achievementDataService);

                using (var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create))
                {
                    WritePackageImageEntry(archive, "ach_one.png");
                    WritePackageImageEntry(archive, "images/ach_two.locked.png");
                    WritePackageImageEntry(archive, "missing_api.png");
                }

                var importResult = store.ImportReplacePortable(gameId, packagePath);
                var imported = importResult.ImportedData;

                Assert.IsNotNull(imported);
                Assert.IsTrue(importResult.HasIgnoredPackageImages);
                Assert.AreEqual(1, importResult.IgnoredPackageImageCount);
                Assert.AreEqual(1, imported.AchievementUnlockedIconOverrides.Count);
                Assert.AreEqual(1, imported.AchievementLockedIconOverrides.Count);
                Assert.IsTrue(File.Exists(imported.AchievementUnlockedIconOverrides["ach_one"]));
                Assert.IsTrue(File.Exists(imported.AchievementLockedIconOverrides["ach_two"]));
                Assert.IsFalse(imported.AchievementUnlockedIconOverrides.ContainsKey("missing_api"));
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public void ImportReplacePortable_ImageOnlyPackage_RejectsWhenNoImagesMatchAchievementApiNames()
        {
            var tempDir = CreateTempDirectory();
            var gameId = Guid.NewGuid();
            var packagePath = Path.Combine(tempDir, "image-only.pa.zip");

            try
            {
                var store = new GameCustomDataStore(tempDir);
                var diskImageService = new DiskImageService(logger: null, cacheRoot: tempDir);
                var managedCustomIconService = new ManagedCustomIconService(diskImageService, logger: null);
                var achievementDataService = new AchievementDataService();
                achievementDataService.GameDataById[gameId] = new GameAchievementData
                {
                    PlayniteGameId = gameId,
                    Achievements = new List<AchievementDetail>
                    {
                        new AchievementDetail { ApiName = "ach_one" }
                    }
                };

                store.AttachManagedCustomIconService(managedCustomIconService);
                store.AttachAchievementDataService(achievementDataService);

                using (var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create))
                {
                    WritePackageImageEntry(archive, "missing_api.png");
                }

                Assert.ThrowsException<InvalidOperationException>(() => store.ImportReplacePortable(gameId, packagePath));
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public void ImportReplace_RejectsPortableJsonWithoutPortableData()
        {
            var tempDir = CreateTempDirectory();
            var gameId = Guid.NewGuid();
            var importPath = Path.Combine(tempDir, "empty.json");

            try
            {
                var store = new GameCustomDataStore(tempDir);
                store.Save(gameId, new GameCustomDataFile
                {
                    PlayniteGameId = gameId,
                    ExcludedFromRefreshes = true
                });

                File.WriteAllText(
                    importPath,
                    JsonConvert.SerializeObject(new GameCustomDataPortableFile
                    {
                        PlayniteGameId = Guid.NewGuid()
                    }));

                Assert.ThrowsException<InvalidOperationException>(() => store.ImportReplace(gameId, importPath));

                Assert.IsTrue(store.TryLoad(gameId, out var current));
                Assert.IsTrue(current.ExcludedFromRefreshes == true);
                Assert.IsNull(current.ManualCapstoneApiName);
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public void MigrateLegacyConfig_CreatesMergedRowsRemovesLegacyFieldsAndPrefersExistingData()
        {
            var tempDir = CreateTempDirectory();
            var existingGameId = Guid.NewGuid();
            var legacyOnlyGameId = Guid.NewGuid();

            try
            {
                var store = new GameCustomDataStore(tempDir);
                store.Save(existingGameId, new GameCustomDataFile
                {
                    PlayniteGameId = existingGameId,
                    UseSeparateLockedIconsOverride = true,
                    ManualCapstoneApiName = "existing-capstone"
                });

                var persisted = new JObject
                {
                    ["ExcludedGameIds"] = new JArray(existingGameId.ToString("D"), legacyOnlyGameId.ToString("D")),
                    ["ExcludedFromSummariesGameIds"] = new JArray(legacyOnlyGameId.ToString("D")),
                    ["SeparateLockedIconEnabledGameIds"] = new JArray(legacyOnlyGameId.ToString("D")),
                    ["ManualCapstones"] = new JObject
                    {
                        [existingGameId.ToString("D")] = "legacy-capstone",
                        [legacyOnlyGameId.ToString("D")] = "legacy-only-capstone"
                    },
                    ["AchievementOrderOverrides"] = new JObject
                    {
                        [legacyOnlyGameId.ToString("D")] = new JArray("ach_one", " ACH_ONE ", "ach_two")
                    },
                    ["AchievementCategoryOverrides"] = new JObject
                    {
                        [legacyOnlyGameId.ToString("D")] = new JObject
                        {
                            ["ach_one"] = " Main "
                        }
                    },
                    ["AchievementCategoryTypeOverrides"] = new JObject
                    {
                        [legacyOnlyGameId.ToString("D")] = new JObject
                        {
                            ["ach_one"] = "dlc | single player | dlc"
                        }
                    }
                };

                persisted["ProviderSettings"] = new JObject
                {
                    ["RetroAchievements"] = new JObject
                    {
                        ["RaGameIdOverrides"] = new JObject
                        {
                            [existingGameId.ToString("D")] = 222,
                            [legacyOnlyGameId.ToString("D")] = 333
                        }
                    },
                    ["Exophase"] = new JObject
                    {
                        ["IncludedGames"] = new JArray(legacyOnlyGameId.ToString("D")),
                        ["SlugOverrides"] = new JObject
                        {
                            [legacyOnlyGameId.ToString("D")] = " legacy-slug "
                        }
                    },
                    ["Manual"] = new JObject
                    {
                        ["AchievementLinks"] = new JObject
                        {
                            [legacyOnlyGameId.ToString("D")] = JObject.FromObject(new ManualAchievementLink
                            {
                                SourceKey = "Steam",
                                SourceGameId = "999",
                                UnlockStates = new Dictionary<string, bool>
                                {
                                    ["ach_one"] = true,
                                    ["ach_two"] = false
                                },
                                UnlockTimes = new Dictionary<string, DateTime?>
                                {
                                    ["ach_one"] = new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc)
                                },
                                CreatedUtc = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                                LastModifiedUtc = new DateTime(2025, 1, 3, 0, 0, 0, DateTimeKind.Utc)
                            })
                        }
                    }
                };

                var rawJson = new JObject
                {
                    ["Persisted"] = persisted
                }.ToString(Formatting.None);

                var migratedJson = store.MigrateLegacyConfig(rawJson);
                var migratedRoot = JObject.Parse(migratedJson);
                var migratedPersisted = (JObject)migratedRoot["Persisted"];
                var migratedProviderSettings = (JObject)migratedPersisted["ProviderSettings"];

                Assert.IsNull(migratedPersisted["ExcludedGameIds"]);
                Assert.IsNull(migratedPersisted["ExcludedFromSummariesGameIds"]);
                Assert.IsNull(migratedPersisted["SeparateLockedIconEnabledGameIds"]);
                Assert.IsNull(migratedPersisted["ManualCapstones"]);
                Assert.IsNull(migratedPersisted["AchievementOrderOverrides"]);
                Assert.IsNull(migratedPersisted["AchievementCategoryOverrides"]);
                Assert.IsNull(migratedPersisted["AchievementCategoryTypeOverrides"]);
                Assert.IsNull(((JObject)migratedProviderSettings["RetroAchievements"])["RaGameIdOverrides"]);
                Assert.IsNull(((JObject)migratedProviderSettings["Exophase"])["IncludedGames"]);
                Assert.IsNull(((JObject)migratedProviderSettings["Exophase"])["SlugOverrides"]);
                Assert.IsNull(((JObject)migratedProviderSettings["Manual"])["AchievementLinks"]);

                Assert.IsTrue(store.TryLoad(existingGameId, out var existing));
                Assert.AreEqual("existing-capstone", existing.ManualCapstoneApiName);
                Assert.IsTrue(existing.UseSeparateLockedIconsOverride == true);
                Assert.IsTrue(existing.ExcludedFromRefreshes == true);
                Assert.AreEqual(222, existing.RetroAchievementsGameIdOverride);

                Assert.IsTrue(store.TryLoad(legacyOnlyGameId, out var legacyOnly));
                Assert.IsTrue(legacyOnly.ExcludedFromRefreshes == true);
                Assert.IsTrue(legacyOnly.ExcludedFromSummaries == true);
                Assert.IsTrue(legacyOnly.UseSeparateLockedIconsOverride == true);
                Assert.AreEqual("legacy-only-capstone", legacyOnly.ManualCapstoneApiName);
                CollectionAssert.AreEqual(new[] { "ach_one", "ach_two" }, legacyOnly.AchievementOrder);
                Assert.AreEqual("Main", legacyOnly.AchievementCategoryOverrides["ach_one"]);
                Assert.AreEqual("DLC|Singleplayer", legacyOnly.AchievementCategoryTypeOverrides["ach_one"]);
                Assert.AreEqual(333, legacyOnly.RetroAchievementsGameIdOverride);
                Assert.IsTrue(legacyOnly.ForceUseExophase == true);
                Assert.AreEqual("legacy-slug", legacyOnly.ExophaseSlugOverride);
                Assert.IsNotNull(legacyOnly.ManualLink);
                Assert.AreEqual("Steam", legacyOnly.ManualLink.SourceKey);
                Assert.AreEqual("999", legacyOnly.ManualLink.SourceGameId);
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public void AttachRuntimeSettings_DoesNotProjectRuntimeCachesFromDatabase()
        {
            var tempDir = CreateTempDirectory();
            var gameId = Guid.NewGuid();

            try
            {
                var settings = new PlayniteAchievementsSettings();
                var store = new GameCustomDataStore(tempDir);

                store.Save(gameId, new GameCustomDataFile
                {
                    PlayniteGameId = gameId,
                    ExcludedFromRefreshes = true,
                    ExcludedFromSummaries = true,
                    UseSeparateLockedIconsOverride = true,
                    ManualCapstoneApiName = "capstone",
                    AchievementOrder = new List<string> { "ach_one", "ach_two" },
                    AchievementCategoryOverrides = new Dictionary<string, string>
                    {
                        ["ach_one"] = "Category"
                    },
                    AchievementCategoryTypeOverrides = new Dictionary<string, string>
                    {
                        ["ach_one"] = "DLC"
                    },
                    AchievementUnlockedIconOverrides = new Dictionary<string, string>
                    {
                        ["ach_one"] = "https://example.com/unlocked.png"
                    },
                    AchievementLockedIconOverrides = new Dictionary<string, string>
                    {
                        ["ach_one"] = "https://example.com/locked.png"
                    },
                    RetroAchievementsGameIdOverride = 9876,
                    ForceUseExophase = true,
                    ExophaseSlugOverride = "slug",
                    ManualLink = new ManualAchievementLink
                    {
                        SourceKey = "Steam",
                        SourceGameId = "123"
                    }
                });

                store.AttachRuntimeSettings(settings);

                Assert.AreEqual(0, settings.Persisted.ExcludedGameIds.Count);
                Assert.AreEqual(0, settings.Persisted.ExcludedFromSummariesGameIds.Count);
                Assert.AreEqual(0, settings.Persisted.SeparateLockedIconEnabledGameIds.Count);
                Assert.AreEqual(0, settings.Persisted.ManualCapstones.Count);
                Assert.AreEqual(0, settings.Persisted.AchievementOrderOverrides.Count);
                Assert.AreEqual(0, settings.Persisted.AchievementCategoryOverrides.Count);
                Assert.AreEqual(0, settings.Persisted.AchievementCategoryTypeOverrides.Count);
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public void ImportReplace_AcceptsPortableJsonContainingOnlyIconOverrides()
        {
            var tempDir = CreateTempDirectory();
            var gameId = Guid.NewGuid();
            var importPath = Path.Combine(tempDir, "icon-only.json");

            try
            {
                var store = new GameCustomDataStore(tempDir);

                File.WriteAllText(
                    importPath,
                    JsonConvert.SerializeObject(
                        new GameCustomDataPortableFile
                        {
                            PlayniteGameId = Guid.NewGuid(),
                            AchievementUnlockedIconOverrides = new Dictionary<string, string>
                            {
                                [" ach_one "] = " https://example.com/unlocked.png "
                            },
                            AchievementLockedIconOverrides = new Dictionary<string, string>
                            {
                                ["ach_one"] = " https://example.com/locked.png "
                            }
                        }));

                var imported = store.ImportReplace(gameId, importPath);

                Assert.IsNotNull(imported);
                Assert.AreEqual(gameId, imported.PlayniteGameId);
                Assert.AreEqual("https://example.com/unlocked.png", imported.AchievementUnlockedIconOverrides["ach_one"]);
                Assert.AreEqual("https://example.com/locked.png", imported.AchievementLockedIconOverrides["ach_one"]);
                Assert.IsFalse(imported.ExcludedFromRefreshes.HasValue);
                Assert.IsFalse(imported.ExcludedFromSummaries.HasValue);
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public void GetExcludedSummaryGameIds_UsesDatabaseAsSourceOfTruthButKeepsFallbackForUnmigratedGames()
        {
            var tempDir = CreateTempDirectory();
            var fallbackOverriddenGameId = Guid.NewGuid();
            var fallbackOnlyGameId = Guid.NewGuid();
            var databaseOnlyGameId = Guid.NewGuid();

            try
            {
                var store = new GameCustomDataStore(tempDir);
                var fallbackSettings = new PersistedSettings
                {
                    ExcludedFromSummariesGameIds = new HashSet<Guid>
                    {
                        fallbackOverriddenGameId,
                        fallbackOnlyGameId
                    }
                };

                store.Save(fallbackOverriddenGameId, new GameCustomDataFile
                {
                    PlayniteGameId = fallbackOverriddenGameId,
                    UseSeparateLockedIconsOverride = true
                });
                store.Save(databaseOnlyGameId, new GameCustomDataFile
                {
                    PlayniteGameId = databaseOnlyGameId,
                    ExcludedFromSummaries = true
                });

                var excluded = GameCustomDataLookup.GetExcludedSummaryGameIds(fallbackSettings, store);

                Assert.IsFalse(excluded.Contains(fallbackOverriddenGameId));
                Assert.IsTrue(excluded.Contains(fallbackOnlyGameId));
                Assert.IsTrue(excluded.Contains(databaseOnlyGameId));
                Assert.AreEqual(2, excluded.Count);
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public void TryLoad_AfterDeleteAndResave_ReturnsCurrentDatabaseValue()
        {
            var tempDir = CreateTempDirectory();
            var gameId = Guid.NewGuid();

            try
            {
                var store = new GameCustomDataStore(tempDir);
                store.Save(gameId, new GameCustomDataFile
                {
                    PlayniteGameId = gameId,
                    ManualCapstoneApiName = "capstone_one"
                });

                Assert.IsTrue(store.TryLoad(gameId, out var initial));
                Assert.AreEqual("capstone_one", initial.ManualCapstoneApiName);

                store.Delete(gameId);
                Assert.IsFalse(store.TryLoad(gameId, out _));

                store.Save(gameId, new GameCustomDataFile
                {
                    PlayniteGameId = gameId,
                    ManualCapstoneApiName = "capstone_two"
                });

                Assert.IsTrue(store.TryLoad(gameId, out var updated));
                Assert.AreEqual("capstone_two", updated.ManualCapstoneApiName);
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public void PersistedSettingsSerialization_OmitsRuntimeProjectedCustomDataFields()
        {
            var gameId = Guid.NewGuid();
            var persisted = new PersistedSettings
            {
                GlobalLanguage = "english",
                ExcludedGameIds = new HashSet<Guid> { gameId },
                ExcludedFromSummariesGameIds = new HashSet<Guid> { gameId },
                SeparateLockedIconEnabledGameIds = new HashSet<Guid> { gameId },
                ManualCapstones = new Dictionary<Guid, string>
                {
                    [gameId] = "capstone"
                },
                AchievementOrderOverrides = new Dictionary<Guid, List<string>>
                {
                    [gameId] = new List<string> { "ach_one" }
                },
                AchievementCategoryOverrides = new Dictionary<Guid, Dictionary<string, string>>
                {
                    [gameId] = new Dictionary<string, string>
                    {
                        ["ach_one"] = "Category"
                    }
                },
                AchievementCategoryTypeOverrides = new Dictionary<Guid, Dictionary<string, string>>
                {
                    [gameId] = new Dictionary<string, string>
                    {
                        ["ach_one"] = "DLC"
                    }
                }
            };

            var json = JsonConvert.SerializeObject(persisted);

            Assert.IsTrue(json.Contains(nameof(PersistedSettings.GlobalLanguage)));
            Assert.IsFalse(json.Contains(nameof(PersistedSettings.ExcludedGameIds)));
            Assert.IsFalse(json.Contains(nameof(PersistedSettings.ExcludedFromSummariesGameIds)));
            Assert.IsFalse(json.Contains(nameof(PersistedSettings.SeparateLockedIconEnabledGameIds)));
            Assert.IsFalse(json.Contains(nameof(PersistedSettings.ManualCapstones)));
            Assert.IsFalse(json.Contains(nameof(PersistedSettings.AchievementOrderOverrides)));
            Assert.IsFalse(json.Contains(nameof(PersistedSettings.AchievementCategoryOverrides)));
            Assert.IsFalse(json.Contains(nameof(PersistedSettings.AchievementCategoryTypeOverrides)));
        }

        [TestMethod]
        public void ProviderSettingsSerialization_OmitsRuntimeProjectedCustomDataFields()
        {
            var gameId = Guid.NewGuid();

            var raSettings = new RetroAchievementsSettings
            {
                RaUsername = "user",
                RaGameIdOverrides = new Dictionary<Guid, int>
                {
                    [gameId] = 1234
                }
            };
            var exophaseSettings = new ExophaseSettings
            {
                UserId = "user",
                ManagedProviders = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "origin" },
                IncludedGames = new HashSet<Guid> { gameId },
                SlugOverrides = new Dictionary<Guid, string>
                {
                    [gameId] = "slug"
                }
            };
            var manualSettings = new ManualSettings
            {
                ManualTrackingOverrideEnabled = true,
                AchievementLinks = new Dictionary<Guid, ManualAchievementLink>
                {
                    [gameId] = new ManualAchievementLink
                    {
                        SourceKey = "Steam",
                        SourceGameId = "123"
                    }
                }
            };

            var raJson = raSettings.SerializeToJson();
            var exophaseJson = exophaseSettings.SerializeToJson();
            var manualJson = manualSettings.SerializeToJson();

            Assert.IsTrue(raJson.Contains(nameof(RetroAchievementsSettings.RaUsername)));
            Assert.IsFalse(raJson.Contains(nameof(RetroAchievementsSettings.RaGameIdOverrides)));

            Assert.IsTrue(exophaseJson.Contains(nameof(ExophaseSettings.ManagedProviders)));
            Assert.IsFalse(exophaseJson.Contains(nameof(ExophaseSettings.IncludedGames)));
            Assert.IsFalse(exophaseJson.Contains(nameof(ExophaseSettings.SlugOverrides)));

            Assert.IsTrue(manualJson.Contains(nameof(ManualSettings.ManualTrackingOverrideEnabled)));
            Assert.IsFalse(manualJson.Contains(nameof(ManualSettings.AchievementLinks)));
        }

        private static string CreateTempDirectory()
        {
            var path = Path.Combine(
                Path.GetTempPath(),
                "PlayniteAchievementsTests",
                Guid.NewGuid().ToString("N"));
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

        private static void WritePlaceholderFile(string path)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4 });
        }

        private static void WritePackageImageEntry(ZipArchive archive, string entryName)
        {
            using (var stream = archive.CreateEntry(entryName, CompressionLevel.Optimal).Open())
            {
                var pngBytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIW2NkYGD4DwABBAEAgh8sXQAAAABJRU5ErkJggg==");
                stream.Write(pngBytes, 0, pngBytes.Length);
            }
        }
    }
}
