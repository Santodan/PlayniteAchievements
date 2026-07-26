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
    /// recent unlocks must exclude achievements present in the AchievementFilters mirror table,
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
                Assert.AreEqual(0, gameA.HasUnlockedCapstone, "A filtered capstone unlock must not complete the game.");

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
                // Duplicate insert is absorbed by the UNIQUE constraint (INSERT OR IGNORE).
                db.ExecuteNonQuery(
                    "INSERT OR IGNORE INTO AchievementFilters (PlayniteGameId, ApiName, Kind, CreatedUtc) VALUES (?, ?, ?, ?);",
                    GameAId, "a3", "Filtered", "2026-07-20T00:00:00Z");
                var countA = db.ExecuteScalar<long>(
                    "SELECT COUNT(*) FROM AchievementFilters WHERE PlayniteGameId = ?;", GameAId);
                Assert.AreEqual(2L, countA);

                // Replace: delete-then-insert swaps the game's set wholesale.
                db.ExecuteNonQuery("DELETE FROM AchievementFilters WHERE PlayniteGameId = ?;", GameAId);
                db.ExecuteNonQuery(
                    "INSERT OR IGNORE INTO AchievementFilters (PlayniteGameId, ApiName, Kind, CreatedUtc) VALUES (?, ?, ?, ?);",
                    GameAId, "a1", "SummaryFiltered", "2026-07-20T00:00:00Z");

                var remaining = db.Load<FilterTestRow>(
                        "SELECT ApiName, Kind FROM AchievementFilters WHERE PlayniteGameId = ? ORDER BY ApiName;",
                        GameAId)
                    .ToList();
                Assert.AreEqual(1, remaining.Count);
                Assert.AreEqual("a1", remaining[0].ApiName);
                Assert.AreEqual("SummaryFiltered", remaining[0].Kind);

                // The swap is visible to the aggregates: a3/a4 count again, a1 no longer does.
                var gameA = db.Load<GameSummaryTestRow>(GameSummarySql)
                    .Single(r => r.PlayniteGameId == GameAId);
                Assert.AreEqual(3, gameA.TotalAchievements);
                Assert.AreEqual(2, gameA.AchievementsUnlocked);
                Assert.AreEqual(1, gameA.HasUnlockedCapstone);
            });
        }

        // Tethers the duplicated SQL above to the production reader and schema: if the
        // production predicate or DDL changes shape, this fails and the copies here must be
        // updated together with it.
        [TestMethod]
        public void ProductionReaderAndSchema_ContainFilterAwarePredicates()
        {
            var reader = File.ReadAllText(FindRepoFile("source", "Services", "Database", "SummaryCacheReader.cs"));
            var predicateCount = CountOccurrences(reader, "NOT EXISTS (SELECT 1 FROM AchievementFilters af");
            Assert.AreEqual(4, predicateCount, "All four summary queries must carry the AchievementFilters anti-join.");
            StringAssert.Contains(reader, "MAX(CASE WHEN ua.Unlocked = 1 THEN ua.UnlockTimeUtc END) AS LastUnlockUtc");
            StringAssert.Contains(reader, "COUNT(ad.Id) AS TotalAchievements");
            StringAssert.Contains(reader, "SUM(CASE WHEN ua.Unlocked = 1 THEN 1 ELSE 0 END) AS AchievementsUnlocked");

            var schema = File.ReadAllText(FindRepoFile("source", "Services", "Database", "SqlNadoSchemaManager.cs"));
            StringAssert.Contains(schema, "CREATE TABLE IF NOT EXISTS AchievementFilters");
            StringAssert.Contains(schema, "UNIQUE (PlayniteGameId, ApiName, Kind)");
            StringAssert.Contains(schema, "SchemaVersion = 17");
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

            // The real DDL from SqlNadoSchemaManager.EnsureAchievementFiltersTable.
            db.ExecuteNonQuery(@"CREATE TABLE IF NOT EXISTS AchievementFilters (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                PlayniteGameId TEXT NOT NULL COLLATE NOCASE,
                ApiName TEXT NOT NULL COLLATE NOCASE,
                Kind TEXT NOT NULL COLLATE NOCASE,
                CreatedUtc TEXT NOT NULL,
                UNIQUE (PlayniteGameId, ApiName, Kind)
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
            Exec(db, $"INSERT INTO AchievementFilters (PlayniteGameId, ApiName, Kind, CreatedUtc) VALUES ('{GameAId}', 'a3', 'Filtered', '2026-07-01T00:00:00Z');");
            Exec(db, $"INSERT INTO AchievementFilters (PlayniteGameId, ApiName, Kind, CreatedUtc) VALUES ('{GameAId}', 'a4', 'SummaryFiltered', '2026-07-01T00:00:00Z');");

            // Game B: provider-only (no PlayniteGameId); the decoy filter row targets a
            // different game id with a matching ApiName and must not apply.
            Exec(db, "INSERT INTO Games (Id, ProviderKey, ProviderGameId, GameName) VALUES (200, 'Steam', 200, 'Game B');");
            Exec(db, "INSERT INTO UserGameProgress (Id, UserId, GameId, CacheKey, HasAchievements, LastUpdatedUtc) VALUES (1200, 1, 200, 'app:200', 1, '2026-07-02T00:00:00Z');");
            Exec(db, "INSERT INTO AchievementDefinitions (Id, GameId, ApiName, Rarity) VALUES (5, 200, 'b1', 'common');");
            Exec(db, "INSERT INTO UserAchievements (Id, UserGameProgressId, AchievementDefinitionId, Unlocked, UnlockTimeUtc) VALUES (5, 1200, 5, 1, '2026-05-20T10:00:00Z');");
            Exec(db, $"INSERT INTO AchievementFilters (PlayniteGameId, ApiName, Kind, CreatedUtc) VALUES ('{DecoyGameId}', 'b1', 'Filtered', '2026-07-01T00:00:00Z');");

            // Game C: every achievement filtered.
            Exec(db, $"INSERT INTO Games (Id, ProviderKey, PlayniteGameId, GameName) VALUES (300, 'Steam', '{GameCId}', 'Game C');");
            Exec(db, $"INSERT INTO UserGameProgress (Id, UserId, GameId, CacheKey, HasAchievements, LastUpdatedUtc) VALUES (1300, 1, 300, '{GameCId}', 1, '2026-07-03T00:00:00Z');");
            Exec(db, "INSERT INTO AchievementDefinitions (Id, GameId, ApiName, Rarity) VALUES (6, 300, 'c1', 'rare');");
            Exec(db, "INSERT INTO UserAchievements (Id, UserGameProgressId, AchievementDefinitionId, Unlocked, UnlockTimeUtc) VALUES (6, 1300, 6, 1, '2026-06-15T10:00:00Z');");
            Exec(db, $"INSERT INTO AchievementFilters (PlayniteGameId, ApiName, Kind, CreatedUtc) VALUES ('{GameCId}', 'c1', 'Filtered', '2026-07-01T00:00:00Z');");
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
                MAX(CASE WHEN ad.IsCapstone = 1 AND ua.Unlocked = 1 THEN 1 ELSE 0 END) AS HasUnlockedCapstone
            FROM LatestProgress lp
            LEFT JOIN AchievementDefinitions ad
                ON ad.GameId = lp.GameId
               AND NOT EXISTS (SELECT 1 FROM AchievementFilters af
                               WHERE af.PlayniteGameId = lp.PlayniteGameId
                                 AND af.ApiName = ad.ApiName)
            LEFT JOIN UserAchievements ua
                ON ua.AchievementDefinitionId = ad.Id
               AND ua.UserGameProgressId = lp.UserGameProgressId
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
              AND NOT EXISTS (SELECT 1 FROM AchievementFilters af
                              WHERE af.PlayniteGameId = lp.PlayniteGameId
                                AND af.ApiName = ad.ApiName)
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
              AND NOT EXISTS (SELECT 1 FROM AchievementFilters af
                              WHERE af.PlayniteGameId = lp.PlayniteGameId
                                AND af.ApiName = ad.ApiName)
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
                    ad.Points AS Points
                FROM LatestProgress lp
                INNER JOIN AchievementDefinitions ad ON ad.GameId = lp.GameId
                " + userAchievementJoin + @"
                WHERE lp.RowNum = 1
                  AND NOT EXISTS (SELECT 1 FROM AchievementFilters af
                                  WHERE af.PlayniteGameId = lp.PlayniteGameId
                                    AND af.ApiName = ad.ApiName)
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
            public long HasUnlockedCapstone { get; set; }
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
            public string Kind { get; set; }
        }
    }
}
