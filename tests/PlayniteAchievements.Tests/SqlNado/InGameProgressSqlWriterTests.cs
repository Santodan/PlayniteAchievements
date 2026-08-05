using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Services.Database;
using SqlNado;
using System;
using System.IO;
using System.Linq;

namespace PlayniteAchievements.SqlNado.Tests
{
    [TestClass]
    public class InGameProgressSqlWriterTests
    {
        [TestMethod]
        public void Apply_IsMonotonic_PreservesSchemaAndIcons_AndReportsUnmatched()
        {
            WithSeededDatabase(db =>
            {
                var unlockTime = new DateTime(
                    2026,
                    7,
                    28,
                    12,
                    0,
                    0,
                    DateTimeKind.Utc);
                var result = InGameProgressSqlWriter.Apply(
                    db,
                    "game-key",
                    "Steam",
                    new[]
                    {
                        new AchievementProgressObservation
                        {
                            ApiName = "ACH_OLD",
                            Unlocked = false,
                            UnlockTimeUtc = unlockTime.AddDays(-10),
                            ProgressNum = 5,
                            ProgressDenom = 50
                        },
                        new AchievementProgressObservation
                        {
                            ApiName = "ACH_NEW",
                            Unlocked = true,
                            UnlockTimeUtc = unlockTime,
                            ProgressNum = 1,
                            ProgressDenom = 10
                        },
                        new AchievementProgressObservation
                        {
                            ApiName = "ACH_PROGRESS",
                            ProgressNum = 2,
                            ProgressDenom = 10
                        },
                        new AchievementProgressObservation
                        {
                            ApiName = "UNKNOWN",
                            Unlocked = true
                        }
                    });

                Assert.IsTrue(result.Success);
                Assert.IsTrue(result.Changed);
                CollectionAssert.AreEquivalent(
                    new[] { "ACH_OLD", "ACH_NEW", "ACH_PROGRESS" },
                    result.MatchedKeys.ToArray());
                CollectionAssert.AreEqual(
                    new[] { "UNKNOWN" },
                    result.UnmatchedKeys.ToArray());
                CollectionAssert.AreEqual(
                    new[] { "ACH_NEW" },
                    result.NewlyUnlockedKeys.ToArray());

                Assert.AreEqual(
                    3L,
                    db.ExecuteScalar<long>("SELECT COUNT(*) FROM AchievementDefinitions;"));
                Assert.AreEqual(
                    "Old Name|old-unlocked.png|old-locked.png|12.5|Base",
                    db.ExecuteScalar<string>(
                        @"SELECT DisplayName || '|' || UnlockedIconPath || '|' ||
                                 LockedIconPath || '|' || GlobalPercentUnlocked || '|' ||
                                 CategoryType
                          FROM AchievementDefinitions
                          WHERE ApiName = 'ACH_OLD';"));
                Assert.AreEqual(
                    2L,
                    db.ExecuteScalar<long>(
                        "SELECT AchievementsUnlocked FROM UserGameProgress WHERE Id = 20;"));

                Assert.AreEqual(
                    "1|2026-07-28T12:00:00.0000000Z|1|10",
                    db.ExecuteScalar<string>(
                        @"SELECT Unlocked || '|' || UnlockTimeUtc || '|' ||
                                 ProgressNum || '|' || ProgressDenom
                          FROM UserAchievements
                          WHERE AchievementDefinitionId = 101;"));
                Assert.AreEqual(
                    "0|2|10",
                    db.ExecuteScalar<string>(
                        @"SELECT Unlocked || '|' || ProgressNum || '|' || ProgressDenom
                          FROM UserAchievements
                          WHERE AchievementDefinitionId = 102;"));

                var monotonic = InGameProgressSqlWriter.Apply(
                    db,
                    "game-key",
                    "Steam",
                    new[]
                    {
                        new AchievementProgressObservation
                        {
                            ApiName = "ACH_NEW",
                            Unlocked = false,
                            UnlockTimeUtc = unlockTime.AddDays(-1),
                            ProgressNum = 0,
                            ProgressDenom = 5
                        }
                    });

                Assert.IsTrue(monotonic.Success);
                Assert.IsFalse(monotonic.Changed);
                Assert.AreEqual(
                    "1|2026-07-28T12:00:00.0000000Z|1|10",
                    db.ExecuteScalar<string>(
                        @"SELECT Unlocked || '|' || UnlockTimeUtc || '|' ||
                                 ProgressNum || '|' || ProgressDenom
                          FROM UserAchievements
                          WHERE AchievementDefinitionId = 101;"));
            });
        }

        [TestMethod]
        public void Apply_RetroAchievementsMode_ReplacesOnlyModeToken()
        {
            WithSeededDatabase(db =>
            {
                db.ExecuteNonQuery(
                    "UPDATE Games SET ProviderKey = 'RetroAchievements' WHERE Id = 10;");
                db.ExecuteNonQuery(
                    @"UPDATE AchievementDefinitions
                      SET CategoryType = 'Subset|Softcore'
                      WHERE ApiName = 'ACH_NEW';");

                var result = InGameProgressSqlWriter.Apply(
                    db,
                    "game-key",
                    "RetroAchievements",
                    new[]
                    {
                        new AchievementProgressObservation
                        {
                            ApiName = "ACH_NEW",
                            Unlocked = true,
                            UnlockMode = "Hardcore"
                        }
                    });

                Assert.IsTrue(result.Success);
                var categoryType = db.ExecuteScalar<string>(
                    @"SELECT CategoryType
                      FROM AchievementDefinitions
                      WHERE ApiName = 'ACH_NEW';");
                StringAssert.Contains(categoryType, "Subset");
                StringAssert.Contains(categoryType, "Hardcore");
                Assert.IsFalse(
                    categoryType.IndexOf(
                        "Softcore",
                        StringComparison.OrdinalIgnoreCase) >= 0);
            });
        }

        [TestMethod]
        public void Apply_MissingSchema_ReturnsFailureWithoutCreatingRows()
        {
            WithSeededDatabase(db =>
            {
                var beforeDefinitions = db.ExecuteScalar<long>(
                    "SELECT COUNT(*) FROM AchievementDefinitions;");
                var beforeProgress = db.ExecuteScalar<long>(
                    "SELECT COUNT(*) FROM UserAchievements;");

                var result = InGameProgressSqlWriter.Apply(
                    db,
                    "missing-key",
                    "Steam",
                    new[]
                    {
                        new AchievementProgressObservation
                        {
                            ApiName = "ACH_NEW",
                            Unlocked = true
                        }
                    });

                Assert.IsFalse(result.Success);
                Assert.AreEqual("schema_missing", result.ErrorCode);
                Assert.AreEqual(
                    beforeDefinitions,
                    db.ExecuteScalar<long>(
                        "SELECT COUNT(*) FROM AchievementDefinitions;"));
                Assert.AreEqual(
                    beforeProgress,
                    db.ExecuteScalar<long>(
                        "SELECT COUNT(*) FROM UserAchievements;"));
            });
        }

        private static void WithSeededDatabase(Action<SQLiteDatabase> action)
        {
            var path = Path.Combine(
                Path.GetTempPath(),
                "playach-in-game-progress-" + Guid.NewGuid().ToString("N") + ".db");
            try
            {
                using (var db = new SQLiteDatabase(
                    path,
                    SQLiteOpenOptions.SQLITE_OPEN_READWRITE |
                    SQLiteOpenOptions.SQLITE_OPEN_CREATE |
                    SQLiteOpenOptions.SQLITE_OPEN_FULLMUTEX))
                {
                    CreateSchema(db);
                    Seed(db);
                    action(db);
                }
            }
            finally
            {
                foreach (var file in new[] { path, path + "-wal", path + "-shm" })
                {
                    if (File.Exists(file))
                    {
                        File.Delete(file);
                    }
                }
            }
        }

        private static void CreateSchema(SQLiteDatabase db)
        {
            db.ExecuteNonQuery(
                @"CREATE TABLE Users (
                    Id INTEGER PRIMARY KEY,
                    IsCurrentUser INTEGER NOT NULL
                  );");
            db.ExecuteNonQuery(
                @"CREATE TABLE Games (
                    Id INTEGER PRIMARY KEY,
                    ProviderKey TEXT NOT NULL,
                    LastUpdatedUtc TEXT
                  );");
            db.ExecuteNonQuery(
                @"CREATE TABLE UserGameProgress (
                    Id INTEGER PRIMARY KEY,
                    UserId INTEGER NOT NULL,
                    GameId INTEGER NOT NULL,
                    CacheKey TEXT NOT NULL,
                    AchievementsUnlocked INTEGER NOT NULL,
                    LastUpdatedUtc TEXT,
                    UpdatedUtc TEXT
                  );");
            db.ExecuteNonQuery(
                @"CREATE TABLE AchievementDefinitions (
                    Id INTEGER PRIMARY KEY,
                    GameId INTEGER NOT NULL,
                    ApiName TEXT NOT NULL,
                    DisplayName TEXT,
                    UnlockedIconPath TEXT,
                    LockedIconPath TEXT,
                    GlobalPercentUnlocked REAL,
                    CategoryType TEXT,
                    UpdatedUtc TEXT
                  );");
            db.ExecuteNonQuery(
                @"CREATE TABLE UserAchievements (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    UserGameProgressId INTEGER NOT NULL,
                    AchievementDefinitionId INTEGER NOT NULL,
                    Unlocked INTEGER NOT NULL,
                    UnlockTimeUtc TEXT,
                    ProgressNum INTEGER,
                    ProgressDenom INTEGER,
                    LastUpdatedUtc TEXT,
                    CreatedUtc TEXT
                  );");
        }

        private static void Seed(SQLiteDatabase db)
        {
            db.ExecuteNonQuery("INSERT INTO Users (Id, IsCurrentUser) VALUES (1, 1);");
            db.ExecuteNonQuery(
                "INSERT INTO Games (Id, ProviderKey, LastUpdatedUtc) VALUES (10, 'Steam', 'old');");
            db.ExecuteNonQuery(
                @"INSERT INTO UserGameProgress
                    (Id, UserId, GameId, CacheKey, AchievementsUnlocked, LastUpdatedUtc, UpdatedUtc)
                  VALUES
                    (20, 1, 10, 'game-key', 1, 'old', 'old');");
            db.ExecuteNonQuery(
                @"INSERT INTO AchievementDefinitions
                    (Id, GameId, ApiName, DisplayName, UnlockedIconPath, LockedIconPath,
                     GlobalPercentUnlocked, CategoryType, UpdatedUtc)
                  VALUES
                    (100, 10, 'ACH_OLD', 'Old Name', 'old-unlocked.png', 'old-locked.png',
                     12.5, 'Base', 'old'),
                    (101, 10, 'ACH_NEW', 'New Name', 'new-unlocked.png', 'new-locked.png',
                     33.3, 'DLC', 'old'),
                    (102, 10, 'ACH_PROGRESS', 'Progress Name', 'progress-unlocked.png',
                     'progress-locked.png', 50.0, 'Base', 'old');");
            db.ExecuteNonQuery(
                @"INSERT INTO UserAchievements
                    (Id, UserGameProgressId, AchievementDefinitionId, Unlocked, UnlockTimeUtc,
                     ProgressNum, ProgressDenom, LastUpdatedUtc, CreatedUtc)
                  VALUES
                    (200, 20, 100, 1, '2026-07-20T12:00:00.0000000Z',
                     10, 100, 'old', 'old'),
                    (201, 20, 101, 0, NULL, NULL, NULL, 'old', 'old');");
        }
    }
}
