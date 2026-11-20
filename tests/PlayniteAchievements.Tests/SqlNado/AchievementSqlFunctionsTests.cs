using System;
using System.Globalization;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Achievements.Scoring;
using PlayniteAchievements.Services.Database;
using PlayniteAchievements.Services.Overview;
using SqlNado;

namespace PlayniteAchievements.SqlNado.Tests
{
    /// <summary>
    /// Runs the registered SQL functions and checks each returns what the C# rule it wraps returns.
    /// </summary>
    [TestClass]
    public class AchievementSqlFunctionsTests
    {
        private static readonly string[] Rarities = { null, "Common", "Uncommon", "Rare", "UltraRare", "ultrarare", "Mythic", "" };

        [TestMethod]
        public void Prestige_MatchesTheCalculator()
        {
            WithDatabase(db =>
            {
                foreach (var percent in new double?[] { null, 0.1, 4.9, 12.5, 50.0, 99.9, 150.0 })
                {
                    foreach (var rarity in Rarities)
                    {
                        var expected = AchievementScoreCalculator.GetPrestigeValue(percent, AchievementSqlFunctions.ParseStoredRarity(rarity));
                        var actual = db.ExecuteScalar<long>("SELECT pa_prestige(?, ?);", percent, rarity);
                        Assert.AreEqual(expected, actual, $"percent {percent}, rarity {rarity ?? "null"}");
                    }
                }

                Assert.AreEqual(
                    AchievementScoreCalculator.GetPrestigeValue(50.0, RarityTier.Common),
                    db.ExecuteScalar<long>("SELECT pa_prestige(50, 'Common');"),
                    "an integer percent reads as a number");
            });
        }

        [TestMethod]
        public void Collection_MatchesTheCalculator()
        {
            WithDatabase(db =>
            {
                foreach (var rarity in Rarities)
                {
                    var expected = AchievementScoreCalculator.GetCollectionValue(AchievementSqlFunctions.ParseStoredRarity(rarity));
                    Assert.AreEqual(expected, db.ExecuteScalar<long>("SELECT pa_collection(?);", rarity), rarity ?? "null");
                }

                Assert.AreEqual(AchievementScoreCalculator.GetCollectionValue(RarityTier.UltraRare), db.ExecuteScalar<long>("SELECT pa_collection('ultrarare');"));
                Assert.AreEqual(AchievementScoreCalculator.GetCollectionValue(RarityTier.Common), db.ExecuteScalar<long>("SELECT pa_collection('Mythic');"), "an unknown name is Common");
            });
        }

        [TestMethod]
        public void LocalDay_IsTheLocalCalendarDay_OrNull()
        {
            WithDatabase(db =>
            {
                Assert.AreEqual(1L, db.ExecuteScalar<long>("SELECT pa_localday(NULL) IS NULL;"));
                Assert.AreEqual(1L, db.ExecuteScalar<long>("SELECT pa_localday('') IS NULL;"));
                Assert.AreEqual(1L, db.ExecuteScalar<long>("SELECT pa_localday('not a date') IS NULL;"));

                // Thirty seconds either side of a local midnight land on different days.
                var midnight = new DateTime(2025, 3, 10, 0, 0, 0, DateTimeKind.Local).ToUniversalTime();
                Assert.AreEqual("2025-03-09", LocalDay(db, midnight.AddSeconds(-30).ToString("O", CultureInfo.InvariantCulture)));
                Assert.AreEqual("2025-03-10", LocalDay(db, midnight.AddSeconds(30).ToString("O", CultureInfo.InvariantCulture)));

                // A timestamp without an offset reads as UTC, as the cache loader reads it.
                const string offsetless = "2025-07-01T23:59:59";
                Assert.AreEqual(
                    Day(AchievementSqlFunctions.ParseUtc(offsetless).Value),
                    LocalDay(db, offsetless));
            });
        }

        // An explicit array: a lone string argument would bind to ExecuteScalar's default-value overload.
        private static string LocalDay(SQLiteDatabase db, string stored) =>
            db.ExecuteScalar<string>("SELECT pa_localday(?);", new object[] { stored });

        private static string Day(DateTime utc) =>
            UnlockDayCounts.DayOf(utc).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        private static void WithDatabase(Action<SQLiteDatabase> test)
        {
            using (var db = new SQLiteDatabase(":memory:"))
            {
                AchievementSqlFunctions.Register(db);
                test(db);
            }
        }
    }
}
