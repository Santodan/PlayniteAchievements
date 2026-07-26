using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlNado;

namespace PlayniteAchievements.SqlNado.Tests
{
    /// <summary>
    /// Guards the friends-overview summary query rewrite: the CTE-based queries must return the
    /// exact same rows (values and ordering) as the original correlated-subquery-per-row versions.
    /// Both variants run against the same seeded database and are compared field-by-field.
    /// </summary>
    [TestClass]
    public class FriendSummaryQueryEquivalenceTests
    {
        [TestMethod]
        public void FriendGameSummary_Rewrite_MatchesLegacyCorrelatedSubqueryResults()
        {
            WithSeededDb(db =>
            {
                var legacy = db.Load<GameSummaryTestRow>(LegacyGameSummarySql).ToList();
                var rewritten = db.Load<GameSummaryTestRow>(RewrittenGameSummarySql).ToList();

                AssertRowsEqual(legacy, rewritten);

                // Sanity: the fixture must actually exercise the query (owned games only, and the
                // inactive friend's exclusive game must be excluded).
                Assert.AreEqual(3, rewritten.Count, "Expected G1, G2, G4 (G3 owned only by inactive friend is excluded).");
                CollectionAssert.AreEquivalent(
                    new[] { "G1", "G2", "G4" },
                    rewritten.Select(r => r.GameName).ToArray());
            });
        }

        [TestMethod]
        public void FriendSummary_Rewrite_MatchesLegacyCorrelatedSubqueryResults()
        {
            WithSeededDb(db =>
            {
                var cutoff = "2024-01-01T00:00:00Z";
                var legacy = db.Load<FriendSummaryTestRow>(LegacyFriendSummarySql, cutoff).ToList();
                var rewritten = db.Load<FriendSummaryTestRow>(RewrittenFriendSummarySql, cutoff).ToList();

                AssertRowsEqual(legacy, rewritten);

                // Only active friends with FriendSource appear.
                Assert.AreEqual(2, rewritten.Count, "Expected friends A and B (C is inactive).");
                CollectionAssert.AreEquivalent(
                    new[] { "A", "B" },
                    rewritten.Select(r => r.DisplayName).ToArray());
            });
        }

        private static void WithSeededDb(Action<SQLiteDatabase> action)
        {
            var path = Path.Combine(Path.GetTempPath(), "playach-friendq-" + Guid.NewGuid().ToString("N") + ".db");
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
                ProviderKey TEXT,
                ExternalUserId TEXT,
                DisplayName TEXT,
                IsCurrentUser INTEGER NOT NULL DEFAULT 0,
                IsActiveFriend INTEGER NOT NULL DEFAULT 1,
                FriendSource TEXT NULL,
                AvatarUrl TEXT NULL,
                AvatarPath TEXT NULL,
                LastRefreshedUtc TEXT NULL);");

            db.ExecuteNonQuery(@"CREATE TABLE Games (
                Id INTEGER PRIMARY KEY,
                ProviderKey TEXT,
                ProviderGameId INTEGER NULL,
                PlayniteGameId TEXT NULL,
                GameName TEXT,
                IconPath TEXT NULL,
                CoverPath TEXT NULL);");

            db.ExecuteNonQuery(@"CREATE TABLE AchievementDefinitions (
                Id INTEGER PRIMARY KEY,
                GameId INTEGER NOT NULL);");

            db.ExecuteNonQuery(@"CREATE TABLE UserGameProgress (
                Id INTEGER PRIMARY KEY,
                UserId INTEGER NOT NULL,
                GameId INTEGER NOT NULL);");

            db.ExecuteNonQuery(@"CREATE TABLE UserAchievements (
                Id INTEGER PRIMARY KEY,
                UserGameProgressId INTEGER NOT NULL,
                AchievementDefinitionId INTEGER NOT NULL,
                Unlocked INTEGER NOT NULL DEFAULT 0,
                UnlockTimeUtc TEXT NULL);");

            db.ExecuteNonQuery(@"CREATE TABLE FriendOwnership (
                Id INTEGER PRIMARY KEY,
                UserId INTEGER NOT NULL,
                GameId INTEGER NOT NULL,
                PlaytimeForeverMinutes INTEGER NOT NULL DEFAULT 0,
                LastPlayedUtc TEXT NULL,
                LastScrapedUtc TEXT NULL,
                LastScrapeStatus TEXT NULL);");
        }

        private static void Seed(SQLiteDatabase db)
        {
            // Users: 1 = current user, A(2)/B(3) active friends, C(4) inactive friend.
            Exec(db, "INSERT INTO Users (Id, ProviderKey, ExternalUserId, DisplayName, IsCurrentUser, IsActiveFriend, FriendSource) VALUES (1,'Steam','me','Me',1,0,NULL);");
            Exec(db, "INSERT INTO Users (Id, ProviderKey, ExternalUserId, DisplayName, IsCurrentUser, IsActiveFriend, FriendSource, AvatarUrl, LastRefreshedUtc) VALUES (2,'Steam','a','A',0,1,'friends','urlA','2024-06-01T00:00:00Z');");
            Exec(db, "INSERT INTO Users (Id, ProviderKey, ExternalUserId, DisplayName, IsCurrentUser, IsActiveFriend, FriendSource, AvatarUrl, LastRefreshedUtc) VALUES (3,'Steam','b','B',0,1,'friends','urlB','2024-06-02T00:00:00Z');");
            Exec(db, "INSERT INTO Users (Id, ProviderKey, ExternalUserId, DisplayName, IsCurrentUser, IsActiveFriend, FriendSource) VALUES (4,'Steam','c','C',0,0,'friends');");

            // Games: G1(100), G2(200), G3(300 owned only by inactive C), G4(400).
            Exec(db, "INSERT INTO Games (Id, ProviderKey, ProviderGameId, PlayniteGameId, GameName, IconPath, CoverPath) VALUES (100,'Steam',100,'p100','G1','i1','c1');");
            Exec(db, "INSERT INTO Games (Id, ProviderKey, ProviderGameId, PlayniteGameId, GameName, IconPath, CoverPath) VALUES (200,'Steam',200,'p200','G2','i2','c2');");
            Exec(db, "INSERT INTO Games (Id, ProviderKey, ProviderGameId, PlayniteGameId, GameName, IconPath, CoverPath) VALUES (300,'Steam',300,'p300','G3','i3','c3');");
            Exec(db, "INSERT INTO Games (Id, ProviderKey, ProviderGameId, PlayniteGameId, GameName, IconPath, CoverPath) VALUES (400,'Steam',400,'p400','G4','i4','c4');");

            // Achievement definitions: G1 has 2, G4 has 3, G2 has none.
            Exec(db, "INSERT INTO AchievementDefinitions (Id, GameId) VALUES (1,100),(2,100),(3,400),(4,400),(5,400);");

            // Ownership: A owns G1,G2,G4; B owns G1,G4; C owns G3.
            Exec(db, "INSERT INTO FriendOwnership (Id, UserId, GameId, PlaytimeForeverMinutes, LastPlayedUtc, LastScrapedUtc, LastScrapeStatus) VALUES (1,2,100,60,'2024-05-01T00:00:00Z','2024-05-10T00:00:00Z','ok');");
            Exec(db, "INSERT INTO FriendOwnership (Id, UserId, GameId, PlaytimeForeverMinutes, LastPlayedUtc, LastScrapedUtc, LastScrapeStatus) VALUES (2,3,100,120,'2024-05-05T00:00:00Z','2024-05-12T00:00:00Z','partial');");
            Exec(db, "INSERT INTO FriendOwnership (Id, UserId, GameId, PlaytimeForeverMinutes, LastPlayedUtc, LastScrapedUtc, LastScrapeStatus) VALUES (3,2,200,30,'2024-04-01T00:00:00Z','2024-04-02T00:00:00Z',NULL);");
            Exec(db, "INSERT INTO FriendOwnership (Id, UserId, GameId, PlaytimeForeverMinutes, LastPlayedUtc, LastScrapedUtc, LastScrapeStatus) VALUES (4,2,400,200,'2024-06-01T00:00:00Z','2024-06-03T00:00:00Z','ok');");
            Exec(db, "INSERT INTO FriendOwnership (Id, UserId, GameId, PlaytimeForeverMinutes, LastPlayedUtc, LastScrapedUtc, LastScrapeStatus) VALUES (5,3,400,10,'2024-06-02T00:00:00Z','2024-06-04T00:00:00Z','ok');");
            Exec(db, "INSERT INTO FriendOwnership (Id, UserId, GameId, PlaytimeForeverMinutes, LastPlayedUtc, LastScrapedUtc, LastScrapeStatus) VALUES (6,4,300,999,'2024-06-02T00:00:00Z','2024-06-04T00:00:00Z','ok');");

            // Progress rows (Id = user*1000+game).
            Exec(db, "INSERT INTO UserGameProgress (Id, UserId, GameId) VALUES (2100,2,100),(3100,3,100),(2400,2,400),(3400,3,400),(2200,2,200),(4300,4,300);");

            // Unlocks: A on G1 (def1,def2), B on G1 (def1); A on G4 (def3); C on G3 (should be excluded).
            Exec(db, "INSERT INTO UserAchievements (Id, UserGameProgressId, AchievementDefinitionId, Unlocked, UnlockTimeUtc) VALUES (1,2100,1,1,'2024-05-15T00:00:00Z');");
            Exec(db, "INSERT INTO UserAchievements (Id, UserGameProgressId, AchievementDefinitionId, Unlocked, UnlockTimeUtc) VALUES (2,2100,2,1,'2024-05-16T00:00:00Z');");
            Exec(db, "INSERT INTO UserAchievements (Id, UserGameProgressId, AchievementDefinitionId, Unlocked, UnlockTimeUtc) VALUES (3,3100,1,1,'2024-05-20T00:00:00Z');");
            Exec(db, "INSERT INTO UserAchievements (Id, UserGameProgressId, AchievementDefinitionId, Unlocked, UnlockTimeUtc) VALUES (4,2400,3,1,'2024-06-10T00:00:00Z');");
            // A locked achievement (Unlocked = 0) must be ignored by the aggregates.
            Exec(db, "INSERT INTO UserAchievements (Id, UserGameProgressId, AchievementDefinitionId, Unlocked, UnlockTimeUtc) VALUES (5,3400,3,0,NULL);");
            // Inactive friend C's unlock on G3 - excluded because C is not an active friend.
            Exec(db, "INSERT INTO UserAchievements (Id, UserGameProgressId, AchievementDefinitionId, Unlocked, UnlockTimeUtc) VALUES (6,4300,1,1,'2024-06-11T00:00:00Z');");
        }

        private static void Exec(SQLiteDatabase db, string sql) => db.ExecuteNonQuery(sql);

        private static void AssertRowsEqual<T>(IReadOnlyList<T> expected, IReadOnlyList<T> actual)
        {
            Assert.AreEqual(expected.Count, actual.Count, "Row counts differ.");

            var properties = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance);
            for (var i = 0; i < expected.Count; i++)
            {
                foreach (var property in properties)
                {
                    var expectedValue = property.GetValue(expected[i]);
                    var actualValue = property.GetValue(actual[i]);
                    Assert.AreEqual(
                        expectedValue,
                        actualValue,
                        $"Row {i} property {property.Name} differs (legacy={expectedValue ?? "null"}, rewritten={actualValue ?? "null"}).");
                }
            }
        }

        private sealed class GameSummaryTestRow
        {
            public string ProviderKey { get; set; }
            public long? ProviderGameId { get; set; }
            public string PlayniteGameId { get; set; }
            public string GameName { get; set; }
            public long FriendCount { get; set; }
            public long FriendsWithUnlocksCount { get; set; }
            public long UnlockedAchievementsCount { get; set; }
            public long UniqueUnlockedAchievementsCount { get; set; }
            public long TotalAchievements { get; set; }
            public string LastUnlockUtc { get; set; }
            public long TotalPlaytimeMinutes { get; set; }
            public long AveragePlaytimeMinutes { get; set; }
            public string LastPlayedUtc { get; set; }
            public string LastScrapedUtc { get; set; }
            public string LastScrapeStatus { get; set; }
            public string IconPath { get; set; }
            public string CoverPath { get; set; }
        }

        private sealed class FriendSummaryTestRow
        {
            public string ProviderKey { get; set; }
            public string ExternalUserId { get; set; }
            public string DisplayName { get; set; }
            public string AvatarUrl { get; set; }
            public string AvatarPath { get; set; }
            public long SharedGamesCount { get; set; }
            public long GamesWithUnlocksCount { get; set; }
            public long UnlockedAchievementsCount { get; set; }
            public long RecentUnlockCount { get; set; }
            public string LastUnlockUtc { get; set; }
            public string LastRefreshedUtc { get; set; }
            public long TotalPlaytimeMinutes { get; set; }
        }

        private const string LegacyGameSummarySql = @"SELECT
                    g.ProviderKey AS ProviderKey,
                    g.ProviderGameId AS ProviderGameId,
                    g.PlayniteGameId AS PlayniteGameId,
                    g.GameName AS GameName,
                    (
                        SELECT COUNT(DISTINCT u.Id)
                        FROM FriendOwnership fo
                        INNER JOIN Users u ON u.Id = fo.UserId
                        WHERE fo.GameId = g.Id
                          AND u.IsCurrentUser = 0 AND u.IsActiveFriend = 1 AND u.FriendSource IS NOT NULL
                    ) AS FriendCount,
                    (
                        SELECT COUNT(DISTINCT u.Id)
                        FROM Users u
                        INNER JOIN UserGameProgress ugp ON ugp.UserId = u.Id AND ugp.GameId = g.Id
                        INNER JOIN UserAchievements ua ON ua.UserGameProgressId = ugp.Id AND ua.Unlocked = 1
                        WHERE u.IsCurrentUser = 0 AND u.IsActiveFriend = 1 AND u.FriendSource IS NOT NULL
                    ) AS FriendsWithUnlocksCount,
                    (
                        SELECT COUNT(DISTINCT ua.Id)
                        FROM Users u
                        INNER JOIN UserGameProgress ugp ON ugp.UserId = u.Id AND ugp.GameId = g.Id
                        INNER JOIN UserAchievements ua ON ua.UserGameProgressId = ugp.Id AND ua.Unlocked = 1
                        WHERE u.IsCurrentUser = 0 AND u.IsActiveFriend = 1 AND u.FriendSource IS NOT NULL
                    ) AS UnlockedAchievementsCount,
                    (
                        SELECT COUNT(DISTINCT ua.AchievementDefinitionId)
                        FROM Users u
                        INNER JOIN UserGameProgress ugp ON ugp.UserId = u.Id AND ugp.GameId = g.Id
                        INNER JOIN UserAchievements ua ON ua.UserGameProgressId = ugp.Id AND ua.Unlocked = 1
                        WHERE u.IsCurrentUser = 0 AND u.IsActiveFriend = 1 AND u.FriendSource IS NOT NULL
                    ) AS UniqueUnlockedAchievementsCount,
                    (
                        SELECT COUNT(DISTINCT ad.Id)
                        FROM AchievementDefinitions ad
                        WHERE ad.GameId = g.Id
                    ) AS TotalAchievements,
                    (
                        SELECT MAX(ua.UnlockTimeUtc)
                        FROM Users u
                        INNER JOIN UserGameProgress ugp ON ugp.UserId = u.Id AND ugp.GameId = g.Id
                        INNER JOIN UserAchievements ua ON ua.UserGameProgressId = ugp.Id AND ua.Unlocked = 1
                        WHERE u.IsCurrentUser = 0 AND u.IsActiveFriend = 1 AND u.FriendSource IS NOT NULL
                    ) AS LastUnlockUtc,
                    (
                        SELECT COALESCE(SUM(fo.PlaytimeForeverMinutes), 0)
                        FROM FriendOwnership fo
                        INNER JOIN Users u ON u.Id = fo.UserId
                        WHERE fo.GameId = g.Id
                          AND u.IsCurrentUser = 0 AND u.IsActiveFriend = 1 AND u.FriendSource IS NOT NULL
                    ) AS TotalPlaytimeMinutes,
                    (
                        SELECT COALESCE(AVG(fo.PlaytimeForeverMinutes), 0)
                        FROM FriendOwnership fo
                        INNER JOIN Users u ON u.Id = fo.UserId
                        WHERE fo.GameId = g.Id
                          AND u.IsCurrentUser = 0 AND u.IsActiveFriend = 1 AND u.FriendSource IS NOT NULL
                    ) AS AveragePlaytimeMinutes,
                    (
                        SELECT MAX(fo.LastPlayedUtc)
                        FROM FriendOwnership fo
                        INNER JOIN Users u ON u.Id = fo.UserId
                        WHERE fo.GameId = g.Id
                          AND u.IsCurrentUser = 0 AND u.IsActiveFriend = 1 AND u.FriendSource IS NOT NULL
                    ) AS LastPlayedUtc,
                    (
                        SELECT MAX(fo.LastScrapedUtc)
                        FROM FriendOwnership fo
                        INNER JOIN Users u ON u.Id = fo.UserId
                        WHERE fo.GameId = g.Id
                          AND u.IsCurrentUser = 0 AND u.IsActiveFriend = 1 AND u.FriendSource IS NOT NULL
                    ) AS LastScrapedUtc,
                    (
                        SELECT fo.LastScrapeStatus
                        FROM FriendOwnership fo
                        INNER JOIN Users u ON u.Id = fo.UserId
                        WHERE fo.GameId = g.Id
                          AND u.IsCurrentUser = 0 AND u.IsActiveFriend = 1 AND u.FriendSource IS NOT NULL
                          AND fo.LastScrapeStatus IS NOT NULL
                        ORDER BY fo.LastScrapedUtc DESC
                        LIMIT 1
                    ) AS LastScrapeStatus,
                    g.IconPath AS IconPath,
                    g.CoverPath AS CoverPath
                  FROM Games g
                  WHERE EXISTS (
                        SELECT 1
                        FROM FriendOwnership fo
                        INNER JOIN Users u ON u.Id = fo.UserId
                        WHERE fo.GameId = g.Id
                          AND u.IsCurrentUser = 0 AND u.IsActiveFriend = 1 AND u.FriendSource IS NOT NULL
                    )
                  ORDER BY LastUnlockUtc DESC, g.GameName;";

        private const string RewrittenGameSummarySql = @"WITH ownership AS (
                    SELECT fo.GameId AS GameId,
                           COUNT(DISTINCT u.Id) AS FriendCount,
                           COALESCE(SUM(fo.PlaytimeForeverMinutes), 0) AS TotalPlaytimeMinutes,
                           COALESCE(AVG(fo.PlaytimeForeverMinutes), 0) AS AveragePlaytimeMinutes,
                           MAX(fo.LastPlayedUtc) AS LastPlayedUtc,
                           MAX(fo.LastScrapedUtc) AS LastScrapedUtc
                    FROM FriendOwnership fo
                    INNER JOIN Users u ON u.Id = fo.UserId
                    WHERE u.IsCurrentUser = 0 AND u.IsActiveFriend = 1 AND u.FriendSource IS NOT NULL
                    GROUP BY fo.GameId
                ),
                unlocks AS (
                    SELECT ugp.GameId AS GameId,
                           COUNT(DISTINCT u.Id) AS FriendsWithUnlocksCount,
                           COUNT(ua.Id) AS UnlockedAchievementsCount,
                           COUNT(DISTINCT ua.AchievementDefinitionId) AS UniqueUnlockedAchievementsCount,
                           MAX(ua.UnlockTimeUtc) AS LastUnlockUtc
                    FROM Users u
                    INNER JOIN UserGameProgress ugp ON ugp.UserId = u.Id
                    INNER JOIN UserAchievements ua ON ua.UserGameProgressId = ugp.Id AND ua.Unlocked = 1
                    WHERE u.IsCurrentUser = 0 AND u.IsActiveFriend = 1 AND u.FriendSource IS NOT NULL
                    GROUP BY ugp.GameId
                ),
                totals AS (
                    SELECT ad.GameId AS GameId, COUNT(*) AS TotalAchievements
                    FROM AchievementDefinitions ad
                    GROUP BY ad.GameId
                ),
                scrapeStatus AS (
                    SELECT GameId, LastScrapeStatus
                    FROM (
                        SELECT fo.GameId AS GameId,
                               fo.LastScrapeStatus AS LastScrapeStatus,
                               ROW_NUMBER() OVER (PARTITION BY fo.GameId ORDER BY fo.LastScrapedUtc DESC) AS rn
                        FROM FriendOwnership fo
                        INNER JOIN Users u ON u.Id = fo.UserId
                        WHERE u.IsCurrentUser = 0 AND u.IsActiveFriend = 1 AND u.FriendSource IS NOT NULL
                          AND fo.LastScrapeStatus IS NOT NULL
                    )
                    WHERE rn = 1
                )
                SELECT
                    g.ProviderKey AS ProviderKey,
                    g.ProviderGameId AS ProviderGameId,
                    g.PlayniteGameId AS PlayniteGameId,
                    g.GameName AS GameName,
                    o.FriendCount AS FriendCount,
                    COALESCE(un.FriendsWithUnlocksCount, 0) AS FriendsWithUnlocksCount,
                    COALESCE(un.UnlockedAchievementsCount, 0) AS UnlockedAchievementsCount,
                    COALESCE(un.UniqueUnlockedAchievementsCount, 0) AS UniqueUnlockedAchievementsCount,
                    COALESCE(t.TotalAchievements, 0) AS TotalAchievements,
                    un.LastUnlockUtc AS LastUnlockUtc,
                    o.TotalPlaytimeMinutes AS TotalPlaytimeMinutes,
                    o.AveragePlaytimeMinutes AS AveragePlaytimeMinutes,
                    o.LastPlayedUtc AS LastPlayedUtc,
                    o.LastScrapedUtc AS LastScrapedUtc,
                    ss.LastScrapeStatus AS LastScrapeStatus,
                    g.IconPath AS IconPath,
                    g.CoverPath AS CoverPath
                  FROM Games g
                  INNER JOIN ownership o ON o.GameId = g.Id
                  LEFT JOIN unlocks un ON un.GameId = g.Id
                  LEFT JOIN totals t ON t.GameId = g.Id
                  LEFT JOIN scrapeStatus ss ON ss.GameId = g.Id
                  ORDER BY un.LastUnlockUtc DESC, g.GameName;";

        private const string LegacyFriendSummarySql = @"SELECT
                    u.ProviderKey AS ProviderKey,
                    u.ExternalUserId AS ExternalUserId,
                    u.DisplayName AS DisplayName,
                    u.AvatarUrl AS AvatarUrl,
                    u.AvatarPath AS AvatarPath,
                    (
                        SELECT COUNT(DISTINCT fo.GameId)
                        FROM FriendOwnership fo
                        WHERE fo.UserId = u.Id
                    ) AS SharedGamesCount,
                    (
                        SELECT COUNT(DISTINCT ugp.GameId)
                        FROM UserGameProgress ugp
                        INNER JOIN UserAchievements ua ON ua.UserGameProgressId = ugp.Id AND ua.Unlocked = 1
                        WHERE ugp.UserId = u.Id
                    ) AS GamesWithUnlocksCount,
                    (
                        SELECT COUNT(DISTINCT ua.Id)
                        FROM UserGameProgress ugp
                        INNER JOIN UserAchievements ua ON ua.UserGameProgressId = ugp.Id AND ua.Unlocked = 1
                        WHERE ugp.UserId = u.Id
                    ) AS UnlockedAchievementsCount,
                    (
                        SELECT COUNT(DISTINCT ua.Id)
                        FROM UserGameProgress ugp
                        INNER JOIN UserAchievements ua ON ua.UserGameProgressId = ugp.Id AND ua.Unlocked = 1
                        WHERE ugp.UserId = u.Id
                          AND ua.UnlockTimeUtc IS NOT NULL
                          AND ua.UnlockTimeUtc >= ?
                    ) AS RecentUnlockCount,
                    (
                        SELECT MAX(ua.UnlockTimeUtc)
                        FROM UserGameProgress ugp
                        INNER JOIN UserAchievements ua ON ua.UserGameProgressId = ugp.Id AND ua.Unlocked = 1
                        WHERE ugp.UserId = u.Id
                    ) AS LastUnlockUtc,
                    u.LastRefreshedUtc AS LastRefreshedUtc,
                    (
                        SELECT COALESCE(SUM(fo.PlaytimeForeverMinutes), 0)
                        FROM FriendOwnership fo
                        WHERE fo.UserId = u.Id
                    ) AS TotalPlaytimeMinutes
                  FROM Users u
                  WHERE u.IsCurrentUser = 0
                    AND u.IsActiveFriend = 1
                    AND u.FriendSource IS NOT NULL
                  ORDER BY LastUnlockUtc DESC, u.DisplayName;";

        private const string RewrittenFriendSummarySql = @"WITH ownership AS (
                    SELECT fo.UserId AS UserId,
                           COUNT(DISTINCT fo.GameId) AS SharedGamesCount,
                           COALESCE(SUM(fo.PlaytimeForeverMinutes), 0) AS TotalPlaytimeMinutes
                    FROM FriendOwnership fo
                    INNER JOIN Users u ON u.Id = fo.UserId
                    WHERE u.IsCurrentUser = 0 AND u.IsActiveFriend = 1 AND u.FriendSource IS NOT NULL
                    GROUP BY fo.UserId
                ),
                unlocks AS (
                    SELECT ugp.UserId AS UserId,
                           COUNT(DISTINCT ugp.GameId) AS GamesWithUnlocksCount,
                           COUNT(ua.Id) AS UnlockedAchievementsCount,
                           COUNT(CASE WHEN ua.UnlockTimeUtc IS NOT NULL AND ua.UnlockTimeUtc >= ? THEN ua.Id END) AS RecentUnlockCount,
                           MAX(ua.UnlockTimeUtc) AS LastUnlockUtc
                    FROM UserGameProgress ugp
                    INNER JOIN Users u ON u.Id = ugp.UserId
                    INNER JOIN UserAchievements ua ON ua.UserGameProgressId = ugp.Id AND ua.Unlocked = 1
                    WHERE u.IsCurrentUser = 0 AND u.IsActiveFriend = 1 AND u.FriendSource IS NOT NULL
                    GROUP BY ugp.UserId
                )
                SELECT
                    u.ProviderKey AS ProviderKey,
                    u.ExternalUserId AS ExternalUserId,
                    u.DisplayName AS DisplayName,
                    u.AvatarUrl AS AvatarUrl,
                    u.AvatarPath AS AvatarPath,
                    COALESCE(o.SharedGamesCount, 0) AS SharedGamesCount,
                    COALESCE(un.GamesWithUnlocksCount, 0) AS GamesWithUnlocksCount,
                    COALESCE(un.UnlockedAchievementsCount, 0) AS UnlockedAchievementsCount,
                    COALESCE(un.RecentUnlockCount, 0) AS RecentUnlockCount,
                    un.LastUnlockUtc AS LastUnlockUtc,
                    u.LastRefreshedUtc AS LastRefreshedUtc,
                    COALESCE(o.TotalPlaytimeMinutes, 0) AS TotalPlaytimeMinutes
                  FROM Users u
                  LEFT JOIN ownership o ON o.UserId = u.Id
                  LEFT JOIN unlocks un ON un.UserId = u.Id
                  WHERE u.IsCurrentUser = 0
                    AND u.IsActiveFriend = 1
                    AND u.FriendSource IS NOT NULL
                  ORDER BY un.LastUnlockUtc DESC, u.DisplayName;";
    }
}
