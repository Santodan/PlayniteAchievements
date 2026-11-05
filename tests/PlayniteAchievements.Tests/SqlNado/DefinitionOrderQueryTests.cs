using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlNado;

namespace PlayniteAchievements.SqlNado.Tests
{
    /// <summary>
    /// Runs the store's definition sort expression against seeded rows: achievements the metadata
    /// order source placed read first by their position, the rest after them by insertion order.
    /// </summary>
    [TestClass]
    public class DefinitionOrderQueryTests
    {
        private sealed class NameRow
        {
            public string ApiName { get; set; }
        }

        [TestMethod]
        public void PlacedAchievementsReadFirst_ThenTheRestInInsertionOrder()
        {
            var orderSql = ReadDefinitionOrderSql();
            var path = Path.Combine(Path.GetTempPath(), "playach-order-" + Guid.NewGuid().ToString("N") + ".db");
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
                        ApiName TEXT NOT NULL,
                        MetadataOrder INTEGER NULL);");

                    // Insertion order a..e; the source placed d, b and e.
                    db.ExecuteNonQuery("INSERT INTO AchievementDefinitions (GameId, ApiName, MetadataOrder) VALUES (1, 'a', NULL);");
                    db.ExecuteNonQuery("INSERT INTO AchievementDefinitions (GameId, ApiName, MetadataOrder) VALUES (1, 'b', 2);");
                    db.ExecuteNonQuery("INSERT INTO AchievementDefinitions (GameId, ApiName, MetadataOrder) VALUES (1, 'c', NULL);");
                    db.ExecuteNonQuery("INSERT INTO AchievementDefinitions (GameId, ApiName, MetadataOrder) VALUES (1, 'd', 0);");
                    db.ExecuteNonQuery("INSERT INTO AchievementDefinitions (GameId, ApiName, MetadataOrder) VALUES (1, 'e', 5);");
                    db.ExecuteNonQuery("INSERT INTO AchievementDefinitions (GameId, ApiName, MetadataOrder) VALUES (2, 'z', NULL);");

                    var names = db.Load<NameRow>(
                            "SELECT ad.ApiName AS ApiName FROM AchievementDefinitions ad WHERE ad.GameId = 1 ORDER BY " + orderSql + ";")
                        .Select(row => row.ApiName)
                        .ToArray();

                    CollectionAssert.AreEqual(new[] { "d", "b", "e", "a", "c" }, names);

                    db.ExecuteNonQuery("UPDATE AchievementDefinitions SET MetadataOrder = NULL;");
                    var unordered = db.Load<NameRow>(
                            "SELECT ad.ApiName AS ApiName FROM AchievementDefinitions ad WHERE ad.GameId = 1 ORDER BY " + orderSql + ";")
                        .Select(row => row.ApiName)
                        .ToArray();

                    CollectionAssert.AreEqual(new[] { "a", "b", "c", "d", "e" }, unordered, "with no source the platform's order stands");
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

        private static string ReadDefinitionOrderSql()
        {
            var store = File.ReadAllText(FindRepoFile("source", "Services", "Database", "SqlNadoCacheStore.cs"));
            var match = Regex.Match(store, "private const string DefinitionOrderSql = \"([^\"]+)\";");
            Assert.IsTrue(match.Success, "DefinitionOrderSql not found in SqlNadoCacheStore.cs");
            return match.Groups[1].Value;
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

            throw new FileNotFoundException(string.Join("/", parts));
        }
    }
}
