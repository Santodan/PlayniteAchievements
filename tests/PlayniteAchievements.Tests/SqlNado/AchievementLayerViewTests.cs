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
            public string CategoryKind { get; set; }
            public string Tags { get; set; }
            public double? GlobalPercentUnlocked { get; set; }
            public string Rarity { get; set; }
            public int? MetadataOrder { get; set; }
            public string P_DisplayName { get; set; }
            public long HasMetadata { get; set; }
        }

        private sealed class CountRow
        {
            public long Count { get; set; }
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
                Assert.AreEqual(0, plain.HasMetadata);

                var borrowed = rows.Single(row => row.ApiName == "borrowed");
                Assert.AreEqual("Borrowed", borrowed.DisplayName);
                Assert.AreEqual("Own", borrowed.P_DisplayName);
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

                db.ExecuteNonQuery("DELETE FROM AchievementDefinitions WHERE ApiName = 'gone';");

                var left = db.Load<CountRow>("SELECT COUNT(*) AS Count FROM AchievementMetadataLayer;").Single();
                Assert.AreEqual(0, left.Count);
            });
        }

        private static void Definition(
            SQLiteDatabase db, string apiName, string name, string description, int? points,
            string category, string kind, string tags, double? percent, string rarity)
        {
            db.ExecuteNonQuery(
                @"INSERT INTO AchievementDefinitions
                    (GameId, ApiName, DisplayName, Description, Points, Category, CategoryKind, Tags, GlobalPercentUnlocked, Rarity, CreatedUtc, UpdatedUtc)
                  VALUES (1, ?, ?, ?, ?, ?, ?, ?, ?, ?, '', '');",
                apiName, name, description, points, category, kind, tags, percent, rarity);
        }

        private static void Metadata(
            SQLiteDatabase db, string apiName, string displayName = null, int? points = null, string tags = null,
            double? percent = null, string rarity = null, int? order = null, AchievementField cleared = AchievementField.None)
        {
            var id = db.ExecuteScalar<long>("SELECT Id FROM AchievementDefinitions WHERE ApiName = ?;", apiName);
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
                    db.ExecuteNonQuery(@"CREATE TABLE AchievementDefinitions (
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
                    db.ExecuteNonQuery(AchievementLayerSql.CreateTableSql);
                    db.ExecuteNonQuery(AchievementLayerSql.CreateTriggerSql);
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
