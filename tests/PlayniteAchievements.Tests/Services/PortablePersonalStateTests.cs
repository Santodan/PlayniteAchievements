using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.GameCustomData;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace PlayniteAchievements.Services.Tests
{
    [TestClass]
    [DoNotParallelize]
    public class PortablePersonalStateTests
    {
        private static readonly DateTime UnlockTime = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        private static readonly DateTime OtherUnlockTime = new DateTime(2025, 6, 7, 8, 9, 10, DateTimeKind.Utc);

        [TestMethod]
        public void ExportPortablePackage_OmitsPersonalStateAndKeepsCuration()
        {
            WithStore((store, tempDir) =>
            {
                var gameId = Guid.NewGuid();
                store.Save(gameId, BuildRecordWithPersonalState(gameId, "480", UnlockTime));

                var packagePath = Path.Combine(tempDir, "game.pa");
                store.ExportPortablePackage(gameId, packagePath);

                string manifestText;
                using (var archive = ZipFile.OpenRead(packagePath))
                using (var reader = new StreamReader(archive.GetEntry(GameCustomDataStore.PortablePackageManifestEntryName).Open()))
                {
                    manifestText = reader.ReadToEnd();
                }

                StringAssert.DoesNotMatch(manifestText, new System.Text.RegularExpressions.Regex("2026-01-02"));
                var portable = JsonConvert.DeserializeObject<GameCustomDataPortableFile>(manifestText);
                Assert.IsNull(portable.GoalAchievementApiNames);
                Assert.AreEqual("Steam", portable.ManualLink.SourceKey);
                Assert.AreEqual("480", portable.ManualLink.SourceGameId);
                Assert.AreEqual(0, portable.ManualLink.UnlockStates?.Count ?? 0);
                Assert.AreEqual(0, portable.ManualLink.UnlockTimes?.Count ?? 0);
                Assert.AreEqual("note", portable.AchievementOverrides["ach_one"].Note);
                Assert.IsNull(portable.AchievementOverrides["ach_one"].UnlockTimeUtc);
                Assert.IsFalse(portable.AchievementOverrides.ContainsKey("ach_two"), "An override holding only an unlock time is not exported.");
                CollectionAssert.AreEqual(new[] { "ach_one" }, portable.FilteredAchievementApiNames);

                var custom = portable.CustomAchievements.Single();
                Assert.IsFalse(custom.Unlocked);
                Assert.IsNull(custom.UnlockTimeUtc);
                Assert.IsNull(custom.ProgressNum);
                Assert.AreEqual(3, custom.ProgressDenom);
            });
        }

        [TestMethod]
        public void ImportReplacePortable_OverSameGame_KeepsLocalPersonalState()
        {
            WithStore((store, tempDir) =>
            {
                var gameId = Guid.NewGuid();
                store.Save(gameId, BuildRecordWithPersonalState(gameId, "480", UnlockTime));
                var packagePath = Path.Combine(tempDir, "game.pa");
                store.ExportPortablePackage(gameId, packagePath);

                var imported = store.ImportReplacePortable(gameId, packagePath).ImportedData;

                CollectionAssert.AreEqual(new[] { "ach_one" }, imported.GoalAchievementApiNames);
                Assert.IsTrue(imported.ManualLink.UnlockStates["ach_one"]);
                Assert.AreEqual(UnlockTime, imported.ManualLink.UnlockTimes["ach_one"]);
                Assert.AreEqual(UnlockTime, imported.AchievementOverrides["ach_one"].UnlockTimeUtc);
                Assert.AreEqual("note", imported.AchievementOverrides["ach_one"].Note);
                Assert.AreEqual(UnlockTime, imported.AchievementOverrides["ach_two"].UnlockTimeUtc);
                var custom = imported.CustomAchievements.Single();
                Assert.IsTrue(custom.Unlocked);
                Assert.AreEqual(UnlockTime, custom.UnlockTimeUtc);
                Assert.AreEqual(1, custom.ProgressNum);
            });
        }

        [TestMethod]
        public void ImportReplacePortable_LegacyPackageWithUnlocks_BringsNoPersonalState()
        {
            WithStore((store, tempDir) =>
            {
                var exporterId = Guid.NewGuid();
                var targetId = Guid.NewGuid();
                var legacy = BuildRecordWithPersonalState(exporterId, "480", OtherUnlockTime).ToPortable();
                var packagePath = Path.Combine(tempDir, "legacy.pa");
                using (var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create))
                using (var writer = new StreamWriter(archive.CreateEntry(GameCustomDataStore.PortablePackageManifestEntryName).Open()))
                {
                    writer.Write(JsonConvert.SerializeObject(legacy));
                }

                // The target tracks a different source game, so its own unlocks do not carry over
                // onto the package's link either.
                store.Save(targetId, BuildRecordWithPersonalState(targetId, "999", UnlockTime));
                var imported = store.ImportReplacePortable(targetId, packagePath).ImportedData;

                Assert.AreEqual("480", imported.ManualLink.SourceGameId);
                Assert.AreEqual(0, imported.ManualLink.UnlockStates?.Count ?? 0);
                Assert.AreEqual(0, imported.ManualLink.UnlockTimes?.Count ?? 0);
                Assert.AreEqual(UnlockTime, imported.AchievementOverrides["ach_one"].UnlockTimeUtc, "The local override time wins over the package's.");
                Assert.AreEqual(UnlockTime, imported.CustomAchievements.Single().UnlockTimeUtc);

                var freshId = Guid.NewGuid();
                var fresh = store.ImportReplacePortable(freshId, packagePath).ImportedData;
                Assert.IsNull(fresh.GoalAchievementApiNames);
                Assert.AreEqual(0, fresh.ManualLink.UnlockStates?.Count ?? 0);
                Assert.IsNull(fresh.AchievementOverrides["ach_one"].UnlockTimeUtc);
                Assert.AreEqual("note", fresh.AchievementOverrides["ach_one"].Note);
                Assert.IsFalse(fresh.AchievementOverrides.ContainsKey("ach_two"));
                Assert.IsFalse(fresh.CustomAchievements.Single().Unlocked);
                Assert.IsNull(fresh.CustomAchievements.Single().ProgressNum);
            });
        }

        [TestMethod]
        public void ImportCustomAchievementsPackage_LegacyCsvWithUnlocks_ImportsLocked()
        {
            WithStore((store, tempDir) =>
            {
                var packagePath = Path.Combine(tempDir, "legacy-custom.pa");
                using (var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create))
                using (var writer = new StreamWriter(archive.CreateEntry(GameCustomDataStore.CustomAchievementsPackageCsvEntryName).Open()))
                {
                    writer.WriteLine("ID,Title,Description,Unlocked,Unlock Time (UTC),Points,Trophy Type,Hidden,Rarity,Global Percent,Progress,Progress Total,Unlocked Icon,Locked Icon");
                    writer.WriteLine("c1,C1,,true,2026-01-02T03:04:05Z,10,,false,,,1,3,,");
                }

                var result = store.ImportCustomAchievementsPackage(Guid.NewGuid(), packagePath);

                Assert.IsFalse(result.HasErrors, string.Join("; ", result.Errors));
                var definition = result.Definitions.Single();
                Assert.IsFalse(definition.Unlocked);
                Assert.IsNull(definition.UnlockTimeUtc);
                Assert.IsNull(definition.ProgressNum);
                Assert.AreEqual(3, definition.ProgressDenom);
                Assert.AreEqual(10, definition.Points);
            });
        }

        [TestMethod]
        public void CarryLocal_Definition_CopiesOnlyPersonalFields()
        {
            var local = new CustomAchievementDefinition { Id = "c1", DisplayName = "Local", Unlocked = true, UnlockTimeUtc = UnlockTime, ProgressNum = 2 };
            var incoming = new CustomAchievementDefinition { Id = "c1", DisplayName = "Incoming", ProgressDenom = 5 };

            PortablePersonalState.CarryLocal(local, incoming);

            Assert.AreEqual("Incoming", incoming.DisplayName);
            Assert.IsTrue(incoming.Unlocked);
            Assert.AreEqual(UnlockTime, incoming.UnlockTimeUtc);
            Assert.AreEqual(2, incoming.ProgressNum);
            Assert.AreEqual(5, incoming.ProgressDenom);
        }

        private static GameCustomDataFile BuildRecordWithPersonalState(Guid gameId, string sourceGameId, DateTime unlockTime)
        {
            return new GameCustomDataFile
            {
                PlayniteGameId = gameId,
                FilteredAchievementApiNames = new List<string> { "ach_one" },
                GoalAchievementApiNames = new List<string> { "ach_one" },
                AchievementOverrides = new Dictionary<string, AchievementOverride>(StringComparer.OrdinalIgnoreCase)
                {
                    ["ach_one"] = new AchievementOverride { Note = "note", UnlockTimeUtc = unlockTime },
                    ["ach_two"] = new AchievementOverride { UnlockTimeUtc = unlockTime }
                },
                ManualLink = new ManualAchievementLink
                {
                    SourceKey = "Steam",
                    SourceGameId = sourceGameId,
                    UnlockStates = new Dictionary<string, bool> { ["ach_one"] = true },
                    UnlockTimes = new Dictionary<string, DateTime?> { ["ach_one"] = unlockTime },
                    CreatedUtc = unlockTime,
                    LastModifiedUtc = unlockTime
                },
                CustomAchievements = new List<CustomAchievementDefinition>
                {
                    new CustomAchievementDefinition
                    {
                        Id = "c1",
                        DisplayName = "C1",
                        Unlocked = true,
                        UnlockTimeUtc = unlockTime,
                        ProgressNum = 1,
                        ProgressDenom = 3
                    }
                }
            };
        }

        private static void WithStore(Action<GameCustomDataStore, string> test)
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "PlayniteAchievementsTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                test(new GameCustomDataStore(tempDir), tempDir);
            }
            finally
            {
                try
                {
                    Directory.Delete(tempDir, recursive: true);
                }
                catch
                {
                }
            }
        }
    }
}
