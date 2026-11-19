using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Achievements.Scoring;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.Cache;
using PlayniteAchievements.Services.Database;
using SqlNado;

namespace PlayniteAchievements.SqlNado.Tests
{
    /// <summary>
    /// The library summary read from the resolved rows, on a fixture with every override kind. The
    /// expected values are what the hydrated summary path produced for the same platform rows and
    /// custom data before it was replaced, except the decided changes, which each test names.
    /// </summary>
    [TestClass]
    public class SummaryCacheReaderTests
    {
        private const string Alpha = "aaaaaaaa-0000-0000-0000-000000000001";
        private const string Beta = "aaaaaaaa-0000-0000-0000-000000000002";
        private const string Retro = "aaaaaaaa-0000-0000-0000-000000000003";
        private const string Multi = "aaaaaaaa-0000-0000-0000-000000000004";
        private const string AuthoredOnly = "aaaaaaaa-0000-0000-0000-000000000005";
        private const string Gamma = "aaaaaaaa-0000-0000-0000-000000000006";

        // The hydrated path's game summaries, field by field.
        private static readonly Dictionary<string, string> Previous = new Dictionary<string, string>
        {
            [Alpha] = "AppId=100;CapstonesNotPlatinum=2;CapstoneTotal=2;CapstoneUnlocked=1;CollectionScore=330;CollectionScoreTotal=525;CommonCount=1;GameName=Alpha;HasAchievements=True;IsCompleted=False;LastUnlockUtc=2025-03-06T10:00:00.0000000Z;PlatformScorePoints=143;PlatformScorePointsTotal=176;PlatinumsNotCapstone=1;Points=143;PrestigeScore=547;PrestigeScoreTotal=834;ProviderGameKey=null;ProviderKey=Steam;ProviderPlatformKey=null;RareCount=1;TotalAchievements=7;TotalCommonPossible=2;TotalRarePossible=1;TotalUltraRarePossible=2;TotalUncommonPossible=1;TrophyBronzeCount=2;TrophyBronzeTotal=2;TrophyGoldCount=1;TrophyGoldTotal=2;TrophyPlatinumCount=1;TrophyPlatinumTotal=1;TrophySilverCount=0;TrophySilverTotal=0;UltraRareCount=1;UncommonCount=1;UnlockedAchievements=5",
            [Retro] = "AppId=300;CapstonesNotPlatinum=0;CapstoneTotal=0;CapstoneUnlocked=0;CollectionScore=105;CollectionScoreTotal=105;CommonCount=1;GameName=Retro;HasAchievements=True;IsCompleted=True;LastUnlockUtc=2025-05-02T12:00:00.0000000Z;PlatformScorePoints=7;PlatformScorePointsTotal=12;PlatinumsNotCapstone=0;Points=12;PrestigeScore=186;PrestigeScoreTotal=186;ProviderGameKey=null;ProviderKey=RetroAchievements;ProviderPlatformKey=null;RareCount=1;TotalAchievements=2;TotalCommonPossible=1;TotalRarePossible=1;TotalUltraRarePossible=0;TotalUncommonPossible=0;TrophyBronzeCount=0;TrophyBronzeTotal=0;TrophyGoldCount=0;TrophyGoldTotal=0;TrophyPlatinumCount=0;TrophyPlatinumTotal=0;TrophySilverCount=0;TrophySilverTotal=0;UltraRareCount=0;UncommonCount=0;UnlockedAchievements=2",
            [Multi] = "AppId=0;CapstonesNotPlatinum=0;CapstoneTotal=0;CapstoneUnlocked=0;CollectionScore=30;CollectionScoreTotal=30;CommonCount=0;GameName=Multi;HasAchievements=True;IsCompleted=True;LastUnlockUtc=2025-01-06T12:00:00.0000000Z;PlatformScorePoints=0;PlatformScorePointsTotal=0;PlatinumsNotCapstone=0;Points=0;PrestigeScore=32;PrestigeScoreTotal=32;ProviderGameKey=multi-exo;ProviderKey=Exophase;ProviderPlatformKey=null;RareCount=0;TotalAchievements=1;TotalCommonPossible=0;TotalRarePossible=0;TotalUltraRarePossible=0;TotalUncommonPossible=1;TrophyBronzeCount=0;TrophyBronzeTotal=0;TrophyGoldCount=0;TrophyGoldTotal=0;TrophyPlatinumCount=0;TrophyPlatinumTotal=0;TrophySilverCount=0;TrophySilverTotal=0;UltraRareCount=0;UncommonCount=1;UnlockedAchievements=1",
            [AuthoredOnly] = "AppId=0;CapstonesNotPlatinum=0;CapstoneTotal=0;CapstoneUnlocked=0;CollectionScore=90;CollectionScoreTotal=90;CommonCount=0;GameName=null;HasAchievements=True;IsCompleted=True;LastUnlockUtc=2025-02-01T10:00:00.0000000Z;PlatformScorePoints=0;PlatformScorePointsTotal=0;PlatinumsNotCapstone=0;Points=0;PrestigeScore=170;PrestigeScoreTotal=170;ProviderGameKey=null;ProviderKey=Custom;ProviderPlatformKey=null;RareCount=1;TotalAchievements=1;TotalCommonPossible=0;TotalRarePossible=1;TotalUltraRarePossible=0;TotalUncommonPossible=0;TrophyBronzeCount=0;TrophyBronzeTotal=0;TrophyGoldCount=0;TrophyGoldTotal=0;TrophyPlatinumCount=0;TrophyPlatinumTotal=0;TrophySilverCount=0;TrophySilverTotal=0;UltraRareCount=0;UncommonCount=0;UnlockedAchievements=1",
            [Gamma] = "AppId=700;CapstonesNotPlatinum=1;CapstoneTotal=1;CapstoneUnlocked=0;CollectionScore=90;CollectionScoreTotal=105;CommonCount=0;GameName=Gamma;HasAchievements=True;IsCompleted=False;LastUnlockUtc=2025-08-01T12:00:00.0000000Z;PlatformScorePoints=10;PlatformScorePointsTotal=20;PlatinumsNotCapstone=1;Points=10;PrestigeScore=210;PrestigeScoreTotal=220;ProviderGameKey=null;ProviderKey=Steam;ProviderPlatformKey=null;RareCount=1;TotalAchievements=2;TotalCommonPossible=1;TotalRarePossible=1;TotalUltraRarePossible=0;TotalUncommonPossible=0;TrophyBronzeCount=0;TrophyBronzeTotal=0;TrophyGoldCount=0;TrophyGoldTotal=1;TrophyPlatinumCount=1;TrophyPlatinumTotal=1;TrophySilverCount=0;TrophySilverTotal=0;UltraRareCount=0;UncommonCount=0;UnlockedAchievements=1"
        };

        [TestMethod]
        public void GameSummaries_MatchTheHydratedPath_ExceptTheDecidedChanges()
        {
            WithFixture(db =>
            {
                var games = Load(db).Games.ToDictionary(game => game.PlayniteGameId.Value.ToString("D"));
                CollectionAssert.AreEquivalent(Previous.Keys.ToList(), games.Keys.ToList(), "Beta is excluded from summaries");

                var expected = Previous.ToDictionary(pair => pair.Key, pair => Fields(pair.Value));

                // Unknown rarity (a7's "Weird") counts as Common; the hydrated path left it out of
                // the tiers but scored it as Common.
                expected[Alpha]["CommonCount"] = "2";
                expected[Alpha]["TotalCommonPossible"] = "3";

                // Disagreement: the hydrated path kept the stored last unlock (raised only by the
                // authored unlock, 2025-03-06); the resolved rows follow a1's unlock-time override.
                expected[Alpha]["LastUnlockUtc"] = "2025-03-10T23:45:00.0000000Z";

                // Every current-user account counts: Multi's Steam account adds m1 (unlocked, Common)
                // and m2 (locked, Rare) beside the Exophase x1.
                foreach (var pair in new Dictionary<string, string>
                         {
                             ["TotalAchievements"] = "3", ["UnlockedAchievements"] = "2", ["IsCompleted"] = "False",
                             ["CommonCount"] = "1", ["TotalCommonPossible"] = "1", ["TotalRarePossible"] = "1",
                             ["CollectionScore"] = "45", ["CollectionScoreTotal"] = "135", ["PrestigeScore"] = "42", ["PrestigeScoreTotal"] = "212"
                         })
                {
                    expected[Multi][pair.Key] = pair.Value;
                }

                foreach (var game in games)
                {
                    var actual = Fields(Describe(game.Value));
                    foreach (var field in expected[game.Key])
                    {
                        Assert.AreEqual(field.Value, actual[field.Key], game.Value.GameName + " " + field.Key);
                    }
                }
            });
        }

        [TestMethod]
        public void Tallies_CarryTagEditsCapstoneSetsAndAuthoredAchievements()
        {
            WithFixture(db =>
            {
                var games = Load(db).Games.ToDictionary(game => game.PlayniteGameId.Value.ToString("D"));

                // As the hydrated path counted them, with a7 under Common rather than no tier: a6
                // retagged to Progression, a3's tags cleared and its trophy edited to gold, the
                // authored one tagged Collectable, and the capstone set a2 and custom:two.
                Assert.AreEqual(
                    "Collectable|UltraRare|platinum|False|1/1; Progression|Uncommon|bronze|False|1/1; |Common|bronze|False|1/1; " +
                    "|Common||False|1/1; |Common||True|0/1; |Rare|gold|True|1/1; |UltraRare|gold|False|0/1",
                    Describe(games[Alpha].Tallies));
                // Gamma's set replaces the provider's capstone g1 with g2.
                Assert.AreEqual("|Common|gold|True|0/1; |Rare|platinum|False|1/1", Describe(games[Gamma].Tallies));
                Assert.AreEqual("|Rare||False|1/1", Describe(games[AuthoredOnly].Tallies));
            });
        }

        [TestMethod]
        public void DayCounts_FollowUnlockTimeOverrides_AndSkipAManuallyLinkedGames()
        {
            WithFixture(db =>
            {
                var summary = Load(db);
                var days = summary.UnlockCountsByDateByGame
                    .SelectMany(game => game.Value.Select(day => game.Key.ToString("D").Substring(35) + " " + day.Key.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " " + day.Value))
                    .OrderBy(text => text)
                    .ToList();

                // The hydrated path's counts plus Multi's Steam unlock (2025-01-05). a1 moved to its
                // override's day, a2's cleared time has none, Retro's override is skipped for its
                // manual link, and filtered and summary-filtered unlocks do not count.
                CollectionAssert.AreEqual(
                    new[]
                    {
                        "1 2025-03-04", "1 2025-03-06", "1 2025-03-10", "3 2025-05-01", "3 2025-05-02",
                        "4 2025-01-05", "4 2025-01-06", "5 2025-02-01", "6 2025-08-01"
                    }.Select(text => text + " 1").ToList(),
                    days);
                Assert.AreEqual(9, summary.GlobalUnlockCountsByDate.Values.Sum());
            });
        }

        [TestMethod]
        public void Unlocks_GroupTheUnlockedRows_AsTheRowBasedViewsCountedThem()
        {
            WithFixture(db =>
            {
                var summary = Load(db);
                var rows = AchievementReadService.Rows(db, AchievementQuery.CurrentUser.WithSummaryRules().WhereUnlocked(true));
                RarityTier Tier(AchievementRowRecord row) => AchievementSqlFunctions.ParseStoredRarity(row.Rarity);
                DateTime? Day(AchievementRowRecord row) =>
                    row.UnlockTimeUtc == null ? (DateTime?)null : PlayniteAchievements.Services.Overview.UnlockDayCounts.DayOf(AchievementSqlFunctions.ParseUtc(row.UnlockTimeUtc).Value);

                // Every unlock under its game, day, tags, tier, trophy and capstone flag, as the
                // filtered views and the tier series read them off the rows.
                CollectionAssert.AreEquivalent(
                    rows.Select(row => row.PlayniteGameId + "|" + Day(row) + "|" + row.Tags + "|" + Tier(row) + "|" +
                                       AchievementTally.NormalizeTrophy(row.TrophyType) + "|" + (row.IsCapstone != 0)).ToList(),
                    summary.Unlocks.SelectMany(fact => Enumerable.Repeat(
                        fact.GameId.ToString("D") + "|" + fact.Day + "|" + (fact.Tally.Key.Length == 0 ? null : fact.Tally.Key) + "|" +
                        fact.Tally.Rarity + "|" + fact.Tally.Trophy + "|" + fact.Tally.IsCapstone, fact.Count)).ToList());

                foreach (var game in rows.GroupBy(row => Guid.Parse(row.PlayniteGameId)))
                {
                    var facts = summary.Unlocks.Where(fact => fact.GameId == game.Key).ToList();
                    var name = game.First().GameName;

                    // Score history: each unlock's collection, prestige and platform points (softcore left out).
                    Assert.AreEqual(game.Sum(row => AchievementScoreCalculator.GetCollectionValue(Tier(row))), facts.Sum(fact => fact.CollectionScore), name);
                    Assert.AreEqual(game.Sum(row => AchievementScoreCalculator.GetPrestigeValue(row.GlobalPercentUnlocked, Tier(row))), facts.Sum(fact => fact.PrestigeScore), name);
                    Assert.AreEqual(
                        game.Where(row => row.Points > 0 && !AchievementTagHelper.IsSoftcore(row.Tags)).Sum(row => row.Points.Value),
                        facts.Sum(fact => fact.PlatformScorePoints),
                        name);

                    // Statistics: the average global percent of the unlocks.
                    Assert.AreEqual(game.Where(row => row.GlobalPercentUnlocked.HasValue).Sum(row => row.GlobalPercentUnlocked.Value), facts.Sum(fact => fact.PercentSum), 1e-9, name);
                    Assert.AreEqual(game.Count(row => row.GlobalPercentUnlocked.HasValue), facts.Sum(fact => fact.PercentCount), name);

                    // Game mosaic: the rarest positive percent, among capstones, among base-game capstones.
                    double? Rarest(IEnumerable<AchievementRowRecord> candidates) =>
                        candidates.Where(row => row.GlobalPercentUnlocked > 0).Select(row => row.GlobalPercentUnlocked).DefaultIfEmpty().Min();
                    Assert.AreEqual(Rarest(game), facts.Select(fact => fact.MinPercent).Where(value => value.HasValue).DefaultIfEmpty().Min(), name);
                    Assert.AreEqual(
                        Rarest(game.Where(row => row.IsCapstone != 0)),
                        facts.Where(fact => fact.Tally.IsCapstone).Select(fact => fact.MinPercent).Where(value => value.HasValue).DefaultIfEmpty().Min(),
                        name);
                    Assert.AreEqual(
                        Rarest(game.Where(row => row.IsCapstone != 0 && CategoryKindHelper.IsBase(row.CategoryKind))),
                        facts.Select(fact => fact.MinBasePercent).Where(value => value.HasValue).DefaultIfEmpty().Min(),
                        name);

                    // Completions: the last capstone and the first platinum unlock.
                    var summaryGame = summary.Games.Single(item => item.PlayniteGameId == game.Key);
                    Assert.AreEqual(
                        game.Where(row => row.IsCapstone != 0 && row.UnlockTimeUtc != null).Select(row => AchievementSqlFunctions.ParseUtc(row.UnlockTimeUtc)).DefaultIfEmpty().Max(),
                        summaryGame.LastCapstoneUnlockUtc,
                        name);
                    Assert.AreEqual(
                        game.Where(row => AchievementTally.NormalizeTrophy(row.TrophyType) == "platinum" && row.UnlockTimeUtc != null)
                            .Select(row => AchievementSqlFunctions.ParseUtc(row.UnlockTimeUtc)).DefaultIfEmpty().Min(),
                        summaryGame.FirstPlatinumUnlockUtc,
                        name);
                }

                Assert.IsNotNull(summary.Games.Single(game => game.PlayniteGameId == Guid.Parse(Gamma)).FirstPlatinumUnlockUtc);
            });
        }

        [TestMethod]
        public void Rows_CarryTheResolvedValues_AndRecentUnlocksLeaveOutClearedTimes()
        {
            WithFixture(db =>
            {
                var summary = Load(db, limit: 20);
                var games = summary.Games.ToDictionary(game => game.PlayniteGameId.Value);
                var rows = AchievementReadService.Rows(db, AchievementQuery.CurrentUser.WithSummaryRules().WhereUnlocked(true))
                    .Select(row => SummaryCacheReader.MapRow(row, games, path => path))
                    .ToDictionary(row => row.ApiName);

                CollectionAssert.AreEquivalent(
                    new[] { "a1", "a2", "a6", "a7", "custom:one", "r1", "r2", "m1", "x1", "custom:solo", "g1" },
                    rows.Keys.ToList(),
                    "every unlocked row of the summary, both of Multi's accounts included");
                Assert.AreEqual(99, rows["a1"].Points);
                Assert.AreEqual("remember", rows["a1"].AchievementNote);
                Assert.AreEqual(new DateTime(2025, 3, 10, 23, 45, 0, DateTimeKind.Utc), rows["a1"].UnlockTimeUtc);
                Assert.IsNull(rows["a2"].UnlockTimeUtc);
                Assert.IsTrue(rows["a2"].IsCapstone);
                Assert.AreEqual("Progression", rows["a6"].Tags);
                Assert.AreEqual("Missable", rows["a7"].CategoryKind);
                Assert.AreEqual(RarityTier.Common, rows["a7"].Rarity);
                Assert.AreEqual(new DateTime(2025, 5, 2, 12, 0, 0, DateTimeKind.Utc), rows["r2"].UnlockTimeUtc, "manual link skips the override");
                Assert.AreEqual("mine", rows["custom:one"].AchievementNote);
                Assert.AreEqual("Steam", rows["custom:one"].ProviderKey, "an authored row takes its game's provider");
                Assert.AreEqual(100, rows["custom:one"].AppId);
                Assert.AreEqual("Custom", rows["custom:solo"].ProviderKey);
                // Disagreement: the hydrated path left the provider category of authored rows null.
                Assert.AreEqual("Default", rows["custom:solo"].ProviderCategory);

                // Disagreement: the hydrated path kept a2 in the recent unlocks with its time cleared;
                // the resolved rows know it has none.
                CollectionAssert.AreEquivalent(
                    rows.Keys.Except(new[] { "a2", "a7" }).ToList(),
                    summary.RecentUnlocks.Select(row => row.ApiName).ToList());
                CollectionAssert.AreEqual(
                    summary.RecentUnlocks.OrderByDescending(row => row.UnlockTimeUtc).Select(row => row.UnlockTimeUtc).ToList(),
                    summary.RecentUnlocks.Select(row => row.UnlockTimeUtc).ToList(),
                    "newest first");
            });
        }

        [TestMethod]
        public void BoundedRead_TakesTheNewestDatedUnlocks()
        {
            WithFixture(db =>
            {
                var full = Load(db, limit: 20);
                var bounded = Load(db, limit: 3);

                Assert.AreEqual(0, Load(db).RecentUnlocks.Count);
                CollectionAssert.AreEqual(
                    full.RecentUnlocks.Take(3).Select(row => row.ApiName).ToList(),
                    bounded.RecentUnlocks.Select(row => row.ApiName).ToList());
                Assert.AreEqual(Describe(full.Games[0]), Describe(bounded.Games[0]), "the games do not depend on the bound");
            });
        }

        [TestMethod]
        public void ExcludedGames_LeaveEveryPartOfTheSummary()
        {
            WithFixture(db =>
            {
                var summary = Load(db, excluded: new[] { Guid.Parse(Alpha) });

                Assert.IsFalse(summary.Games.Any(game => game.PlayniteGameId == Guid.Parse(Alpha)));
                Assert.IsFalse(summary.Unlocks.Any(fact => fact.GameId == Guid.Parse(Alpha)));
                Assert.IsFalse(summary.UnlockCountsByDateByGame.ContainsKey(Guid.Parse(Alpha)));
                Assert.AreEqual(6, summary.GlobalUnlockCountsByDate.Values.Sum());
            });
        }

        [TestMethod]
        public void AGameWhoseProgressHasNoAchievements_IsNotShown_UnlessTheUserAuthoredSome()
        {
            WithFixture(db =>
            {
                db.ExecuteNonQuery("UPDATE UserGameProgress SET HasAchievements = 0 WHERE Id IN (1, 6);");
                var games = Load(db).Games.ToDictionary(game => game.PlayniteGameId.Value.ToString("D"));

                Assert.IsTrue(games[Alpha].HasAchievements, "Alpha has authored achievements");
                Assert.IsFalse(games[Gamma].HasAchievements);
            });
        }

        private static CachedSummaryData Load(SQLiteDatabase db, int limit = 0, IEnumerable<Guid> excluded = null)
        {
            var identities = db.Load<IdentityRow>(
                @"SELECT ugp.CacheKey AS CacheKey, g.PlayniteGameId AS PlayniteGameId, g.ProviderKey AS ProviderKey,
                         COALESCE(g.ProviderGameId, 0) AS AppId, g.ProviderGameKey AS ProviderGameKey, g.GameName AS GameName,
                         ugp.HasAchievements AS HasAchievements, ugp.LastUpdatedUtc AS LastUpdatedUtc
                  FROM UserGameProgress ugp
                  INNER JOIN Users u ON u.Id = ugp.UserId AND u.IsCurrentUser = 1
                  INNER JOIN Games g ON g.Id = ugp.GameId;")
                .Select(row => new CachedGameSummaryData
                {
                    CacheKey = row.CacheKey,
                    PlayniteGameId = Guid.Parse(row.PlayniteGameId),
                    ProviderKey = row.ProviderKey,
                    AppId = (int)row.AppId,
                    ProviderGameKey = row.ProviderGameKey,
                    GameName = row.GameName,
                    HasAchievements = row.HasAchievements != 0,
                    LastUpdatedUtc = DateTime.Parse(row.LastUpdatedUtc, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal)
                })
                .ToList();
            return SummaryCacheReader.Load(
                db,
                identities,
                id => new CachedGameSummaryData { ProviderKey = "Custom" },
                limit,
                excluded,
                path => path);
        }

        private sealed class IdentityRow
        {
            public string CacheKey { get; set; }
            public string PlayniteGameId { get; set; }
            public string ProviderKey { get; set; }
            public long AppId { get; set; }
            public string ProviderGameKey { get; set; }
            public string GameName { get; set; }
            public long HasAchievements { get; set; }
            public string LastUpdatedUtc { get; set; }
        }

        private static Dictionary<string, string> Fields(string described) =>
            described.Split(';').Select(part => part.Split(new[] { '=' }, 2)).ToDictionary(part => part[0], part => part[1]);

        private static string Describe(CachedGameSummaryData game) =>
            string.Join(";", typeof(CachedGameSummaryData).GetProperties()
                .Where(property => property.PropertyType.IsValueType || property.PropertyType == typeof(string))
                .OrderBy(property => property.Name, StringComparer.Ordinal)
                .Select(property => property.Name + "=" + Text(property.GetValue(game))));

        private static string Text(object value) =>
            value == null ? "null"
            : value is DateTime time ? time.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture)
            : Convert.ToString(value, CultureInfo.InvariantCulture);

        private static string Describe(IEnumerable<AchievementTally> tallies) =>
            string.Join("; ", (tallies ?? Enumerable.Empty<AchievementTally>())
                .Select(tally => tally.Key + "|" + tally.Rarity + "|" + tally.Trophy + "|" + tally.IsCapstone + "|" + tally.Unlocked + "/" + tally.Total)
                .OrderBy(text => text, StringComparer.Ordinal));

        /// <summary>
        /// Platform rows for Alpha (a1-a7, two authored), Beta (excluded from summaries), Retro
        /// (softcore and hardcore unlocks, manually linked), Multi (a Steam and an Exophase account),
        /// a game with only an authored achievement, and Gamma (its capstone set swaps g1 for g2);
        /// the custom layer as the plugin mirrors it from the custom data.
        /// </summary>
        private static void Seed(SQLiteDatabase db)
        {
            void Exec(string sql, params object[] args) => db.ExecuteNonQuery(sql, args);
            const string Stamp = "2025-01-01T00:00:00.0000000Z";

            Exec(@"INSERT INTO Users (Id, ProviderKey, ExternalUserId, DisplayName, IsCurrentUser, IsActiveFriend, FriendSource, CreatedUtc, UpdatedUtc) VALUES
                (1, 'Steam', 'me', 'Me', 1, 1, NULL, ?1, ?1), (2, 'Exophase', 'me-exo', 'Me', 1, 1, NULL, ?1, ?1),
                (10, 'Steam', 'a', 'Anna', 0, 1, 'SteamFriends', ?1, ?1);", Stamp);
            Exec(@"INSERT INTO Games (Id, ProviderKey, ProviderGameId, ProviderGameKey, PlayniteGameId, GameName, RaritySource, FirstSeenUtc, LastUpdatedUtc) VALUES
                (1, 'Steam', 100, NULL, ?1, 'Alpha', 'Percent', ?6, ?6), (2, 'Steam', 200, NULL, ?2, 'Beta', 'Percent', ?6, ?6),
                (3, 'RetroAchievements', 300, NULL, ?3, 'Retro', 'Percent', ?6, ?6),
                (5, 'Steam', 500, NULL, ?4, 'Multi', 'Percent', ?6, ?6), (6, 'Exophase', NULL, 'multi-exo', ?4, 'Multi', 'Percent', ?6, ?6),
                (7, 'Steam', 700, NULL, ?5, 'Gamma', 'Percent', ?6, ?6);",
                Alpha, Beta, Retro, Multi, Gamma, Stamp);
            Exec(@"INSERT INTO UserGameProgress (Id, UserId, GameId, CacheKey, HasAchievements, LastUpdatedUtc, CreatedUtc, UpdatedUtc) VALUES
                (1, 1, 1, ?1, 1, '2025-06-01T00:00:00Z', ?6, ?6), (2, 1, 2, ?2, 1, '2025-06-01T00:00:00Z', ?6, ?6),
                (3, 1, 3, ?3, 1, '2025-06-01T00:00:00Z', ?6, ?6), (4, 1, 5, ?4, 1, '2025-06-01T00:00:00Z', ?6, ?6),
                (5, 2, 6, ?4, 1, '2025-07-01T00:00:00Z', ?6, ?6), (6, 1, 7, ?5, 1, '2025-06-01T00:00:00Z', ?6, ?6),
                (10, 10, 1, 'f-a-1', 1, ?6, ?6, ?6);",
                Alpha, Beta, Retro, Multi, Gamma, Stamp);
            Exec(@"INSERT INTO PlatformDefinitions (Id, GameId, ApiName, DisplayName, Description, Points, TrophyType, Tags, GlobalPercentUnlocked, Rarity, IsCapstone, Category, MetadataOrder, CreatedUtc, UpdatedUtc) VALUES
                (1, 1, 'a1', 'Sword Master', 'Swing', 10, 'bronze', NULL, 50.0, 'Common', 0, 'Default', 1, ?1, ?1),
                (2, 1, 'a2', 'Gold Rush', 'Dig', 20, 'gold', NULL, 8.0, 'Rare', 0, 'Default', 2, ?1, ?1),
                (3, 1, 'a3', 'Finale', NULL, 30, 'platinum', NULL, 1.0, 'UltraRare', 1, 'Default', 3, ?1, ?1),
                (4, 1, 'a4', 'Hidden Path', NULL, 5, 'silver', NULL, 30.0, 'Uncommon', 0, 'Default', 4, ?1, ?1),
                (5, 1, 'a5', 'Side Quest', NULL, 5, NULL, 'Missable', 60.0, 'Common', 0, 'Default', 5, ?1, ?1),
                (6, 1, 'a6', 'Collector', NULL, 5, 'bronze', 'Collectable|Missable', 20.0, 'Uncommon', 0, 'Default', 6, ?1, ?1),
                (7, 1, 'a7', 'Odd Tier', NULL, 4, NULL, NULL, NULL, 'Weird', 0, 'Story', 7, ?1, ?1),
                (21, 2, 'b1', 'Beta One', NULL, 10, NULL, NULL, 70.0, 'Common', 0, 'Default', NULL, ?1, ?1),
                (31, 3, 'r1', 'Soft', NULL, 5, NULL, 'Softcore', 9.0, 'Rare', 0, 'Default', NULL, ?1, ?1),
                (32, 3, 'r2', 'Hard', NULL, 7, NULL, 'Hardcore', 40.0, 'Common', 0, 'Default', NULL, ?1, ?1),
                (51, 5, 'm1', 'Multi One', NULL, NULL, NULL, NULL, 50.0, 'Common', 0, 'Default', NULL, ?1, ?1),
                (52, 5, 'm2', 'Multi Two', NULL, NULL, NULL, NULL, 9.0, 'Rare', 0, 'Default', NULL, ?1, ?1),
                (61, 6, 'x1', 'Multi Exo', NULL, NULL, NULL, NULL, 30.0, 'Uncommon', 0, 'Default', NULL, ?1, ?1),
                (71, 7, 'g1', 'Gamma One', NULL, 10, 'platinum', NULL, 5.0, 'Rare', 1, 'Default', NULL, ?1, ?1),
                (72, 7, 'g2', 'Gamma Two', NULL, 10, 'gold', NULL, 50.0, 'Common', 0, 'Default', NULL, ?1, ?1);", Stamp);
            Exec(@"INSERT INTO UserAchievements (UserGameProgressId, AchievementDefinitionId, Unlocked, UnlockTimeUtc, LastUpdatedUtc, CreatedUtc) VALUES
                (1, 1, 1, '2025-03-01T23:30:00.0000000Z', ?1, ?1), (1, 2, 1, '2025-03-02T10:00:00.0000000Z', ?1, ?1), (1, 3, 0, NULL, ?1, ?1),
                (1, 4, 1, '2025-03-03T10:00:00.0000000Z', ?1, ?1), (1, 5, 1, '2025-03-04T10:00:00.0000000Z', ?1, ?1),
                (1, 6, 1, '2025-03-05T00:30:00.0000000Z', ?1, ?1), (1, 7, 1, NULL, ?1, ?1),
                (2, 21, 1, '2025-04-01T10:00:00.0000000Z', ?1, ?1),
                (3, 31, 1, '2025-05-01T12:00:00.0000000Z', ?1, ?1), (3, 32, 1, '2025-05-02T12:00:00.0000000Z', ?1, ?1),
                (4, 51, 1, '2025-01-05T12:00:00.0000000Z', ?1, ?1), (5, 61, 1, '2025-01-06T12:00:00.0000000Z', ?1, ?1),
                (6, 71, 1, '2025-08-01T12:00:00.0000000Z', ?1, ?1), (6, 72, 0, NULL, ?1, ?1),
                (10, 1, 1, '2024-01-01T08:00:00.0000000Z', ?1, ?1);", Stamp);

            // The custom layer: a1's points, note and unlock time; a2's cleared time; a3's cleared
            // tags and trophy edit; a6 retagged; a4 filtered and a5 filtered from summaries; the
            // capstone sets; two authored achievements on Alpha and one on a game of its own.
            Exec(@"INSERT INTO CustomLayerAchievements (PlayniteGameId, ApiName, Points, TrophyType, Tags, Cleared, Note, UnlockTimeUtc, CapstoneOrder, IsAuthored, Unlocked, IsCapstone, FilterOrder, SummaryFilterOrder, Rarity, GlobalPercentUnlocked, DisplayName, UpdatedUtc) VALUES
                (?1, 'a1', 99, NULL, NULL, 0, 'remember', '2025-03-10T23:45:00.0000000Z', NULL, 0, 0, 0, NULL, NULL, NULL, NULL, NULL, ?4),
                (?1, 'a2', NULL, NULL, NULL, ?5, NULL, NULL, 0, 0, 0, 0, NULL, NULL, NULL, NULL, NULL, ?4),
                (?1, 'a3', NULL, 'gold', NULL, ?6, NULL, NULL, NULL, 0, 0, 0, NULL, NULL, NULL, NULL, NULL, ?4),
                (?1, 'a4', NULL, NULL, NULL, 0, NULL, NULL, NULL, 0, 0, 0, 0, NULL, NULL, NULL, NULL, ?4),
                (?1, 'a5', NULL, NULL, NULL, 0, NULL, NULL, NULL, 0, 0, 0, NULL, 0, NULL, NULL, NULL, ?4),
                (?1, 'a6', NULL, NULL, 'Progression', 0, NULL, NULL, NULL, 0, 0, 0, NULL, NULL, NULL, NULL, NULL, ?4),
                (?1, 'custom:one', 15, 'platinum', 'Collectable', 0, NULL, '2025-03-06T10:00:00.0000000Z', NULL, 1, 1, 0, NULL, NULL, 'UltraRare', 2.0, 'Epilogue', ?4),
                (?1, 'custom:one', NULL, NULL, NULL, 0, 'mine', NULL, NULL, 0, 0, 0, NULL, NULL, NULL, NULL, NULL, ?4),
                (?1, 'custom:two', 3, NULL, NULL, 0, NULL, NULL, NULL, 1, 0, 1, NULL, NULL, 'Common', NULL, 'Later', ?4),
                (?1, 'custom:two', NULL, NULL, NULL, 0, NULL, NULL, 1, 0, 0, 0, NULL, NULL, NULL, NULL, NULL, ?4),
                (?2, 'custom:solo', NULL, NULL, NULL, 0, NULL, '2025-02-01T10:00:00.0000000Z', NULL, 1, 1, 0, NULL, NULL, 'Rare', 9.0, 'Solo', ?4),
                (?3, 'g2', NULL, NULL, NULL, 0, NULL, NULL, 0, 0, 0, 0, NULL, NULL, NULL, NULL, NULL, ?4);",
                Alpha, AuthoredOnly, Gamma, Stamp, (long)AchievementField.UnlockTime, (long)AchievementField.Tags);
            Exec(@"INSERT INTO CustomLayerGames (PlayniteGameId, CapstonesMaterialized, HasManualLink, ExcludedFromSummaries) VALUES
                (?1, 1, 0, 0), (?2, 0, 0, 1), (?3, 0, 1, 0), (?4, 1, 0, 0);", Alpha, Beta, Retro, Gamma);
            Exec("INSERT INTO CustomLayerCategories (PlayniteGameId, Category, Kind) VALUES (?1, 'Story', 'Missable');", Alpha);
        }

        private static void WithFixture(Action<SQLiteDatabase> test)
        {
            var dir = Path.Combine(Path.GetTempPath(), "playach-summary-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, SqlNadoSchemaManager.DatabaseFileName);
            try
            {
                using (var db = new SQLiteDatabase(
                    path,
                    SQLiteOpenOptions.SQLITE_OPEN_READWRITE | SQLiteOpenOptions.SQLITE_OPEN_CREATE | SQLiteOpenOptions.SQLITE_OPEN_FULLMUTEX))
                {
                    new SqlNadoSchemaManager(null, path, dir).EnsureSchema(db);
                    AchievementSqlFunctions.Register(db);
                    Seed(db);
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
