using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.GameCustomData;
using SqlNado;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PlayniteAchievements.Services.Tests
{
    /// <summary>
    /// Reads upgrade a record to the current schema on the way out, but nothing wrote that back
    /// unless the game was edited afterwards - so a game the user never touched kept its old
    /// version indefinitely, and any such record armed a full database backup on every launch.
    /// </summary>
    [TestClass]
    [DoNotParallelize]
    public class GameCustomDataSchemaUpgradeTests
    {
        [TestMethod]
        public void AnOldRecord_IsRewrittenAtTheCurrentSchemaWithItsDataIntact()
        {
            var tempDir = CreateTempDirectory();
            var gameId = Guid.NewGuid();

            try
            {
                var store = new GameCustomDataStore(tempDir);
                store.Save(gameId, new GameCustomDataFile
                {
                    PlayniteGameId = gameId,
                    ManualCapstoneApiName = "ach_final",
                    AchievementOrder = new List<string> { "ach_one", "ach_two" },
                    AchievementNotes = new Dictionary<string, string> { ["ach_one"] = "a note" }
                });

                StampSchemaVersionOnDisk(store.DatabasePath, gameId, 1);
                Assert.AreEqual(1, ReadSchemaVersionOnDisk(store.DatabasePath, gameId));

                // The invariant the whole sweep rests on: it writes back what a read was already
                // producing, so what a caller sees cannot change. Compared as the whole record
                // rather than field by field, because a fold that relocates a value - the legacy
                // capstone moving into Capstones, for one - would slip past a named-field check.
                var before = Serialize(new GameCustomDataStore(tempDir).LoadOrDefault(gameId));

                var upgraded = new GameCustomDataStore(tempDir).UpgradeStoredRecordsToCurrentSchema();

                Assert.AreEqual(1, upgraded, "The one stale record is the one that should be rewritten.");
                Assert.AreEqual(
                    CurrentSchemaVersion,
                    ReadSchemaVersionOnDisk(store.DatabasePath, gameId),
                    "The upgrade has to reach the stored bytes, or the backup arms again next launch.");

                var after = new GameCustomDataStore(tempDir).LoadOrDefault(gameId);
                Assert.AreEqual(before, Serialize(after), "The sweep must not change what a read returns.");

                // And the authorship is actually still there, at its post-fold home.
                Assert.AreEqual("ach_final", after.Capstones.Single().ApiName);
                CollectionAssert.AreEqual(new[] { "ach_one", "ach_two" }, after.AchievementOrder.ToList());
                Assert.AreEqual("a note", after.AchievementNotes["ach_one"]);
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public void ARecordAlreadyCurrent_IsLeftAlone()
        {
            var tempDir = CreateTempDirectory();
            var gameId = Guid.NewGuid();

            try
            {
                var store = new GameCustomDataStore(tempDir);
                store.Save(gameId, new GameCustomDataFile
                {
                    PlayniteGameId = gameId,
                    ManualCapstoneApiName = "ach_final"
                });

                // Untouched bytes cannot be damaged by a defect in the sweep, so a record already
                // at the current version must not be rewritten at all.
                var before = ReadPayloadOnDisk(store.DatabasePath, gameId);
                var upgraded = new GameCustomDataStore(tempDir).UpgradeStoredRecordsToCurrentSchema();

                Assert.AreEqual(0, upgraded);
                Assert.AreEqual(before, ReadPayloadOnDisk(store.DatabasePath, gameId));
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public void RunningItTwice_FindsNothingTheSecondTime()
        {
            var tempDir = CreateTempDirectory();
            var gameId = Guid.NewGuid();

            try
            {
                var store = new GameCustomDataStore(tempDir);
                store.Save(gameId, new GameCustomDataFile
                {
                    PlayniteGameId = gameId,
                    AchievementOrder = new List<string> { "ach_one" }
                });
                StampSchemaVersionOnDisk(store.DatabasePath, gameId, 1);

                Assert.AreEqual(1, new GameCustomDataStore(tempDir).UpgradeStoredRecordsToCurrentSchema());

                // The launch after the upgrade is the one that has to come back clean: that is
                // what stops the migration backup being taken forever.
                Assert.AreEqual(0, new GameCustomDataStore(tempDir).UpgradeStoredRecordsToCurrentSchema());
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public void NoDatabase_IsNotAnError()
        {
            var tempDir = CreateTempDirectory();

            try
            {
                Assert.AreEqual(0, new GameCustomDataStore(tempDir).UpgradeStoredRecordsToCurrentSchema());
                Assert.IsFalse(
                    File.Exists(Path.Combine(tempDir, "game_custom_data.db")),
                    "A fresh install has nothing to upgrade and should not be given a database for it.");
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public void TheStoredPayload_IsWrittenCompact()
        {
            var tempDir = CreateTempDirectory();
            var gameId = Guid.NewGuid();

            try
            {
                var store = new GameCustomDataStore(tempDir);
                store.Save(gameId, new GameCustomDataFile
                {
                    PlayniteGameId = gameId,
                    AchievementOrder = new List<string> { "ach_one", "ach_two" },
                    AchievementNotes = new Dictionary<string, string> { ["ach_one"] = "a note" }
                });

                var payload = ReadPayloadOnDisk(store.DatabasePath, gameId);

                // The payload is only ever round-tripped through JsonConvert, so its indentation
                // was whitespace serialized on every per-game save and carried into the WAL. The
                // serialize scales with the game's override count, which is the profile that made
                // editing a heavily customized game slow.
                Assert.IsFalse(
                    payload.Contains("\n"),
                    "The stored blob must be compact; indenting it costs bytes and serialize time " +
                    "on every save and buys nothing, because nothing reads it as text.");

                // Still a faithful record, not just a short one.
                var reloaded = new GameCustomDataStore(tempDir).LoadOrDefault(gameId);
                CollectionAssert.AreEqual(
                    new[] { "ach_one", "ach_two" },
                    reloaded.AchievementOrder.ToArray());
                Assert.AreEqual("a note", reloaded.AchievementNotes["ach_one"]);
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        // The sources are compiled into this assembly, so the test reads the constant itself
        // rather than restating a number that would drift the next time the schema moves.
        private static int CurrentSchemaVersion => GameCustomDataNormalizer.CurrentSchemaVersion;

        private static void StampSchemaVersionOnDisk(string databasePath, Guid gameId, int schemaVersion)
        {
            var payload = JObject.Parse(ReadPayloadOnDisk(databasePath, gameId));
            payload["SchemaVersion"] = schemaVersion;

            using (var db = new SQLiteDatabase(databasePath))
            {
                db.ExecuteNonQuery(
                    "UPDATE GameCustomData SET PayloadJson = ? WHERE PlayniteGameId = ?;",
                    payload.ToString(Newtonsoft.Json.Formatting.None),
                    gameId.ToString("D"));
            }
        }

        private static int ReadSchemaVersionOnDisk(string databasePath, Guid gameId)
        {
            return JObject.Parse(ReadPayloadOnDisk(databasePath, gameId)).Value<int>("SchemaVersion");
        }

        private static string Serialize(GameCustomDataFile data)
        {
            return Newtonsoft.Json.JsonConvert.SerializeObject(data, Newtonsoft.Json.Formatting.None);
        }

        private sealed class PayloadRow
        {
            public string PayloadJson { get; set; }
        }

        private static string ReadPayloadOnDisk(string databasePath, Guid gameId)
        {
            using (var db = new SQLiteDatabase(databasePath))
            {
                return db.Load<PayloadRow>(
                        "SELECT PayloadJson FROM GameCustomData WHERE PlayniteGameId = ? LIMIT 1;",
                        gameId.ToString("D"))
                    .Select(row => row.PayloadJson)
                    .FirstOrDefault();
            }
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

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch
            {
                // A locked temp directory is not a test failure.
            }
        }
    }
}
