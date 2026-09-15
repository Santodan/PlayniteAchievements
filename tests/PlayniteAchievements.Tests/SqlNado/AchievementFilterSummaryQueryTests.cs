using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlNado;

namespace PlayniteAchievements.SqlNado.Tests
{
    /// <summary>
    /// Guards the filter-aware summary queries: current-user summary aggregates, timeline, and
    /// recent unlocks must exclude achievements flagged filtered in the AchievementOverrides mirror,
    /// recompute headline counts from the filtered join, and fail open for games without a
    /// PlayniteGameId. The SQL here mirrors SummaryCacheReader (not linkable into the test
    /// project); the source-text tether test keeps the two in sync.
    /// </summary>
    [TestClass]
    public class AchievementFilterSummaryQueryTests
    {
        private const string GameAId = "11111111-1111-1111-1111-111111111111";
        private const string DecoyGameId = "22222222-2222-2222-2222-222222222222";
        private const string GameCId = "33333333-3333-3333-3333-333333333333";

        [TestMethod]
        public void GameSummaryRows_RecomputeHeadlineCountsAndLastUnlockPostFilter()
        {
            WithSeededDb(db =>
            {
                var rows = db.Load<GameSummaryTestRow>(GameSummarySql).ToList();
                var gameA = rows.Single(r => r.PlayniteGameId == GameAId);
                var gameB = rows.Single(r => r.CacheKey == "app:200");
                var gameC = rows.Single(r => r.PlayniteGameId == GameCId);

                // Game A: a3 (latest unlock) and a4 (unlocked capstone) are filtered away.
                Assert.AreEqual(2, gameA.TotalAchievements);
                Assert.AreEqual(1, gameA.AchievementsUnlocked);
                Assert.AreEqual("2026-05-01T10:00:00Z", gameA.LastUnlockUtc);
                Assert.AreEqual(1, gameA.RareCount);
                Assert.AreEqual(0, gameA.CommonCount);
                Assert.AreEqual(1, gameA.TotalRarePossible);
                Assert.AreEqual(1, gameA.TotalCommonPossible);
                Assert.AreEqual(0, gameA.CapstoneTotal, "A filtered capstone must not be counted.");
                Assert.AreEqual(0, gameA.CapstoneUnlocked, "A filtered capstone unlock must not complete the game.");

                // Game B has no PlayniteGameId; the decoy filter row must not match (fail open).
                Assert.AreEqual(1, gameB.TotalAchievements);
                Assert.AreEqual(1, gameB.AchievementsUnlocked);
                Assert.AreEqual("2026-05-20T10:00:00Z", gameB.LastUnlockUtc);

                // Game C is fully filtered: the row survives with zero counts (the consumer
                // hides rows whose recomputed total is 0).
                Assert.AreEqual(0, gameC.TotalAchievements);
                Assert.AreEqual(0, gameC.AchievementsUnlocked);
                Assert.IsNull(gameC.LastUnlockUtc);
            });
        }

        [TestMethod]
        public void TimelineRows_ExcludeFilteredUnlocks()
        {
            WithSeededDb(db =>
            {
                var rows = db.Load<TimelineTestRow>(TimelineSql).ToList();

                CollectionAssert.AreEquivalent(
                    new[] { "2026-05-01", "2026-05-20" },
                    rows.Select(r => r.UnlockDateUtc).ToArray(),
                    "Filtered unlocks (a3, a4, c1) must not contribute timeline counts.");
                Assert.IsTrue(rows.All(r => r.UnlockCount == 1));
            });
        }

        [TestMethod]
        public void RecentUnlockRows_ApplyLimitAfterFiltering()
        {
            WithSeededDb(db =>
            {
                // Unfiltered, the two most recent unlocks are c1 (2026-06-15) and a3
                // (2026-06-01) - both filtered. The limit must apply post-filter.
                var rows = db.Load<RecentUnlockTestRow>(RecentUnlocksSql, 2).ToList();

                CollectionAssert.AreEqual(
                    new[] { "b1", "a1" },
                    rows.Select(r => r.ApiName).ToArray());
            });
        }

        [TestMethod]
        public void ScoreTotalRows_ExcludeFilteredDefinitions()
        {
            WithSeededDb(db =>
            {
                var allRows = db.Load<ScoreTestRow>(BuildScoreTotalsSql(unlockedOnly: false)).ToList();
                var unlockedRows = db.Load<ScoreTestRow>(BuildScoreTotalsSql(unlockedOnly: true)).ToList();

                Assert.AreEqual(2, allRows.Count(r => r.CacheKey == GameAId));
                Assert.AreEqual(1, allRows.Count(r => r.CacheKey == "app:200"));
                Assert.AreEqual(0, allRows.Count(r => r.CacheKey == GameCId));

                Assert.AreEqual(1, unlockedRows.Count(r => r.CacheKey == GameAId));
                Assert.AreEqual(1, unlockedRows.Count(r => r.CacheKey == "app:200"));
                Assert.AreEqual(0, unlockedRows.Count(r => r.CacheKey == GameCId));
            });
        }

        [TestMethod]
        public void FilterMirror_ReplaceSemanticsAndUniqueDedupe()
        {
            WithSeededDb(db =>
            {
                // Duplicate insert is absorbed by the UNIQUE constraint (INSERT OR IGNORE). The
                // constraint is now per (game, apiName): one row carries every flag, so a second
                // insert for the same achievement cannot add a row.
                db.ExecuteNonQuery(
                    @"INSERT OR IGNORE INTO AchievementOverrides
                          (PlayniteGameId, ApiName, IsFiltered, IsSummaryFiltered, UpdatedUtc)
                      VALUES (?, ?, ?, ?, ?);",
                    GameAId, "a3", 0, 1, "2026-07-20T00:00:00Z");
                var countA = db.ExecuteScalar<long>(
                    "SELECT COUNT(*) FROM AchievementOverrides WHERE PlayniteGameId = ?;", GameAId);
                Assert.AreEqual(2L, countA);

                // Replace: delete-then-insert swaps the game's set wholesale.
                db.ExecuteNonQuery("DELETE FROM AchievementOverrides WHERE PlayniteGameId = ?;", GameAId);
                db.ExecuteNonQuery(
                    @"INSERT OR IGNORE INTO AchievementOverrides
                          (PlayniteGameId, ApiName, IsFiltered, IsSummaryFiltered, UpdatedUtc)
                      VALUES (?, ?, ?, ?, ?);",
                    GameAId, "a1", 0, 1, "2026-07-20T00:00:00Z");

                var remaining = db.Load<FilterTestRow>(
                        @"SELECT ApiName, IsFiltered, IsSummaryFiltered
                          FROM AchievementOverrides
                          WHERE PlayniteGameId = ? ORDER BY ApiName;",
                        GameAId)
                    .ToList();
                Assert.AreEqual(1, remaining.Count);
                Assert.AreEqual("a1", remaining[0].ApiName);
                Assert.AreEqual(0L, remaining[0].IsFiltered);
                Assert.AreEqual(1L, remaining[0].IsSummaryFiltered);

                // The swap is visible to the aggregates: a3/a4 count again, a1 no longer does.
                var gameA = db.Load<GameSummaryTestRow>(GameSummarySql)
                    .Single(r => r.PlayniteGameId == GameAId);
                Assert.AreEqual(3, gameA.TotalAchievements);
                Assert.AreEqual(2, gameA.AchievementsUnlocked);
                Assert.AreEqual(1, gameA.CapstoneTotal);
                Assert.AreEqual(1, gameA.CapstoneUnlocked);
            });
        }

        [TestMethod]
        public void OverrideRowWithoutFilterFlags_DoesNotRemoveTheAchievement()
        {
            WithSeededDb(db =>
            {
                // The mirror now also carries rows that only customize points or trophy type.
                // Testing a row's presence instead of its flags would silently drop a2 from every
                // summary the moment a user edited its points.
                db.ExecuteNonQuery(
                    @"INSERT OR IGNORE INTO AchievementOverrides
                          (PlayniteGameId, ApiName, Points, TrophyType, IsFiltered, IsSummaryFiltered, UpdatedUtc)
                      VALUES (?, ?, ?, ?, 0, 0, ?);",
                    GameAId, "a2", 25, "gold", "2026-07-20T00:00:00Z");

                var gameA = db.Load<GameSummaryTestRow>(GameSummarySql)
                    .Single(r => r.PlayniteGameId == GameAId);

                Assert.AreEqual(2, gameA.TotalAchievements, "A points/trophy override must not filter the achievement.");
                Assert.AreEqual(1, gameA.TrophyGoldTotal, "The trophy count must resolve the override first.");
            });
        }

        [TestMethod]
        public void CapstoneIdentity_IsDecidedAgainstThePlatinumsThemselves()
        {
            WithSeededDb(db =>
            {
                // A plain PlayStation game: the platinum is the capstone.
                SeedGame(db, 400, "44444444-4444-4444-4444-444444444444", "Game D");
                SeedAchievement(db, 40, 400, 1400, "d_plat", "platinum", isCapstone: true);
                SeedAchievement(db, 41, 400, 1400, "d_gold", "gold", isCapstone: false);

                // Same count on each side, different achievements: two finish lines, not one.
                SeedGame(db, 500, "55555555-5555-5555-5555-555555555555", "Game E");
                SeedAchievement(db, 50, 500, 1500, "e_plat", "platinum", isCapstone: false);
                SeedAchievement(db, 51, 500, 1500, "e_mastery", null, isCapstone: true);

                var rows = db.Load<GameSummaryTestRow>(GameSummarySql).ToList();

                var gameD = rows.Single(r => r.GameName == "Game D");
                Assert.AreEqual(0, gameD.CapstonesNotPlatinum);
                Assert.AreEqual(0, gameD.PlatinumsNotCapstone);
                Assert.AreEqual("d_plat", gameD.PlatinumApiNames);

                var gameE = rows.Single(r => r.GameName == "Game E");
                Assert.AreEqual(1, gameE.CapstonesNotPlatinum);
                Assert.AreEqual(1, gameE.PlatinumsNotCapstone);
                Assert.AreEqual("e_plat", gameE.PlatinumApiNames);

                // A game with neither hands the finish badge to nothing, and says so by carrying
                // no platinum at all rather than by a count that happens to agree.
                var gameC = rows.Single(r => r.PlayniteGameId == GameCId);
                Assert.IsNull(gameC.PlatinumApiNames);
            });
        }

        [TestMethod]
        public void PointsOverride_IsSummedInsteadOfTheProviderValue()
        {
            WithSeededDb(db =>
            {
                db.ExecuteNonQuery("UPDATE AchievementDefinitions SET Points = 10 WHERE ApiName = 'a1';");
                db.ExecuteNonQuery(
                    @"INSERT OR IGNORE INTO AchievementOverrides
                          (PlayniteGameId, ApiName, Points, IsFiltered, IsSummaryFiltered, UpdatedUtc)
                      VALUES (?, ?, ?, 0, 0, ?);",
                    GameAId, "a1", 99, "2026-07-20T00:00:00Z");

                var row = db.Load<ScoreTestRow>(BuildScoreTotalsSql(unlockedOnly: true))
                    .Single(r => r.CacheKey == GameAId);

                Assert.AreEqual(99, row.Points);
            });
        }

        // Tethers the duplicated SQL above to the production reader and schema: if the
        // production predicate or DDL changes shape, this fails and the copies here must be
        // updated together with it.
        [TestMethod]
        public void ProductionReaderAndSchema_ContainFilterAwarePredicates()
        {
            var reader = File.ReadAllText(FindRepoFile("source", "Services", "Database", "SummaryCacheReader.cs"));
            var predicateCount = CountOccurrences(reader, "NOT EXISTS (SELECT 1 FROM AchievementOverrides ao");
            Assert.AreEqual(4, predicateCount, "All four summary queries must carry the AchievementOverrides anti-join.");
            // The mirror now also holds rows that only carry points or trophy type. Without the
            // flag test those rows would silently drop an achievement from every summary.
            Assert.AreEqual(
                4,
                CountOccurrences(reader, "AND (ao.IsFiltered = 1 OR ao.IsSummaryFiltered = 1))"),
                "Each anti-join must test the filter flags, not merely the row's presence.");
            // The user-editable fields aggregates read must resolve the override first.
            StringAssert.Contains(reader, "COALESCE(aov.Points, ad.Points) AS Points");
            StringAssert.Contains(reader, "LOWER(COALESCE(aov.TrophyType, ad.TrophyType, ''))");
            // Rarity stays provider-owned; an override must never reach it.
            Assert.AreEqual(
                0,
                CountOccurrences(reader, "aov.Rarity"),
                "Rarity is provider-owned and must not be resolved from the override mirror.");
            StringAssert.Contains(reader, "MAX(CASE WHEN ua.Unlocked = 1 THEN ua.UnlockTimeUtc END) AS LastUnlockUtc");
            StringAssert.Contains(reader, "COUNT(ad.Id) AS TotalAchievements");
            // The capstone/platinum identity is settled by the query, by identity rather than by
            // tally, and the platinum ApiNames ride along for the stored-capstone overlay.
            StringAssert.Contains(reader, "THEN 1 ELSE 0 END) AS CapstonesNotPlatinum");
            StringAssert.Contains(reader, "THEN 1 ELSE 0 END) AS PlatinumsNotCapstone");
            StringAssert.Contains(reader, "THEN ad.ApiName END, '~|~') AS PlatinumApiNames");
            StringAssert.Contains(reader, "SUM(CASE WHEN ua.Unlocked = 1 THEN 1 ELSE 0 END) AS AchievementsUnlocked");

            var schema = File.ReadAllText(FindRepoFile("source", "Services", "Database", "SqlNadoSchemaManager.cs"));
            StringAssert.Contains(schema, "CREATE TABLE IF NOT EXISTS AchievementOverrides");
            StringAssert.Contains(schema, "UNIQUE (PlayniteGameId, ApiName)");
            // The narrower predecessor is dropped rather than left to drift.
            StringAssert.Contains(schema, "DROP TABLE IF EXISTS AchievementFilters;");
            StringAssert.Contains(schema, "SchemaVersion = 18");
        }

        private static void WithSeededDb(Action<SQLiteDatabase> action)
        {
            var path = Path.Combine(Path.GetTempPath(), "playach-filterq-" + Guid.NewGuid().ToString("N") + ".db");
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
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }

        private static void CreateSchema(SQLiteDatabase db)
        {
            db.ExecuteNonQuery(@"CREATE TABLE Users (
                Id INTEGER PRIMARY KEY,
                IsCurrentUser INTEGER NOT NULL DEFAULT 0);");

            db.ExecuteNonQuery(@"CREATE TABLE Games (
                Id INTEGER PRIMARY KEY,
                ProviderKey TEXT,
                ProviderPlatformKey TEXT NULL,
                ProviderGameId INTEGER NULL,
                ProviderGameKey TEXT NULL,
                PlayniteGameId TEXT NULL,
                GameName TEXT);");

            db.ExecuteNonQuery(@"CREATE TABLE UserGameProgress (
                Id INTEGER PRIMARY KEY,
                UserId INTEGER NOT NULL,
                GameId INTEGER NOT NULL,
                CacheKey TEXT,
                HasAchievements INTEGER NOT NULL DEFAULT 0,
                LastUpdatedUtc TEXT);");

            db.ExecuteNonQuery(@"CREATE TABLE AchievementDefinitions (
                Id INTEGER PRIMARY KEY,
                GameId INTEGER NOT NULL,
                ApiName TEXT,
                DisplayName TEXT NULL,
                Description TEXT NULL,
                UnlockedIconPath TEXT NULL,
                LockedIconPath TEXT NULL,
                Points INTEGER NULL,
                ScaledPoints INTEGER NULL,
                Category TEXT NULL,
                CategoryType TEXT NULL,
                TrophyType TEXT NULL,
                Hidden INTEGER NOT NULL DEFAULT 0,
                IsCapstone INTEGER NOT NULL DEFAULT 0,
                GlobalPercentUnlocked REAL NULL,
                Rarity TEXT NULL);");

            db.ExecuteNonQuery(@"CREATE TABLE UserAchievements (
                Id INTEGER PRIMARY KEY,
                UserGameProgressId INTEGER NOT NULL,
                AchievementDefinitionId INTEGER NOT NULL,
                Unlocked INTEGER NOT NULL DEFAULT 0,
                UnlockTimeUtc TEXT NULL,
                ProgressNum INTEGER NULL,
                ProgressDenom INTEGER NULL);");

            // The real DDL from SqlNadoSchemaManager.EnsureAchievementOverridesTable.
            db.ExecuteNonQuery(@"CREATE TABLE IF NOT EXISTS AchievementOverrides (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                PlayniteGameId TEXT NOT NULL COLLATE NOCASE,
                ApiName TEXT NOT NULL COLLATE NOCASE,
                Points INTEGER NULL,
                TrophyType TEXT NULL COLLATE NOCASE,
                IsFiltered INTEGER NOT NULL DEFAULT 0,
                IsSummaryFiltered INTEGER NOT NULL DEFAULT 0,
                UpdatedUtc TEXT NOT NULL,
                UNIQUE (PlayniteGameId, ApiName)
            );");
        }

        private static void Seed(SQLiteDatabase db)
        {
            Exec(db, "INSERT INTO Users (Id, IsCurrentUser) VALUES (1, 1);");

            // Game A: playnite-backed, 4 achievements, filters on a3 (grid) and a4 (summary).
            Exec(db, $"INSERT INTO Games (Id, ProviderKey, PlayniteGameId, GameName) VALUES (100, 'Steam', '{GameAId}', 'Game A');");
            Exec(db, $"INSERT INTO UserGameProgress (Id, UserId, GameId, CacheKey, HasAchievements, LastUpdatedUtc) VALUES (1100, 1, 100, '{GameAId}', 1, '2026-07-01T00:00:00Z');");
            Exec(db, "INSERT INTO AchievementDefinitions (Id, GameId, ApiName, Rarity, IsCapstone) VALUES (1, 100, 'a1', 'rare', 0);");
            Exec(db, "INSERT INTO AchievementDefinitions (Id, GameId, ApiName, Rarity, IsCapstone) VALUES (2, 100, 'a2', 'common', 0);");
            Exec(db, "INSERT INTO AchievementDefinitions (Id, GameId, ApiName, Rarity, IsCapstone) VALUES (3, 100, 'a3', 'common', 0);");
            Exec(db, "INSERT INTO AchievementDefinitions (Id, GameId, ApiName, Rarity, IsCapstone) VALUES (4, 100, 'a4', 'rare', 1);");
            Exec(db, "INSERT INTO UserAchievements (Id, UserGameProgressId, AchievementDefinitionId, Unlocked, UnlockTimeUtc) VALUES (1, 1100, 1, 1, '2026-05-01T10:00:00Z');");
            Exec(db, "INSERT INTO UserAchievements (Id, UserGameProgressId, AchievementDefinitionId, Unlocked, UnlockTimeUtc) VALUES (2, 1100, 2, 0, NULL);");
            Exec(db, "INSERT INTO UserAchievements (Id, UserGameProgressId, AchievementDefinitionId, Unlocked, UnlockTimeUtc) VALUES (3, 1100, 3, 1, '2026-06-01T10:00:00Z');");
            Exec(db, "INSERT INTO UserAchievements (Id, UserGameProgressId, AchievementDefinitionId, Unlocked, UnlockTimeUtc) VALUES (4, 1100, 4, 1, '2026-04-01T10:00:00Z');");
            Exec(db, $"INSERT INTO AchievementOverrides (PlayniteGameId, ApiName, IsFiltered, IsSummaryFiltered, UpdatedUtc) VALUES ('{GameAId}', 'a3', 1, 0, '2026-07-01T00:00:00Z');");
            Exec(db, $"INSERT INTO AchievementOverrides (PlayniteGameId, ApiName, IsFiltered, IsSummaryFiltered, UpdatedUtc) VALUES ('{GameAId}', 'a4', 0, 1, '2026-07-01T00:00:00Z');");

            // Game B: provider-only (no PlayniteGameId); the decoy filter row targets a
            // different game id with a matching ApiName and must not apply.
            Exec(db, "INSERT INTO Games (Id, ProviderKey, ProviderGameId, GameName) VALUES (200, 'Steam', 200, 'Game B');");
            Exec(db, "INSERT INTO UserGameProgress (Id, UserId, GameId, CacheKey, HasAchievements, LastUpdatedUtc) VALUES (1200, 1, 200, 'app:200', 1, '2026-07-02T00:00:00Z');");
            Exec(db, "INSERT INTO AchievementDefinitions (Id, GameId, ApiName, Rarity) VALUES (5, 200, 'b1', 'common');");
            Exec(db, "INSERT INTO UserAchievements (Id, UserGameProgressId, AchievementDefinitionId, Unlocked, UnlockTimeUtc) VALUES (5, 1200, 5, 1, '2026-05-20T10:00:00Z');");
            Exec(db, $"INSERT INTO AchievementOverrides (PlayniteGameId, ApiName, IsFiltered, IsSummaryFiltered, UpdatedUtc) VALUES ('{DecoyGameId}', 'b1', 1, 0, '2026-07-01T00:00:00Z');");

            // Game C: every achievement filtered.
            Exec(db, $"INSERT INTO Games (Id, ProviderKey, PlayniteGameId, GameName) VALUES (300, 'Steam', '{GameCId}', 'Game C');");
            Exec(db, $"INSERT INTO UserGameProgress (Id, UserId, GameId, CacheKey, HasAchievements, LastUpdatedUtc) VALUES (1300, 1, 300, '{GameCId}', 1, '2026-07-03T00:00:00Z');");
            Exec(db, "INSERT INTO AchievementDefinitions (Id, GameId, ApiName, Rarity) VALUES (6, 300, 'c1', 'rare');");
            Exec(db, "INSERT INTO UserAchievements (Id, UserGameProgressId, AchievementDefinitionId, Unlocked, UnlockTimeUtc) VALUES (6, 1300, 6, 1, '2026-06-15T10:00:00Z');");
            Exec(db, $"INSERT INTO AchievementOverrides (PlayniteGameId, ApiName, IsFiltered, IsSummaryFiltered, UpdatedUtc) VALUES ('{GameCId}', 'c1', 1, 0, '2026-07-01T00:00:00Z');");
        }

        private static void SeedGame(SQLiteDatabase db, int gameId, string playniteGameId, string gameName)
        {
            Exec(db, $"INSERT INTO Games (Id, ProviderKey, PlayniteGameId, GameName) VALUES ({gameId}, 'PSN', '{playniteGameId}', '{gameName}');");
            Exec(db, $"INSERT INTO UserGameProgress (Id, UserId, GameId, CacheKey, HasAchievements, LastUpdatedUtc) VALUES ({1000 + gameId}, 1, {gameId}, '{playniteGameId}', 1, '2026-07-04T00:00:00Z');");
        }

        private static void SeedAchievement(
            SQLiteDatabase db,
            int definitionId,
            int gameId,
            int progressId,
            string apiName,
            string trophyType,
            bool isCapstone)
        {
            var trophy = trophyType == null ? "NULL" : $"'{trophyType}'";
            Exec(
                db,
                $"INSERT INTO AchievementDefinitions (Id, GameId, ApiName, TrophyType, IsCapstone) " +
                $"VALUES ({definitionId}, {gameId}, '{apiName}', {trophy}, {(isCapstone ? 1 : 0)});");
            Exec(
                db,
                $"INSERT INTO UserAchievements (Id, UserGameProgressId, AchievementDefinitionId, Unlocked) " +
                $"VALUES ({definitionId}, {progressId}, {definitionId}, 0);");
        }

        private static void Exec(SQLiteDatabase db, string sql) => db.ExecuteNonQuery(sql);

        private static int CountOccurrences(string text, string token)
        {
            var count = 0;
            var index = 0;
            while ((index = text.IndexOf(token, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += token.Length;
            }

            return count;
        }

        private static string FindRepoFile(params string[] parts)
        {
            var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (directory != null)
            {
                var path = Path.Combine(new[] { directory.FullName }.Concat(parts).ToArray());
                if (File.Exists(path))
                {
                    return path;
                }

                directory = directory.Parent;
            }

            Assert.Fail("Could not find " + Path.Combine(parts));
            return null;
        }

        // Mirrors SummaryCacheReader.LoadCachedGameSummaryRows (rarity/trophy columns trimmed
        // to the ones asserted here).
        private const string GameSummarySql = @"WITH LatestProgress AS (
                SELECT
                    ugp.Id AS UserGameProgressId,
                    ugp.GameId AS GameId,
                    TRIM(ugp.CacheKey) AS CacheKey,
                    ugp.HasAchievements AS HasAchievements,
                    ugp.LastUpdatedUtc AS LastUpdatedUtc,
                    g.PlayniteGameId AS PlayniteGameId,
                    g.GameName AS GameName,
                    ROW_NUMBER() OVER (
                        PARTITION BY ugp.CacheKey
                        ORDER BY ugp.LastUpdatedUtc DESC, ugp.Id DESC
                    ) AS RowNum
                FROM UserGameProgress ugp
                INNER JOIN Users u ON u.Id = ugp.UserId
                INNER JOIN Games g ON g.Id = ugp.GameId
                WHERE u.IsCurrentUser = 1
                  AND ugp.CacheKey IS NOT NULL
                  AND TRIM(ugp.CacheKey) <> ''
            )
            SELECT
                lp.CacheKey AS CacheKey,
                lp.HasAchievements AS HasAchievements,
                SUM(CASE WHEN ua.Unlocked = 1 THEN 1 ELSE 0 END) AS AchievementsUnlocked,
                COUNT(ad.Id) AS TotalAchievements,
                MAX(CASE WHEN ua.Unlocked = 1 THEN ua.UnlockTimeUtc END) AS LastUnlockUtc,
                lp.PlayniteGameId AS PlayniteGameId,
                lp.GameName AS GameName,
                SUM(CASE WHEN LOWER(COALESCE(ad.Rarity, '')) = 'common' AND ua.Unlocked = 1 THEN 1 ELSE 0 END) AS CommonCount,
                SUM(CASE WHEN LOWER(COALESCE(ad.Rarity, '')) = 'rare' AND ua.Unlocked = 1 THEN 1 ELSE 0 END) AS RareCount,
                SUM(CASE WHEN LOWER(COALESCE(ad.Rarity, '')) = 'common' THEN 1 ELSE 0 END) AS TotalCommonPossible,
                SUM(CASE WHEN LOWER(COALESCE(ad.Rarity, '')) = 'rare' THEN 1 ELSE 0 END) AS TotalRarePossible,
                SUM(CASE WHEN LOWER(COALESCE(aov.TrophyType, ad.TrophyType, '')) = 'gold' THEN 1 ELSE 0 END) AS TrophyGoldTotal,
                SUM(CASE WHEN ad.IsCapstone = 1 THEN 1 ELSE 0 END) AS CapstoneTotal,
                SUM(CASE WHEN ad.IsCapstone = 1 AND ua.Unlocked = 1 THEN 1 ELSE 0 END) AS CapstoneUnlocked,
                SUM(CASE WHEN ad.IsCapstone = 1
                         AND LOWER(COALESCE(aov.TrophyType, ad.TrophyType, '')) <> 'platinum'
                    THEN 1 ELSE 0 END) AS CapstonesNotPlatinum,
                SUM(CASE WHEN COALESCE(ad.IsCapstone, 0) <> 1
                         AND LOWER(COALESCE(aov.TrophyType, ad.TrophyType, '')) = 'platinum'
                    THEN 1 ELSE 0 END) AS PlatinumsNotCapstone,
                GROUP_CONCAT(CASE WHEN LOWER(COALESCE(aov.TrophyType, ad.TrophyType, '')) = 'platinum'
                             THEN ad.ApiName END, '~|~') AS PlatinumApiNames
            FROM LatestProgress lp
            LEFT JOIN AchievementDefinitions ad
                ON ad.GameId = lp.GameId
               AND NOT EXISTS (SELECT 1 FROM AchievementOverrides ao
                               WHERE ao.PlayniteGameId = lp.PlayniteGameId
                                 AND ao.ApiName = ad.ApiName
                                     AND (ao.IsFiltered = 1 OR ao.IsSummaryFiltered = 1))
            LEFT JOIN UserAchievements ua
                ON ua.AchievementDefinitionId = ad.Id
               AND ua.UserGameProgressId = lp.UserGameProgressId
            LEFT JOIN AchievementOverrides aov
                ON aov.PlayniteGameId = lp.PlayniteGameId
               AND aov.ApiName = ad.ApiName
            WHERE lp.RowNum = 1
            GROUP BY
                lp.CacheKey,
                lp.HasAchievements,
                lp.LastUpdatedUtc,
                lp.PlayniteGameId,
                lp.GameName
            ORDER BY lp.LastUpdatedUtc DESC, lp.CacheKey;";

        // Mirrors SummaryCacheReader.LoadCachedUnlockTimelineRows.
        private const string TimelineSql = @"WITH LatestProgress AS (
                SELECT
                    ugp.Id AS UserGameProgressId,
                    TRIM(ugp.CacheKey) AS CacheKey,
                    g.PlayniteGameId AS PlayniteGameId,
                    ROW_NUMBER() OVER (
                        PARTITION BY ugp.CacheKey
                        ORDER BY ugp.LastUpdatedUtc DESC, ugp.Id DESC
                    ) AS RowNum
                FROM UserGameProgress ugp
                INNER JOIN Users u ON u.Id = ugp.UserId
                INNER JOIN Games g ON g.Id = ugp.GameId
                WHERE u.IsCurrentUser = 1
                  AND ugp.CacheKey IS NOT NULL
                  AND TRIM(ugp.CacheKey) <> ''
            )
            SELECT
                lp.CacheKey AS CacheKey,
                lp.PlayniteGameId AS PlayniteGameId,
                date(ua.UnlockTimeUtc) AS UnlockDateUtc,
                COUNT(*) AS UnlockCount
            FROM LatestProgress lp
            INNER JOIN UserAchievements ua
                ON ua.UserGameProgressId = lp.UserGameProgressId
               AND ua.Unlocked = 1
               AND ua.UnlockTimeUtc IS NOT NULL
            INNER JOIN AchievementDefinitions ad ON ad.Id = ua.AchievementDefinitionId
            WHERE lp.RowNum = 1
              AND NOT EXISTS (SELECT 1 FROM AchievementOverrides ao
                              WHERE ao.PlayniteGameId = lp.PlayniteGameId
                                AND ao.ApiName = ad.ApiName
                                     AND (ao.IsFiltered = 1 OR ao.IsSummaryFiltered = 1))
            GROUP BY
                lp.CacheKey,
                lp.PlayniteGameId,
                date(ua.UnlockTimeUtc)
            ORDER BY UnlockDateUtc DESC, lp.CacheKey;";

        // Mirrors SummaryCacheReader.LoadCachedRecentUnlockRows (columns trimmed).
        private const string RecentUnlocksSql = @"WITH LatestProgress AS (
                SELECT
                    ugp.Id AS UserGameProgressId,
                    TRIM(ugp.CacheKey) AS CacheKey,
                    g.PlayniteGameId AS PlayniteGameId,
                    g.GameName AS GameName,
                    ROW_NUMBER() OVER (
                        PARTITION BY ugp.CacheKey
                        ORDER BY ugp.LastUpdatedUtc DESC, ugp.Id DESC
                    ) AS RowNum
                FROM UserGameProgress ugp
                INNER JOIN Users u ON u.Id = ugp.UserId
                INNER JOIN Games g ON g.Id = ugp.GameId
                WHERE u.IsCurrentUser = 1
                  AND ugp.CacheKey IS NOT NULL
                  AND TRIM(ugp.CacheKey) <> ''
            )
            SELECT
                lp.CacheKey AS CacheKey,
                lp.GameName AS GameName,
                ad.ApiName AS ApiName,
                ua.UnlockTimeUtc AS UnlockTimeUtc
            FROM LatestProgress lp
            INNER JOIN UserAchievements ua
                ON ua.UserGameProgressId = lp.UserGameProgressId
               AND ua.Unlocked = 1
               AND ua.UnlockTimeUtc IS NOT NULL
            INNER JOIN AchievementDefinitions ad ON ad.Id = ua.AchievementDefinitionId
            WHERE lp.RowNum = 1
              AND NOT EXISTS (SELECT 1 FROM AchievementOverrides ao
                              WHERE ao.PlayniteGameId = lp.PlayniteGameId
                                AND ao.ApiName = ad.ApiName
                                     AND (ao.IsFiltered = 1 OR ao.IsSummaryFiltered = 1))
            ORDER BY ua.UnlockTimeUtc DESC, lp.CacheKey, ad.Id LIMIT ?;";

        // Mirrors SummaryCacheReader.LoadCachedScoreTotals.
        private static string BuildScoreTotalsSql(bool unlockedOnly)
        {
            var userAchievementJoin = unlockedOnly
                ? @"INNER JOIN UserAchievements ua
                    ON ua.AchievementDefinitionId = ad.Id
                   AND ua.UserGameProgressId = lp.UserGameProgressId
                   AND ua.Unlocked = 1"
                : string.Empty;

            return @"WITH LatestProgress AS (
                    SELECT
                        ugp.Id AS UserGameProgressId,
                        ugp.GameId AS GameId,
                        TRIM(ugp.CacheKey) AS CacheKey,
                        g.PlayniteGameId AS PlayniteGameId,
                        ROW_NUMBER() OVER (
                            PARTITION BY ugp.CacheKey
                            ORDER BY ugp.LastUpdatedUtc DESC, ugp.Id DESC
                        ) AS RowNum
                    FROM UserGameProgress ugp
                    INNER JOIN Users u ON u.Id = ugp.UserId
                    INNER JOIN Games g ON g.Id = ugp.GameId
                    WHERE u.IsCurrentUser = 1
                      AND ugp.CacheKey IS NOT NULL
                      AND TRIM(ugp.CacheKey) <> ''
                )
                SELECT
                    lp.CacheKey AS CacheKey,
                    ad.Rarity AS Rarity,
                    COALESCE(aov.Points, ad.Points) AS Points
                FROM LatestProgress lp
                INNER JOIN AchievementDefinitions ad ON ad.GameId = lp.GameId
                LEFT JOIN AchievementOverrides aov
                    ON aov.PlayniteGameId = lp.PlayniteGameId
                   AND aov.ApiName = ad.ApiName
                " + userAchievementJoin + @"
                WHERE lp.RowNum = 1
                  AND NOT EXISTS (SELECT 1 FROM AchievementOverrides ao
                                  WHERE ao.PlayniteGameId = lp.PlayniteGameId
                                    AND ao.ApiName = ad.ApiName
                                     AND (ao.IsFiltered = 1 OR ao.IsSummaryFiltered = 1))
                ORDER BY lp.CacheKey;";
        }

        private sealed class GameSummaryTestRow
        {
            public string CacheKey { get; set; }
            public long HasAchievements { get; set; }
            public long AchievementsUnlocked { get; set; }
            public long TotalAchievements { get; set; }
            public string LastUnlockUtc { get; set; }
            public string PlayniteGameId { get; set; }
            public string GameName { get; set; }
            public long CommonCount { get; set; }
            public long RareCount { get; set; }
            public long TotalCommonPossible { get; set; }
            public long TotalRarePossible { get; set; }
            public long TrophyGoldTotal { get; set; }
            public long CapstoneTotal { get; set; }
            public long CapstoneUnlocked { get; set; }
            public long CapstonesNotPlatinum { get; set; }
            public long PlatinumsNotCapstone { get; set; }
            public string PlatinumApiNames { get; set; }
        }

        private sealed class TimelineTestRow
        {
            public string CacheKey { get; set; }
            public string PlayniteGameId { get; set; }
            public string UnlockDateUtc { get; set; }
            public long UnlockCount { get; set; }
        }

        private sealed class RecentUnlockTestRow
        {
            public string CacheKey { get; set; }
            public string GameName { get; set; }
            public string ApiName { get; set; }
            public string UnlockTimeUtc { get; set; }
        }

        private sealed class ScoreTestRow
        {
            public string CacheKey { get; set; }
            public string Rarity { get; set; }
            public int? Points { get; set; }
        }

        private sealed class FilterTestRow
        {
            public string ApiName { get; set; }
            public long IsFiltered { get; set; }
            public long IsSummaryFiltered { get; set; }
        }
    }
}
