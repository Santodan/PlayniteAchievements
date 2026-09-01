using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Providers.GOG.Local;
using SqlNado;
using System;
using System.IO;
using System.Linq;

namespace PlayniteAchievements.Providers.Tests
{
    [TestClass]
    public class GogGalaxyDbReaderTests
    {
        private const string ReleaseKey = "gog_1073977251";
        private const long UserId = 42;

        [TestMethod]
        public void TryRead_ReturnsOnlyUnlockedRowsForReleaseAndUser()
        {
            var directory = CreateTempDirectory();
            try
            {
                var databasePath = Path.Combine(directory, "galaxy-2.0.db");
                CreateFixtureDatabase(databasePath, db =>
                {
                    InsertRow(db, ReleaseKey, UserId, "ach_one", "2026-01-02 03:04:05", 1);
                    InsertRow(db, ReleaseKey, UserId, "ach_locked", null, 0);
                    InsertRow(db, ReleaseKey, 99, "ach_other_user", "2026-01-02 03:04:05", 1);
                    InsertRow(db, "gog_999", UserId, "ach_other_game", "2026-01-02 03:04:05", 1);
                });

                var reader = new GogGalaxyDbReader(null);
                Assert.IsTrue(reader.TryRead(databasePath, ReleaseKey, UserId, out var observations));
                Assert.AreEqual(1, observations.Count);

                var observation = observations.Single();
                Assert.AreEqual("ach_one", observation.ApiName);
                Assert.IsTrue(observation.Unlocked);
                Assert.AreEqual(
                    new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
                    observation.UnlockTimeUtc);
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        [TestMethod]
        public void TryRead_SucceedsWhileAnotherConnectionHoldsTheDatabase()
        {
            var directory = CreateTempDirectory();
            try
            {
                var databasePath = Path.Combine(directory, "galaxy-2.0.db");
                CreateFixtureDatabase(databasePath, db =>
                {
                    InsertRow(db, ReleaseKey, UserId, "ach_one", "2026-01-02 03:04:05", 1);
                });

                using (var writer = OpenWritable(databasePath))
                {
                    var reader = new GogGalaxyDbReader(null);
                    Assert.IsTrue(reader.TryRead(databasePath, ReleaseKey, UserId, out var observations));
                    Assert.AreEqual(1, observations.Count);
                }
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        [TestMethod]
        public void TryRead_FailsForMissingDatabase()
        {
            var reader = new GogGalaxyDbReader(null);
            Assert.IsFalse(reader.TryRead(
                Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "galaxy-2.0.db"),
                ReleaseKey,
                UserId,
                out _));
        }

        [TestMethod]
        public void TryRead_FailsForDatabaseWithoutUserAchievementsTable()
        {
            var directory = CreateTempDirectory();
            try
            {
                var databasePath = Path.Combine(directory, "galaxy-2.0.db");
                using (var db = OpenWritable(databasePath))
                {
                    db.ExecuteNonQuery("CREATE TABLE 'Unrelated'('id' INTEGER);");
                }

                var reader = new GogGalaxyDbReader(null);
                Assert.IsFalse(reader.TryRead(databasePath, ReleaseKey, UserId, out _));
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        [TestMethod]
        public void ParseUnlockTime_HandlesGalaxyTextEpochAndGarbage()
        {
            Assert.AreEqual(
                new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
                GogGalaxyDbReader.ParseUnlockTime("2026-01-02 03:04:05"));
            Assert.AreEqual(
                DateTimeOffset.FromUnixTimeSeconds(1700000000).UtcDateTime,
                GogGalaxyDbReader.ParseUnlockTime("1700000000"));
            Assert.IsNull(GogGalaxyDbReader.ParseUnlockTime(null));
            Assert.IsNull(GogGalaxyDbReader.ParseUnlockTime(" "));
            Assert.IsNull(GogGalaxyDbReader.ParseUnlockTime("not a time"));
        }

        private static string CreateTempDirectory()
        {
            var directory = Path.Combine(Path.GetTempPath(), "GogGalaxyDbReaderTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            return directory;
        }

        private static SQLiteDatabase OpenWritable(string databasePath)
        {
            return new SQLiteDatabase(
                databasePath,
                SQLiteOpenOptions.SQLITE_OPEN_READWRITE |
                SQLiteOpenOptions.SQLITE_OPEN_CREATE |
                SQLiteOpenOptions.SQLITE_OPEN_FULLMUTEX);
        }

        private static void CreateFixtureDatabase(string databasePath, Action<SQLiteDatabase> populate)
        {
            using (var db = OpenWritable(databasePath))
            {
                db.ExecuteNonQuery(
                    @"CREATE TABLE 'UserAchievements'(
                        'gameReleaseKey' TEXT NOT NULL,
                        'userId' INT64 NOT NULL,
                        'apikey' TEXT NOT NULL,
                        'unlockTime' TEXT NULL,
                        'isUnlocked' BOOLEAN NOT NULL);");
                populate(db);
            }
        }

        private static void InsertRow(
            SQLiteDatabase db,
            string releaseKey,
            long userId,
            string apiKey,
            string unlockTime,
            long isUnlocked)
        {
            db.ExecuteNonQuery(
                @"INSERT INTO UserAchievements (gameReleaseKey, userId, apikey, unlockTime, isUnlocked)
                  VALUES (?, ?, ?, ?, ?);",
                releaseKey,
                userId,
                apiKey,
                unlockTime,
                isUnlocked);
        }
    }
}
