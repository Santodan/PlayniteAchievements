using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services.Database;
using SqlNado;

namespace PlayniteAchievements.Tests.SqlNado
{
    [TestClass]
    public class SavedGameStateTests
    {
        private const string Game = "6f1d2a3b-0000-4000-8000-000000000001";

        private static readonly string[] Tables =
        {
            "UserGameProgress", "Users", "Games", "PlatformDefinitions", "AchievementMetadataLayer", "UserAchievements"
        };

        [TestMethod]
        public void EveryColumnOfEverySavedRow_ChangesTheState_ButTheStampsDoNot()
        {
            WithSeededDatabase(db =>
            {
                var state = SqlNadoCacheBehavior.SavedGameState(db, Game, Game);
                foreach (var table in Tables)
                {
                    var columns = db.LoadRows("PRAGMA table_info(" + table + ");").Select(row => (string)row["name"]).ToList();
                    Assert.IsTrue(columns.Count > 1, table);
                    foreach (var column in columns)
                    {
                        db.ExecuteNonQuery("SAVEPOINT edit;");
                        db.ExecuteNonQuery("UPDATE " + table + " SET " + column + " = CASE WHEN typeof(" + column + ") IN ('integer', 'real') THEN " +
                                           column + " + 1 WHEN " + column + " IS NULL THEN 1 ELSE " + column + " || 'x' END;");
                        var edited = SqlNadoCacheBehavior.SavedGameState(db, Game, Game);
                        db.ExecuteNonQuery("ROLLBACK TO edit;");
                        db.ExecuteNonQuery("RELEASE edit;");

                        if (column == "LastUpdatedUtc" || column == "UpdatedUtc")
                        {
                            Assert.AreEqual(state, edited, table + "." + column + " is a stamp");
                        }
                        else
                        {
                            Assert.AreNotEqual(state, edited, table + "." + column);
                        }
                    }
                }

                Assert.AreEqual(state, SqlNadoCacheBehavior.SavedGameState(db, Game, Game), "every edit was rolled back");
            });
        }

        [TestMethod]
        public void AddedAndRemovedRows_ChangeTheState()
        {
            WithSeededDatabase(db =>
            {
                var state = SqlNadoCacheBehavior.SavedGameState(db, Game, Game);
                void AssertChangedBy(string sql)
                {
                    db.ExecuteNonQuery("SAVEPOINT edit;");
                    db.ExecuteNonQuery(sql);
                    var edited = SqlNadoCacheBehavior.SavedGameState(db, Game, Game);
                    db.ExecuteNonQuery("ROLLBACK TO edit;");
                    db.ExecuteNonQuery("RELEASE edit;");
                    Assert.AreNotEqual(state, edited, sql);
                }

                AssertChangedBy("INSERT INTO PlatformDefinitions (Id, GameId, ApiName, CreatedUtc, UpdatedUtc) VALUES (11, 1, 'b', '', '');");
                AssertChangedBy("DELETE FROM PlatformDefinitions WHERE Id = 10;");
                AssertChangedBy("DELETE FROM AchievementMetadataLayer;");
                AssertChangedBy("DELETE FROM UserAchievements;");
                AssertChangedBy("INSERT INTO Games (Id, ProviderKey, PlayniteGameId, FirstSeenUtc, LastUpdatedUtc) VALUES (2, 'Epic', '" + Game + "', '', '');");
                AssertChangedBy("UPDATE UserGameProgress SET GameId = 2;");
                AssertChangedBy("DELETE FROM UserGameProgress;");

                // Rows of other games are not part of it.
                db.ExecuteNonQuery("INSERT INTO Games (Id, ProviderKey, PlayniteGameId, FirstSeenUtc, LastUpdatedUtc) VALUES (3, 'Steam', 'other', '', '');");
                db.ExecuteNonQuery("INSERT INTO PlatformDefinitions (Id, GameId, ApiName, CreatedUtc, UpdatedUtc) VALUES (30, 3, 'o', '', '');");
                Assert.AreEqual(state, SqlNadoCacheBehavior.SavedGameState(db, Game, Game));
            });
        }

        [TestMethod]
        public void SeveralCurrentUserProgressRows_ForTheGame_AreReported()
        {
            WithSeededDatabase(db =>
            {
                Assert.IsFalse(SqlNadoCacheBehavior.HasSeveralCurrentUserProgressRows(db, Game));
                Assert.IsFalse(SqlNadoCacheBehavior.HasSeveralCurrentUserProgressRows(db, null));

                db.ExecuteNonQuery("INSERT INTO Users (Id, ProviderKey, ExternalUserId, IsCurrentUser, CreatedUtc, UpdatedUtc) VALUES (2, 'Epic', 'me', 1, '', ''), (3, 'Steam', 'friend', 0, '', '');");
                db.ExecuteNonQuery("INSERT INTO Games (Id, ProviderKey, PlayniteGameId, FirstSeenUtc, LastUpdatedUtc) VALUES (2, 'Epic', '" + Game + "', '', '');");
                db.ExecuteNonQuery("INSERT INTO UserGameProgress (UserId, GameId, CacheKey, LastUpdatedUtc, CreatedUtc, UpdatedUtc) VALUES (3, 1, 'friend:" + Game + "', '', '', '');");
                Assert.IsFalse(SqlNadoCacheBehavior.HasSeveralCurrentUserProgressRows(db, Game), "a friend's row does not count");

                db.ExecuteNonQuery("INSERT INTO UserGameProgress (UserId, GameId, CacheKey, LastUpdatedUtc, CreatedUtc, UpdatedUtc) VALUES (2, 2, 'epic:" + Game + "', '', '', '');");
                Assert.IsTrue(SqlNadoCacheBehavior.HasSeveralCurrentUserProgressRows(db, Game));
            });
        }

        private static void WithSeededDatabase(Action<SQLiteDatabase> test)
        {
            var dir = Path.Combine(Path.GetTempPath(), "playach-saved-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, SqlNadoSchemaManager.DatabaseFileName);
            try
            {
                using (var db = new SQLiteDatabase(
                    path,
                    SQLiteOpenOptions.SQLITE_OPEN_READWRITE | SQLiteOpenOptions.SQLITE_OPEN_CREATE | SQLiteOpenOptions.SQLITE_OPEN_FULLMUTEX))
                {
                    new SqlNadoSchemaManager(null, path, dir).EnsureSchema(db);
                    // Edits move keys a foreign key points at; only the reading is under test.
                    db.ExecuteNonQuery("PRAGMA foreign_keys = OFF;");
                    db.ExecuteNonQuery(@"INSERT INTO Users (Id, ProviderKey, ExternalUserId, DisplayName, ProviderNickname, IsCurrentUser, FriendSource, AvatarUrl, AvatarPath, LastRefreshedUtc, IsActiveFriend, CreatedUtc, UpdatedUtc)
                        VALUES (1, 'Steam', '76561', 'Me', 'me', 1, 'Steam', 'http://a', 'a.png', 't', 1, 'c', 'u');");
                    db.ExecuteNonQuery(@"INSERT INTO Games (Id, ProviderKey, ProviderPlatformKey, ProviderGameId, ProviderGameKey, PlayniteGameId, GameName, LibrarySourceName, IconPath, CoverPath, RaritySource, PlatformFields, MetadataFields, FirstSeenUtc, LastUpdatedUtc)
                        VALUES (1, 'Steam', 'Steam', 440, 'tf2', ?, 'Game', 'Steam', 'i', 'c', 'Steam', 1, 2, 'f', 'l');", Game);
                    db.ExecuteNonQuery(@"INSERT INTO UserGameProgress (Id, UserId, GameId, CacheKey, HasAchievements, AchievementsUnlocked, TotalAchievements, LastUpdatedUtc, CreatedUtc, UpdatedUtc)
                        VALUES (1, 1, 1, ?, 1, 1, 1, 'l', 'c', 'u');", Game);
                    db.ExecuteNonQuery(@"INSERT INTO PlatformDefinitions (Id, GameId, ApiName, DisplayName, Description, UnlockedIconPath, LockedIconPath, Points, ScaledPoints, Category, Tags, CategoryKind, TrophyType, Hidden, IsCapstone, GlobalPercentUnlocked, Rarity, ProgressMax, MetadataOrder, CreatedUtc, UpdatedUtc)
                        VALUES (10, 1, 'a', 'A', 'D', 'u.png', 'l.png', 10, 20, 'Default', 't', 'k', 'gold', 0, 0, 12.5, 'Rare', 5, 0, 'c', 'u');");
                    db.ExecuteNonQuery(AchievementLayerSql.InsertSql, 10L, "M", "MD", "mu.png", "ml.png", 5, "silver", "Cat", "kind", "tag", 3.5, "UltraRare", 1, 0L);
                    db.ExecuteNonQuery(@"INSERT INTO UserAchievements (Id, UserGameProgressId, AchievementDefinitionId, Unlocked, UnlockTimeUtc, ProgressNum, ProgressDenom, LastUpdatedUtc, CreatedUtc)
                        VALUES (100, 1, 10, 1, '2025-01-01T00:00:00.0000000Z', 5, 5, 'l', 'c');");
                    test(db);
                }
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch (IOException) { }
            }
        }
    }
}
