using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.Database;
using SqlNado;

namespace PlayniteAchievements.SqlNado.Tests
{
    /// <summary>
    /// Runs the resolved definitions view against seeded platform and metadata rows, and checks it
    /// agrees with <see cref="LayerResolver"/> on the same layers.
    /// </summary>
    [TestClass]
    public class AchievementLayerViewTests
    {
        private sealed class ResolvedRow
        {
            public string ApiName { get; set; }
            public string DisplayName { get; set; }
            public string Description { get; set; }
            public int? Points { get; set; }
            public string Category { get; set; }
            public string ProviderCategory { get; set; }
            public string CategoryKind { get; set; }
            public string Tags { get; set; }
            public double? GlobalPercentUnlocked { get; set; }
            public string Rarity { get; set; }
            public int? MetadataOrder { get; set; }
        }

        private sealed class CountRow
        {
            public long Count { get; set; }
        }

        private const string GameId = "0b7d2f0e-4c41-4c55-9a43-3f1c2a6e8d11";

        [TestMethod]
        public void View_ResolvesCustomOverMetadata_AndKeepsTheProviderCategory()
        {
            WithDatabase(db =>
            {
                Definition(db, "custom", "Own", "Desc", 10, "Steam", null, "Softcore|Unobtainable", null, "Common");
                Metadata(db, "custom", displayName: "Borrowed", points: 50, tags: "Collectable");
                db.ExecuteNonQuery(
                    @"INSERT INTO AchievementOverrides (PlayniteGameId, ApiName, DisplayName, Category, Tags, Cleared, UpdatedUtc)
                      VALUES (?, 'custom', 'Mine', 'My Story', 'Challenge', ?, '');",
                    GameId,
                    (long)AchievementField.Description);

                var row = db.Load<ResolvedRow>("SELECT * FROM " + AchievementLayerSql.ViewName + ";").Single();

                Assert.AreEqual("Mine", row.DisplayName);
                Assert.IsNull(row.Description, "a cleared custom description hides the platform's");
                Assert.AreEqual(50, row.Points, "metadata shows where the user set nothing");
                Assert.AreEqual("My Story", row.Category);
                Assert.AreEqual("Steam", row.ProviderCategory, "the category before the user's");
                CollectionAssert.AreEquivalent(
                    new[] { "Challenge", "Softcore" },
                    AchievementTagHelper.Parse(row.Tags),
                    "custom tags replace Unobtainable but keep the derived tags");
            });
        }

        [TestMethod]
        public void View_ResolvesMetadataOverPlatform_LikeTheResolver()
        {
            WithDatabase(db =>
            {
                Definition(db, "plain", "Own", "Desc", 10, "Default", null, "Missable", null, "Common");
                Definition(db, "borrowed", "Own", "Desc", 10, "Steam", "dlc", "Softcore|Unobtainable|Missable", 40.0, "Common");
                Metadata(db, "borrowed", displayName: "Borrowed", points: 50, tags: "Collectable", percent: 2.0, rarity: "UltraRare", order: 3,
                    cleared: AchievementField.Category | AchievementField.CategoryKind | AchievementField.Description);

                var rows = db.Load<ResolvedRow>("SELECT * FROM " + AchievementLayerSql.ViewName + " ORDER BY Id;").ToList();

                var plain = rows.Single(row => row.ApiName == "plain");
                Assert.AreEqual("Own", plain.DisplayName);
                Assert.AreEqual(10, plain.Points);
                Assert.AreEqual("Default", plain.Category);
                Assert.AreEqual("Missable", plain.Tags);

                var borrowed = rows.Single(row => row.ApiName == "borrowed");
                Assert.AreEqual("Borrowed", borrowed.DisplayName);
                Assert.IsNull(borrowed.Description);
                Assert.AreEqual(50, borrowed.Points);
                Assert.AreEqual("Default", borrowed.Category);
                Assert.IsNull(borrowed.CategoryKind);
                Assert.AreEqual(2.0, borrowed.GlobalPercentUnlocked);
                Assert.AreEqual("UltraRare", borrowed.Rarity);
                Assert.AreEqual(3, borrowed.MetadataOrder);

                var resolved = LayerResolver.Resolve(
                    new AchievementLayerValues("Own", "Desc", points: 10, category: "Steam", categoryKind: "dlc", tags: "Softcore|Unobtainable|Missable"),
                    new AchievementLayerValues("Borrowed", points: 50, tags: "Collectable",
                        cleared: AchievementField.Category | AchievementField.CategoryKind | AchievementField.Description),
                    null).Values;
                Assert.AreEqual(resolved.DisplayName, borrowed.DisplayName);
                Assert.AreEqual(resolved.Description, borrowed.Description);
                Assert.AreEqual(resolved.Points, borrowed.Points);
                Assert.AreEqual(resolved.CategoryKind, borrowed.CategoryKind);
                CollectionAssert.AreEquivalent(
                    AchievementTagHelper.Parse(resolved.Tags),
                    AchievementTagHelper.Parse(borrowed.Tags),
                    "metadata tags plus the platform's derived tags and Unobtainable");
            });
        }

        [TestMethod]
        public void DeletingADefinition_DropsItsMetadataLayer()
        {
            WithDatabase(db =>
            {
                Definition(db, "gone", "Own", null, null, "Default", null, null, null, "Common");
                Metadata(db, "gone", displayName: "Borrowed");

                db.ExecuteNonQuery("DELETE FROM PlatformDefinitions WHERE ApiName = 'gone';");

                var left = db.Load<CountRow>("SELECT COUNT(*) AS Count FROM AchievementMetadataLayer;").Single();
                Assert.AreEqual(0, left.Count);
            });
        }

        private sealed class UserRow
        {
            public string ApiName { get; set; }
            public string DisplayName { get; set; }
            public string Note { get; set; }
            public long IsGoal { get; set; }
            public long? CustomOrder { get; set; }
            public long IsFiltered { get; set; }
            public long Unlocked { get; set; }
            public string GameName { get; set; }
        }

        [TestMethod]
        public void UserView_JoinsTheCurrentUsersUnlocks_AndCarriesTheUserState()
        {
            WithDatabase(db =>
            {
                Definition(db, "won", "Won", null, null, "Default", null, null, null, "Common");
                Definition(db, "open", "Open", null, null, "Default", null, null, null, "Common");
                var wonId = db.ExecuteScalar<long>("SELECT Id FROM PlatformDefinitions WHERE ApiName = 'won';");
                db.ExecuteNonQuery("INSERT INTO UserAchievements (UserGameProgressId, AchievementDefinitionId, Unlocked) VALUES (1, ?, 1);", wonId);
                db.ExecuteNonQuery("INSERT INTO UserAchievements (UserGameProgressId, AchievementDefinitionId, Unlocked) VALUES (2, ?, 0);", wonId);
                db.ExecuteNonQuery(
                    @"INSERT INTO AchievementOverrides (PlayniteGameId, ApiName, Note, GoalOrder, CustomOrder, IsFiltered, UpdatedUtc)
                      VALUES (?, 'open', 'route note', 0, 3, 1, '');",
                    GameId);

                var rows = db.Load<UserRow>("SELECT * FROM " + AchievementLayerSql.UserViewName + " ORDER BY ApiName;").ToList();

                Assert.AreEqual(2, rows.Count, "one row per achievement for the current user only");
                var open = rows.Single(row => row.ApiName == "open");
                Assert.AreEqual(0, open.Unlocked);
                Assert.AreEqual("route note", open.Note);
                Assert.AreEqual(1, open.IsGoal);
                Assert.AreEqual(3L, open.CustomOrder);
                Assert.AreEqual(1, open.IsFiltered);
                Assert.AreEqual("Game", open.GameName);
                Assert.AreEqual(1, rows.Single(row => row.ApiName == "won").Unlocked);
            });
        }

        private sealed class GapRow
        {
            public string ApiName { get; set; }
            public string DisplayName { get; set; }
            public string UnlockedIconPath { get; set; }
            public long IsCapstone { get; set; }
            public string CategoryKind { get; set; }
            public long Unlocked { get; set; }
            public string UnlockTimeUtc { get; set; }
            public long IsAuthored { get; set; }
            public long ExcludedFromSummaries { get; set; }
            public string CleanCapturePath { get; set; }
        }

        [TestMethod]
        public void UserView_AppliesIconsCapstonesKindsUnlockTimes_AndListsAuthoredAchievements()
        {
            WithDatabase(db =>
            {
                Definition(db, "won", "Won", null, null, "DLC One", "base", null, null, "Common");
                Definition(db, "plat", "Platinum", null, null, "Default", null, null, null, "Common");
                db.ExecuteNonQuery("UPDATE PlatformDefinitions SET IsCapstone = 1 WHERE ApiName = 'plat';");
                var wonId = db.ExecuteScalar<long>("SELECT Id FROM PlatformDefinitions WHERE ApiName = 'won';");
                db.ExecuteNonQuery(
                    "INSERT INTO UserAchievements (UserGameProgressId, AchievementDefinitionId, Unlocked, UnlockTimeUtc) VALUES (1, ?, 1, '2025-01-01T00:00:00Z');",
                    wonId);
                db.ExecuteNonQuery(
                    @"INSERT INTO AchievementOverrides (PlayniteGameId, ApiName, UnlockedIconPath, UnlockTimeUtc, CapstoneAssigned, UpdatedUtc)
                      VALUES (?, 'won', 'custom/won.png', '2026-02-02T00:00:00Z', 1, '');",
                    GameId);
                db.ExecuteNonQuery(
                    @"INSERT INTO AchievementOverrides (PlayniteGameId, ApiName, DisplayName, IsAuthored, Unlocked, Category, UpdatedUtc)
                      VALUES (?, 'custom:mine', 'Mine', 1, 1, 'Side', '');",
                    GameId);
                db.ExecuteNonQuery("INSERT INTO GameCustomState (PlayniteGameId, CapstonesMaterialized, ExcludedFromSummaries) VALUES (?, 1, 1);", GameId);
                db.ExecuteNonQuery("INSERT INTO AchievementCategories (PlayniteGameId, Category, Kind) VALUES (?, 'DLC One', 'DLC');", GameId);
                db.ExecuteNonQuery("INSERT INTO AchievementCaptures (PlayniteGameId, ApiName, CleanPath) VALUES (?, 'won', 'C:/captures/won.png');", GameId);

                var rows = db.Load<GapRow>("SELECT * FROM " + AchievementLayerSql.UserViewName + ";").ToList();

                var won = rows.Single(row => row.ApiName == "won");
                Assert.AreEqual("custom/won.png", won.UnlockedIconPath, "the user's icon");
                Assert.AreEqual(1, won.IsCapstone, "in the stored capstone set");
                Assert.AreEqual("DLC", won.CategoryKind, "the kind stored for its category");
                Assert.AreEqual("2026-02-02T00:00:00Z", won.UnlockTimeUtc, "the user's unlock time on an unlocked row");
                Assert.AreEqual(1, won.ExcludedFromSummaries);
                Assert.AreEqual("C:/captures/won.png", won.CleanCapturePath);
                Assert.AreEqual(0, rows.Single(row => row.ApiName == "plat").IsCapstone, "an edited capstone set replaces the provider's flag");
                var mine = rows.Single(row => row.ApiName == "custom:mine");
                Assert.AreEqual(1, mine.IsAuthored);
                Assert.AreEqual("Mine", mine.DisplayName);
                Assert.AreEqual(1, mine.Unlocked);
            });
        }

        private sealed class SqlRow
        {
            public string Sql { get; set; }
        }

        [TestMethod]
        public void UpgradingAV21Database_RenamesTheTable_KeepsTheRows_AndBacksUpFirst()
        {
            var dir = Path.Combine(Path.GetTempPath(), "playach-upgrade-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "achievement_cache.db");
            var schema = new SqlNadoSchemaManager(null, path, dir);
            try
            {
                using (var db = Open(path))
                {
                    // A current database turned back into the v21 shape: no views, and the
                    // platform's definitions in the AchievementDefinitions table.
                    schema.EnsureSchema(db);
                    db.ExecuteNonQuery("INSERT INTO Games (Id, ProviderKey, FirstSeenUtc, LastUpdatedUtc) VALUES (1, 'Steam', '', '');");
                    Definition(db, "a", "Platform name", null, null, "Default", null, null, null, "Common");
                    var id = db.ExecuteScalar<long>("SELECT Id FROM PlatformDefinitions WHERE ApiName = 'a';");
                    db.ExecuteNonQuery(AchievementLayerSql.InsertSql, id, "Borrowed", null, null, null, null, null, null, null, null, null, null, null, 0L);
                    foreach (var view in AchievementLayerSql.AllViewNames)
                    {
                        db.ExecuteNonQuery("DROP VIEW IF EXISTS " + view + ";");
                    }

                    db.ExecuteNonQuery("ALTER TABLE PlatformDefinitions RENAME TO AchievementDefinitions;");
                    db.ExecuteNonQuery("UPDATE CacheMetadata SET Value = '21' WHERE Key = 'schema_version';");
                }

                using (var db = Open(path))
                {
                    schema.EnsureSchema(db);

                    string TypeOf(string name) =>
                        db.Load<SqlRow>("SELECT type AS Sql FROM sqlite_master WHERE name = ?;", name).Single().Sql;
                    Assert.AreEqual("table", TypeOf("PlatformDefinitions"));
                    Assert.AreEqual("view", TypeOf("AchievementDefinitions"));
                    Assert.AreEqual("Borrowed", db.ExecuteScalar<string>("SELECT DisplayName FROM AchievementDefinitions WHERE ApiName = 'a';"));
                    Assert.AreEqual("22", db.ExecuteScalar<string>("SELECT Value FROM CacheMetadata WHERE Key = 'schema_version';"));

                    // The metadata layer's foreign key and delete trigger follow the renamed table.
                    db.ExecuteNonQuery("DELETE FROM PlatformDefinitions WHERE ApiName = 'a';");
                    Assert.AreEqual(0L, db.ExecuteScalar<long>("SELECT COUNT(*) FROM AchievementMetadataLayer;"));
                }

                var backups = Directory.GetDirectories(dir, "db-schema-*", SearchOption.AllDirectories);
                Assert.AreEqual(1, backups.Length, "one backup, made before the rename");
                Assert.IsTrue(File.Exists(Path.Combine(backups[0], "achievement_cache.db")));
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch (IOException) { }
            }
        }

        [TestMethod]
        public void EveryTable_IsClearedWithThePlatformLayer_OrKept()
        {
            var dir = Path.Combine(Path.GetTempPath(), "playach-tables-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, SqlNadoSchemaManager.DatabaseFileName);
            try
            {
                using (var db = Open(path))
                {
                    new SqlNadoSchemaManager(null, path, dir).EnsureSchema(db);
                    var tables = db.Load<SqlRow>("SELECT name AS Sql FROM sqlite_master WHERE type = 'table';")
                        .Select(row => row.Sql)
                        .ToList();

                    // Custom data's mirror, the capture index and the schema's own bookkeeping.
                    var kept = new[] { "CacheMetadata", "AchievementOverrides", "GameCustomState", "AchievementCategories", "AchievementCaptures", "sqlite_sequence" };
                    CollectionAssert.AreEquivalent(
                        tables,
                        SqlNadoSchemaManager.PlatformAndMetadataTables.Concat(kept).ToList(),
                        "a new table has to be placed in a layer");
                }
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch (IOException) { }
            }
        }

        [TestMethod]
        public void ClearingPlatformData_ForEveryGame_KeepsTheCustomData()
        {
            var dir = Path.Combine(Path.GetTempPath(), "playach-clear-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, SqlNadoSchemaManager.DatabaseFileName);
            try
            {
                using (var db = Open(path))
                {
                    new SqlNadoSchemaManager(null, path, dir).EnsureSchema(db);
                    db.ExecuteNonQuery("CREATE TABLE GameCustomData (PlayniteGameId TEXT PRIMARY KEY NOT NULL, PayloadJson TEXT NOT NULL, UpdatedUtc TEXT NOT NULL);");
                    db.ExecuteNonQuery("INSERT INTO GameCustomData VALUES ('g', '{}', '');");
                    db.ExecuteNonQuery("INSERT INTO AchievementOverrides (PlayniteGameId, ApiName, DisplayName, UpdatedUtc) VALUES ('g', 'a', 'Renamed', '');");
                    db.ExecuteNonQuery("INSERT INTO Games (Id, ProviderKey, FirstSeenUtc, LastUpdatedUtc) VALUES (1, 'Steam', '', '');");
                    Definition(db, "a", "Platform name", null, null, "Default", null, null, null, "Common");
                    Metadata(db, "a", displayName: "Borrowed");

                    SqlNadoSchemaManager.ClearPlatformAndMetadata(db);

                    foreach (var table in SqlNadoSchemaManager.PlatformAndMetadataTables)
                    {
                        Assert.AreEqual(0L, db.ExecuteScalar<long>("SELECT COUNT(*) FROM " + table + ";"), table);
                    }

                    Assert.AreEqual(1L, db.ExecuteScalar<long>("SELECT COUNT(*) FROM GameCustomData;"));
                    Assert.AreEqual(1L, db.ExecuteScalar<long>("SELECT COUNT(*) FROM AchievementOverrides;"));
                }
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch (IOException) { }
            }
        }

        private static SQLiteDatabase Open(string path) => new SQLiteDatabase(
            path,
            SQLiteOpenOptions.SQLITE_OPEN_READWRITE | SQLiteOpenOptions.SQLITE_OPEN_CREATE | SQLiteOpenOptions.SQLITE_OPEN_FULLMUTEX);

        private static void Definition(
            SQLiteDatabase db, string apiName, string name, string description, int? points,
            string category, string kind, string tags, double? percent, string rarity)
        {
            db.ExecuteNonQuery(
                @"INSERT INTO PlatformDefinitions
                    (GameId, ApiName, DisplayName, Description, Points, Category, CategoryKind, Tags, GlobalPercentUnlocked, Rarity, CreatedUtc, UpdatedUtc)
                  VALUES (1, ?, ?, ?, ?, ?, ?, ?, ?, ?, '', '');",
                apiName, name, description, points, category, kind, tags, percent, rarity);
        }

        private static void Metadata(
            SQLiteDatabase db, string apiName, string displayName = null, int? points = null, string tags = null,
            double? percent = null, string rarity = null, int? order = null, AchievementField cleared = AchievementField.None)
        {
            var id = db.ExecuteScalar<long>("SELECT Id FROM PlatformDefinitions WHERE ApiName = ?;", apiName);
            db.ExecuteNonQuery(
                AchievementLayerSql.InsertSql,
                id, displayName, null, null, null, points, null, null, null, tags, percent, rarity, order, (long)cleared);
        }

        private static void WithDatabase(Action<SQLiteDatabase> test)
        {
            var path = Path.Combine(Path.GetTempPath(), "playach-layers-" + Guid.NewGuid().ToString("N") + ".db");
            try
            {
                using (var db = new SQLiteDatabase(
                    path,
                    SQLiteOpenOptions.SQLITE_OPEN_READWRITE |
                    SQLiteOpenOptions.SQLITE_OPEN_CREATE |
                    SQLiteOpenOptions.SQLITE_OPEN_FULLMUTEX))
                {
                    db.ExecuteNonQuery(@"CREATE TABLE PlatformDefinitions (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        GameId INTEGER NOT NULL,
                        ApiName TEXT NOT NULL COLLATE NOCASE,
                        DisplayName TEXT NULL,
                        Description TEXT NULL,
                        UnlockedIconPath TEXT NULL,
                        LockedIconPath TEXT NULL,
                        Points INTEGER NULL,
                        ScaledPoints INTEGER NULL,
                        Category TEXT NOT NULL DEFAULT 'Default',
                        Tags TEXT NULL,
                        CategoryKind TEXT NULL,
                        TrophyType TEXT NULL,
                        Hidden INTEGER NOT NULL DEFAULT 0,
                        IsCapstone INTEGER NOT NULL DEFAULT 0,
                        GlobalPercentUnlocked REAL NULL,
                        Rarity TEXT NOT NULL DEFAULT 'Common',
                        ProgressMax INTEGER NULL,
                        MetadataOrder INTEGER NULL,
                        CreatedUtc TEXT NOT NULL,
                        UpdatedUtc TEXT NOT NULL);");
                    db.ExecuteNonQuery(@"CREATE TABLE Games (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        PlayniteGameId TEXT NULL,
                        GameName TEXT NULL,
                        ProviderKey TEXT NULL);");
                    db.ExecuteNonQuery("INSERT INTO Games (Id, PlayniteGameId, GameName, ProviderKey) VALUES (1, '" + GameId + "', 'Game', 'Steam');");
                    db.ExecuteNonQuery("CREATE TABLE Users (Id INTEGER PRIMARY KEY, IsCurrentUser INTEGER NOT NULL);");
                    db.ExecuteNonQuery("INSERT INTO Users (Id, IsCurrentUser) VALUES (1, 1), (2, 0);");
                    db.ExecuteNonQuery("CREATE TABLE UserGameProgress (Id INTEGER PRIMARY KEY, UserId INTEGER NOT NULL, GameId INTEGER NOT NULL);");
                    db.ExecuteNonQuery("INSERT INTO UserGameProgress (Id, UserId, GameId) VALUES (1, 1, 1), (2, 2, 1);");
                    db.ExecuteNonQuery(@"CREATE TABLE UserAchievements (
                        Id INTEGER PRIMARY KEY, UserGameProgressId INTEGER NOT NULL, AchievementDefinitionId INTEGER NOT NULL,
                        Unlocked INTEGER NOT NULL, UnlockTimeUtc TEXT NULL, ProgressNum INTEGER NULL, ProgressDenom INTEGER NULL);");
                    db.ExecuteNonQuery(@"CREATE TABLE AchievementOverrides (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        PlayniteGameId TEXT NOT NULL COLLATE NOCASE,
                        ApiName TEXT NOT NULL COLLATE NOCASE,
                        DisplayName TEXT NULL,
                        Description TEXT NULL,
                        Points INTEGER NULL,
                        TrophyType TEXT NULL COLLATE NOCASE,
                        Category TEXT NULL,
                        Tags TEXT NULL,
                        Hidden INTEGER NULL,
                        Cleared INTEGER NOT NULL DEFAULT 0,
                        Note TEXT NULL,
                        GoalOrder INTEGER NULL,
                        CustomOrder INTEGER NULL,
                        UnlockedIconPath TEXT NULL,
                        LockedIconPath TEXT NULL,
                        UnlockTimeUtc TEXT NULL,
                        CapstoneAssigned INTEGER NOT NULL DEFAULT 0,
                        IsAuthored INTEGER NOT NULL DEFAULT 0,
                        Unlocked INTEGER NOT NULL DEFAULT 0,
                        ProgressNum INTEGER NULL,
                        ProgressDenom INTEGER NULL,
                        ScaledPoints INTEGER NULL,
                        CategoryKind TEXT NULL,
                        GlobalPercentUnlocked REAL NULL,
                        Rarity TEXT NULL,
                        IsCapstone INTEGER NOT NULL DEFAULT 0,
                        IsFiltered INTEGER NOT NULL DEFAULT 0,
                        IsSummaryFiltered INTEGER NOT NULL DEFAULT 0,
                        UpdatedUtc TEXT NOT NULL,
                        UNIQUE (PlayniteGameId, ApiName));");
                    db.ExecuteNonQuery(AchievementLayerSql.CreateTableSql);
                    db.ExecuteNonQuery(AchievementLayerSql.CreateTriggerSql);
                    db.ExecuteNonQuery(AchievementLayerSql.CreateGameStateTableSql);
                    db.ExecuteNonQuery(AchievementLayerSql.CreateCategoriesTableSql);
                    db.ExecuteNonQuery(AchievementLayerSql.CreateCapturesTableSql);
                    foreach (var sql in AchievementLayerSql.RecreateViewSql())
                    {
                        db.ExecuteNonQuery(sql);
                    }

                    test(db);
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
    }
}
