using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Workshop;
using System;
using System.Collections.Generic;
using System.IO;

namespace PlayniteAchievements.Services.Tests
{
    [TestClass]
    [DoNotParallelize]
    public class WorkshopBaselineStoreTests
    {
        [TestMethod]
        public void Write_ThenLoad_RoundTripsTheData()
        {
            WithTemp(dir =>
            {
                var store = new WorkshopBaselineStore(Path.Combine(dir, "baselines"), logger: null);
                var gameId = Guid.NewGuid();
                var data = new GameCustomDataFile
                {
                    PlayniteGameId = gameId,
                    AchievementNotes = new Dictionary<string, string> { ["ach_one"] = "a note" }
                };

                var path = store.Write("game-data/steam-1/pack", gameId, data, iconDirectory: null);
                Assert.IsNotNull(path);
                Assert.IsTrue(File.Exists(path));
                StringAssert.StartsWith(path, Path.Combine(dir, "baselines"));
                Assert.IsFalse(Path.GetFileName(path).Contains("/"), "the item id's slashes are replaced");

                var loaded = store.Load(path);
                Assert.IsNotNull(loaded);
                Assert.AreEqual(gameId, loaded.PlayniteGameId);
                Assert.AreEqual("a note", loaded.AchievementNotes["ach_one"]);
            });
        }

        [TestMethod]
        public void Load_Schema8Baseline_IsUpgradedToTheCurrentSchema()
        {
            WithTemp(dir =>
            {
                // A baseline 4.1.x wrote: the type override still on the record and in its mirror map.
                var path = Path.Combine(dir, "old.json");
                File.WriteAllText(path,
                    "{\"SchemaVersion\":8," +
                    "\"AchievementOverrides\":{\"ach_one\":{\"Category\":\"Expansion\",\"CategoryType\":\"DLC|Missable\"}}," +
                    "\"AchievementCategoryTypeOverrides\":{\"ach_one\":\"DLC|Missable\"}}");

                var loaded = new WorkshopBaselineStore(dir, logger: null).Load(path);

                Assert.IsNotNull(loaded);
                Assert.AreEqual(9, loaded.SchemaVersion);
                Assert.IsNull(loaded.AchievementCategoryTypeOverrides);
                Assert.IsNull(loaded.AchievementOverrides["ach_one"].LegacyCategoryType);
                Assert.AreEqual("Missable", loaded.AchievementOverrides["ach_one"].Tags);
                Assert.AreEqual("DLC", loaded.AchievementCategoryKinds["Expansion"]);
            });
        }

        [TestMethod]
        public void Load_MissingOrUnreadable_ReturnsNull()
        {
            WithTemp(dir =>
            {
                var store = new WorkshopBaselineStore(Path.Combine(dir, "baselines"), logger: null);
                Assert.IsNull(store.Load(null));
                Assert.IsNull(store.Load(string.Empty));
                Assert.IsNull(store.Load(Path.Combine(dir, "absent.json")));

                var broken = Path.Combine(dir, "broken.json");
                File.WriteAllText(broken, "{ not json");
                Assert.IsNull(store.Load(broken));
            });
        }

        [TestMethod]
        public void Write_RecordsIconHashesNextToTheBaseline()
        {
            WithTemp(dir =>
            {
                var store = new WorkshopBaselineStore(Path.Combine(dir, "baselines"), logger: null);
                var gameId = Guid.NewGuid();
                var icons = Path.Combine(dir, "icons");
                Directory.CreateDirectory(Path.Combine(icons, "sub"));
                File.WriteAllBytes(Path.Combine(icons, "a.png"), new byte[] { 1, 2, 3 });
                File.WriteAllBytes(Path.Combine(icons, "sub", "b.png"), new byte[] { 4, 5, 6 });

                var path = store.Write("item", gameId, new GameCustomDataFile { PlayniteGameId = gameId }, icons);
                Assert.IsNotNull(path);
                Assert.IsTrue(File.Exists(WorkshopBaselineStore.IconHashesPath(path)));
                Assert.AreEqual(path + ".icons.json", WorkshopBaselineStore.IconHashesPath(path));

                var hashes = store.LoadIconHashes(path);
                Assert.IsNotNull(hashes);
                Assert.AreEqual(2, hashes.Count);
                Assert.AreEqual(WorkshopBaselineStore.HashIcons(icons)["a.png"], hashes["a.png"]);
                Assert.IsTrue(hashes.ContainsKey(Path.Combine("sub", "b.png")));
                Assert.AreNotEqual(hashes["a.png"], hashes[Path.Combine("sub", "b.png")]);
            });
        }

        [TestMethod]
        public void LoadIconHashes_WithoutHashesFile_ReturnsNull()
        {
            WithTemp(dir =>
            {
                var store = new WorkshopBaselineStore(Path.Combine(dir, "baselines"), logger: null);
                Assert.IsNull(store.LoadIconHashes(null));
                Assert.IsNull(store.LoadIconHashes(Path.Combine(dir, "none.json")));
                Assert.AreEqual(0, WorkshopBaselineStore.HashIcons(Path.Combine(dir, "no-such-folder")).Count);
            });
        }

        private static void WithTemp(Action<string> body)
        {
            var dir = Path.Combine(Path.GetTempPath(), "PlayniteAchievementsTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                body(dir);
            }
            finally
            {
                try
                {
                    Directory.Delete(dir, recursive: true);
                }
                catch
                {
                }
            }
        }
    }
}
