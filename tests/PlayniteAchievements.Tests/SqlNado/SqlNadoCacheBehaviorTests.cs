using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Friends;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.Database;
using PlayniteAchievements.Services.GameCustomData;
using PlayniteAchievements.Services.Refresh;
using SqlNado;

namespace PlayniteAchievements.SqlNado.Tests
{
    [TestClass]
    public class SqlNadoCacheBehaviorTests
    {
        [TestMethod]
        public void ExecuteScalar_WithNumericSqlParameters_UsesObjectArray()
        {
            var path = Path.Combine(Path.GetTempPath(), "playach-sqlnado-" + Guid.NewGuid().ToString("N") + ".db");
            try
            {
                using (var db = new SQLiteDatabase(
                    path,
                    SQLiteOpenOptions.SQLITE_OPEN_READWRITE |
                    SQLiteOpenOptions.SQLITE_OPEN_CREATE |
                    SQLiteOpenOptions.SQLITE_OPEN_FULLMUTEX))
                {
                    db.ExecuteNonQuery(
                        @"CREATE TABLE Probe (
                            Id INTEGER PRIMARY KEY AUTOINCREMENT,
                            UserId INTEGER NOT NULL,
                            GameId INTEGER NOT NULL
                          );");
                    db.ExecuteNonQuery(
                        "INSERT INTO Probe (UserId, GameId) VALUES (?, ?);",
                        10L,
                        20L);

                    var count = db.ExecuteScalar<long>(
                        "SELECT COUNT(*) FROM Probe WHERE UserId = ? AND GameId = ?;",
                        new object[] { 10L, 20L });
                    var id = db.ExecuteScalar<long>(
                        "SELECT Id FROM Probe WHERE UserId = ? AND GameId = ? LIMIT 1;",
                        new object[] { 10L, 20L });

                    Assert.AreEqual(1L, count);
                    Assert.AreEqual(1L, id);
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

        [TestMethod]
        public void ReadOnlyConnection_ReadsCommittedWritesConcurrentlyUnderWal()
        {
            // Validates the assumption behind the store's dedicated read connection: a second
            // read-only connection, opened while a read-write connection is live, can read the
            // database under WAL and sees writes the read-write connection commits afterward.
            var path = Path.Combine(Path.GetTempPath(), "playach-sqlnado-ro-" + Guid.NewGuid().ToString("N") + ".db");
            try
            {
                using (var rw = new SQLiteDatabase(
                    path,
                    SQLiteOpenOptions.SQLITE_OPEN_READWRITE |
                    SQLiteOpenOptions.SQLITE_OPEN_CREATE |
                    SQLiteOpenOptions.SQLITE_OPEN_FULLMUTEX))
                {
                    rw.ExecuteNonQuery("PRAGMA journal_mode = WAL;");
                    rw.ExecuteNonQuery(
                        @"CREATE TABLE Probe (
                            Id INTEGER PRIMARY KEY AUTOINCREMENT,
                            Value INTEGER NOT NULL
                          );");
                    rw.ExecuteNonQuery("INSERT INTO Probe (Value) VALUES (?);", 1L);

                    using (var ro = new SQLiteDatabase(
                        path,
                        SQLiteOpenOptions.SQLITE_OPEN_READONLY |
                        SQLiteOpenOptions.SQLITE_OPEN_FULLMUTEX))
                    {
                        ro.BusyTimeout = 5000;

                        Assert.AreEqual(1L, ro.ExecuteScalar<long>("SELECT COUNT(*) FROM Probe;"));

                        // A commit on the read-write connection is visible to the read-only
                        // connection's next read.
                        rw.ExecuteNonQuery("INSERT INTO Probe (Value) VALUES (?);", 2L);
                        Assert.AreEqual(2L, ro.ExecuteScalar<long>("SELECT COUNT(*) FROM Probe;"));

                        // A read-only connection cannot write: the insert is rejected and the
                        // row count is unchanged.
                        var writeRejected = false;
                        try
                        {
                            ro.ExecuteNonQuery("INSERT INTO Probe (Value) VALUES (?);", 3L);
                        }
                        catch
                        {
                            writeRejected = true;
                        }

                        Assert.IsTrue(writeRejected, "Read-only connection should reject writes.");
                        Assert.AreEqual(2L, ro.ExecuteScalar<long>("SELECT COUNT(*) FROM Probe;"));
                    }
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

        [TestMethod]
        public void ComputeStaleDefinitionIds_ReturnsMissingOnly_CaseInsensitive()
        {
            var existing = new Dictionary<string, long>
            {
                ["Alpha"] = 1,
                ["Bravo"] = 2,
                ["Charlie"] = 3
            };

            var incoming = new[] { "alpha", " CHARLIE " };

            var stale = SqlNadoCacheBehavior.ComputeStaleDefinitionIds(existing, incoming);

            Assert.AreEqual(1, stale.Count);
            CollectionAssert.Contains(stale, 2L);
        }

        [TestMethod]
        public void ComputeStaleDefinitionIds_EmptyIncoming_DeletesAllValidIds()
        {
            var existing = new Dictionary<string, long>
            {
                ["A"] = 10,
                ["B"] = 11
            };

            var stale = SqlNadoCacheBehavior.ComputeStaleDefinitionIds(existing, Array.Empty<string>());

            Assert.AreEqual(2, stale.Count);
            CollectionAssert.Contains(stale, 10L);
            CollectionAssert.Contains(stale, 11L);
        }

        [TestMethod]
        public void ComputeStaleDefinitionIds_NullIncoming_DeletesAllValidIds()
        {
            var existing = new Dictionary<string, long>
            {
                ["A"] = 10,
                ["B"] = 11
            };

            var stale = SqlNadoCacheBehavior.ComputeStaleDefinitionIds(existing, null);

            Assert.AreEqual(2, stale.Count);
            CollectionAssert.Contains(stale, 10L);
            CollectionAssert.Contains(stale, 11L);
        }

        [TestMethod]
        public void ComputeStaleDefinitionIds_IgnoresBlankNamesAndNonPositiveIds()
        {
            var existing = new Dictionary<string, long>
            {
                ["  "] = 1,
                ["Real"] = 0,
                ["Other"] = -5,
                ["Keep"] = 9
            };

            var incoming = new[] { "keep" };

            var stale = SqlNadoCacheBehavior.ComputeStaleDefinitionIds(existing, incoming);

            Assert.AreEqual(0, stale.Count);
        }

        [TestMethod]
        public void ComputeStaleDefinitionIds_DedupesByApiName_AndKeepsOnlyValidStaleIds()
        {
            var existing = new Dictionary<string, long>
            {
                ["One"] = 100,
                ["Two"] = 200,
                ["Three"] = 300
            };

            var incoming = new[] { " two ", "TWO", "three" };

            var stale = SqlNadoCacheBehavior.ComputeStaleDefinitionIds(existing, incoming);

            Assert.AreEqual(1, stale.Count);
            Assert.AreEqual(100L, stale[0]);
        }

        [TestMethod]
        public void ComputeStaleDefinitionIds_NullExisting_ReturnsEmpty()
        {
            var stale = SqlNadoCacheBehavior.ComputeStaleDefinitionIds(null, new[] { "A" });
            Assert.AreEqual(0, stale.Count);
        }

        [TestMethod]
        public void ComputeStaleDefinitionIds_EmptyExisting_ReturnsEmpty()
        {
            var stale = SqlNadoCacheBehavior.ComputeStaleDefinitionIds(new Dictionary<string, long>(), new[] { "A" });
            Assert.AreEqual(0, stale.Count);
        }

        [DataTestMethod]
        [DataRow(0, 0, 0, true)]
        [DataRow(-1, 0, 0, true)]
        [DataRow(0, -1, 0, true)]
        [DataRow(0, 0, -1, true)]
        [DataRow(1, 0, 0, false)]
        [DataRow(0, 2, 0, false)]
        [DataRow(0, 0, 3, false)]
        [DataRow(1, 1, 1, false)]
        public void ShouldMarkLegacyImportDone_UsesFailureAndRemainingCounts(
            int parseFailedCount,
            int dbWriteFailedCount,
            int remainingFileCount,
            bool expected)
        {
            var actual = SqlNadoCacheBehavior.ShouldMarkLegacyImportDone(
                parseFailedCount,
                dbWriteFailedCount,
                remainingFileCount);
            Assert.AreEqual(expected, actual);
        }

        [TestMethod]
        public void BuildDefinitionsFromFriendRows_SeedsFromRowsWithApiNameAndDisplayName()
        {
            var rows = new[]
            {
                new FriendAchievementRow
                {
                    ApiName = "plat",
                    DisplayName = "Platinum",
                    Description = "All trophies",
                    UnlockedIconUrl = "unlocked.png",
                    LockedIconUrl = "locked.png",
                    Points = 90,
                    Category = "Finale Pack",
                    CategoryType = "DLC",
                    TrophyType = "platinum",
                    Hidden = true,
                    GlobalPercentUnlocked = 1.2,
                    Rarity = RarityTier.UltraRare,
                    Unlocked = true
                },
                new FriendAchievementRow { ApiName = "story", DisplayName = "Story done", Description = "Finish", Unlocked = false }
            };

            var defs = SqlNadoCacheBehavior.BuildDefinitionsFromFriendRows(rows);

            Assert.AreEqual(2, defs.Count);
            Assert.AreEqual("plat", defs[0].ApiName);
            Assert.AreEqual("Platinum", defs[0].DisplayName);
            Assert.AreEqual("All trophies", defs[0].Description);
            Assert.AreEqual("unlocked.png", defs[0].UnlockedIconPath);
            Assert.AreEqual("locked.png", defs[0].LockedIconPath);
            Assert.AreEqual(90, defs[0].Points);
            Assert.AreEqual("Finale Pack", defs[0].Category);
            Assert.AreEqual("DLC", defs[0].CategoryType);
            Assert.AreEqual("platinum", defs[0].TrophyType);
            Assert.IsTrue(defs[0].Hidden);
            Assert.IsTrue(defs[0].IsCapstone);
            Assert.AreEqual(1.2, defs[0].GlobalPercentUnlocked);
            Assert.AreEqual(RarityTier.UltraRare, defs[0].Rarity);
        }

        [TestMethod]
        public void BuildDefinitionsFromFriendRows_SkipsRowsMissingKeyOrName_AndDedupesByApiName()
        {
            var rows = new[]
            {
                new FriendAchievementRow { ApiName = "", DisplayName = "No key" },
                new FriendAchievementRow { ApiName = "only-key", DisplayName = "  " },
                new FriendAchievementRow { ApiName = "dup", DisplayName = "First" },
                new FriendAchievementRow { ApiName = " DUP ", DisplayName = "Second (dupe key)" },
                null
            };

            var defs = SqlNadoCacheBehavior.BuildDefinitionsFromFriendRows(rows);

            Assert.AreEqual(1, defs.Count);
            Assert.AreEqual("dup", defs[0].ApiName);
            Assert.AreEqual("First", defs[0].DisplayName);
        }

        [TestMethod]
        public void BuildDefinitionsFromFriendRows_NullInput_ReturnsEmpty()
        {
            Assert.AreEqual(0, SqlNadoCacheBehavior.BuildDefinitionsFromFriendRows(null).Count);
        }

        [TestMethod]
        public void ComputeDefinitionCategoryBackfills_FillsOnlyDefaultPlaceholders()
        {
            var existing = new List<(long Id, string ApiName, string Category, string CategoryType)>
            {
                (1, "ach_blank", null, null),
                (2, "ach_default", "Default", "Default"),
                (3, "ach_categorized", "Base Game", "Base"),
                (4, "ach_no_incoming", "Default", "Default")
            };
            var incoming = new List<AchievementDetail>
            {
                new AchievementDetail { ApiName = "ach_blank", Category = "Phantom Liberty", CategoryType = "DLC" },
                new AchievementDetail { ApiName = "ach_default", Category = "Phantom Liberty", CategoryType = "DLC" },
                new AchievementDetail { ApiName = "ach_categorized", Category = "Renamed Group", CategoryType = "DLC" }
            };

            var backfills = SqlNadoCacheBehavior.ComputeDefinitionCategoryBackfills(existing, incoming);

            Assert.AreEqual(2, backfills.Count);
            Assert.AreEqual(1L, backfills[0].DefinitionId);
            Assert.AreEqual("Phantom Liberty", backfills[0].Category);
            Assert.AreEqual("DLC", backfills[0].CategoryType);
            Assert.AreEqual(2L, backfills[1].DefinitionId);
            Assert.AreEqual("Phantom Liberty", backfills[1].Category);
            Assert.AreEqual("DLC", backfills[1].CategoryType);
        }

        [TestMethod]
        public void ComputeDefinitionCategoryBackfills_FillsPartiallyMissingFieldsIndependently()
        {
            var existing = new List<(long Id, string ApiName, string Category, string CategoryType)>
            {
                (1, "ach_label_only", "Existing Label", "Default"),
                (2, "ach_type_only", "Default", "DLC")
            };
            var incoming = new List<AchievementDetail>
            {
                new AchievementDetail { ApiName = "ach_label_only", Category = "Incoming Label", CategoryType = "DLC" },
                new AchievementDetail { ApiName = "ach_type_only", Category = "Incoming Label", CategoryType = "Base" }
            };

            var backfills = SqlNadoCacheBehavior.ComputeDefinitionCategoryBackfills(existing, incoming);

            Assert.AreEqual(2, backfills.Count);
            Assert.AreEqual("Existing Label", backfills[0].Category);
            Assert.AreEqual("DLC", backfills[0].CategoryType);
            Assert.AreEqual("Incoming Label", backfills[1].Category);
            Assert.AreEqual("DLC", backfills[1].CategoryType);
        }

        [TestMethod]
        public void ComputeDefinitionCategoryBackfills_SkipsDefaultOnlyIncomingAndUnknownApiNames()
        {
            var existing = new List<(long Id, string ApiName, string Category, string CategoryType)>
            {
                (1, "ach_one", null, null)
            };
            var incoming = new List<AchievementDetail>
            {
                new AchievementDetail { ApiName = "ach_one", Category = "  ", CategoryType = "Default" },
                new AchievementDetail { ApiName = "ach_unknown", Category = "DLC Pack", CategoryType = "DLC" },
                null
            };

            Assert.AreEqual(0, SqlNadoCacheBehavior.ComputeDefinitionCategoryBackfills(existing, incoming).Count);
            Assert.AreEqual(0, SqlNadoCacheBehavior.ComputeDefinitionCategoryBackfills(null, incoming).Count);
            Assert.AreEqual(0, SqlNadoCacheBehavior.ComputeDefinitionCategoryBackfills(existing, null).Count);
        }

        [TestMethod]
        public void HasLegacyExophaseKeyedDefinitions_DetectsOnlyLegacyPrefix()
        {
            Assert.IsTrue(SqlNadoCacheBehavior.HasLegacyExophaseKeyedDefinitions(
                new[] { "default:5", "exophase_platinum_trophy" }));
            Assert.IsFalse(SqlNadoCacheBehavior.HasLegacyExophaseKeyedDefinitions(
                new[] { "exophase:7186932", "default:5", "KNT_Achievement_05" }));
            Assert.IsFalse(SqlNadoCacheBehavior.HasLegacyExophaseKeyedDefinitions(new string[0]));
            Assert.IsFalse(SqlNadoCacheBehavior.HasLegacyExophaseKeyedDefinitions(null));
        }

        [TestMethod]
        public void MatchesNativeKey_MatchesVerbatimIgnoringCaseAndWhitespace()
        {
            // Steam: definition ApiName is the apiname, which is exactly the aggregator's native key.
            Assert.IsTrue(SqlNadoCacheBehavior.MatchesNativeKey("KNT_Achievement_05", "knt_achievement_05"));
            Assert.IsTrue(SqlNadoCacheBehavior.MatchesNativeKey(" quest_begin_gristle ", "quest_begin_gristle"));
            Assert.IsFalse(SqlNadoCacheBehavior.MatchesNativeKey("KNT_Achievement_05", "KNT_Achievement_06"));
            Assert.IsFalse(SqlNadoCacheBehavior.MatchesNativeKey(null, "key"));
            Assert.IsFalse(SqlNadoCacheBehavior.MatchesNativeKey("key", null));
            Assert.IsFalse(SqlNadoCacheBehavior.MatchesNativeKey("", ""));
        }

        [TestMethod]
        public void MatchesGroupQualifiedNativeKey_MatchesPsnTrophyKeys()
        {
            // PSN: definitions are keyed "{group}:{trophyId}"; the native key is the bare trophy id.
            Assert.IsTrue(SqlNadoCacheBehavior.MatchesGroupQualifiedNativeKey("default:5", "5"));
            Assert.IsTrue(SqlNadoCacheBehavior.MatchesGroupQualifiedNativeKey("001:17", "17"));
            Assert.IsFalse(SqlNadoCacheBehavior.MatchesGroupQualifiedNativeKey("default:5", "6"));
        }

        [TestMethod]
        public void MatchesGroupQualifiedNativeKey_RejectsAggregatorAndMalformedKeys()
        {
            // "exophase:{id}" is the aggregator's own scheme, never a group-qualified native key.
            Assert.IsFalse(SqlNadoCacheBehavior.MatchesGroupQualifiedNativeKey("exophase:7186932", "7186932"));
            Assert.IsFalse(SqlNadoCacheBehavior.MatchesGroupQualifiedNativeKey("EXOPHASE:5", "5"));
            Assert.IsFalse(SqlNadoCacheBehavior.MatchesGroupQualifiedNativeKey("no-separator", "no-separator"));
            Assert.IsFalse(SqlNadoCacheBehavior.MatchesGroupQualifiedNativeKey(":5", "5"));
            Assert.IsFalse(SqlNadoCacheBehavior.MatchesGroupQualifiedNativeKey("default:", ""));
            Assert.IsFalse(SqlNadoCacheBehavior.MatchesGroupQualifiedNativeKey(null, "5"));
            Assert.IsFalse(SqlNadoCacheBehavior.MatchesGroupQualifiedNativeKey("default:5", null));
        }

        [TestMethod]
        public void ShouldFallbackToProviderGameIdLookup_RetroAchievementsWithPlayniteId_ReturnsFalse()
        {
            var shouldFallback = SqlNadoCacheBehavior.ShouldFallbackToProviderGameIdLookup(
                "RetroAchievements",
                Guid.NewGuid().ToString(),
                12345);

            Assert.IsFalse(shouldFallback);
        }

        [TestMethod]
        public void ShouldFallbackToProviderGameIdLookup_RetroAchievementsWithoutPlayniteId_ReturnsTrue()
        {
            var shouldFallback = SqlNadoCacheBehavior.ShouldFallbackToProviderGameIdLookup(
                "RetroAchievements",
                null,
                12345);

            Assert.IsTrue(shouldFallback);
        }

        [TestMethod]
        public void ShouldFallbackToProviderGameIdLookup_NonRetroAchievementsWithPlayniteId_ReturnsFalse()
        {
            var shouldFallback = SqlNadoCacheBehavior.ShouldFallbackToProviderGameIdLookup(
                "Steam",
                Guid.NewGuid().ToString(),
                12345);

            Assert.IsFalse(shouldFallback);
        }

        [TestMethod]
        public void ShouldFallbackToProviderGameIdLookup_NonRetroAchievementsWithoutPlayniteId_ReturnsTrue()
        {
            var shouldFallback = SqlNadoCacheBehavior.ShouldFallbackToProviderGameIdLookup(
                "Steam",
                null,
                12345);

            Assert.IsTrue(shouldFallback);
        }

        [DataTestMethod]
        [DataRow(null)]
        [DataRow(0L)]
        [DataRow(-5L)]
        public void ShouldFallbackToProviderGameIdLookup_MissingOrInvalidProviderGameId_ReturnsFalse(long? providerGameId)
        {
            var shouldFallback = SqlNadoCacheBehavior.ShouldFallbackToProviderGameIdLookup(
                "Steam",
                Guid.NewGuid().ToString(),
                providerGameId);

            Assert.IsFalse(shouldFallback);
        }

        [DataTestMethod]
        [DataRow("RetroAchievements", true)]
        [DataRow(" retroachievements ", true)]
        [DataRow("Steam", false)]
        [DataRow("", false)]
        [DataRow(null, false)]
        public void IsRetroAchievementsProvider_DetectsExpectedProviders(string providerName, bool expected)
        {
            var actual = SqlNadoCacheBehavior.IsRetroAchievementsProvider(providerName);
            Assert.AreEqual(expected, actual);
        }

        [DataTestMethod]
        [DataRow("Steam", true)]
        [DataRow("GOG", true)]
        [DataRow("Exophase", false)]
        [DataRow("Manual", false)]
        [DataRow("Unmapped", false)]
        [DataRow("", false)]
        [DataRow(null, false)]
        public void CanReclaimExophaseProxy_AllowsNativeProvidersOnly(
            string incomingProviderKey,
            bool expected)
        {
            var actual = SqlNadoCacheBehavior.CanReclaimExophaseProxy(incomingProviderKey);
            Assert.AreEqual(expected, actual);
        }

        [DataTestMethod]
        [DataRow(false, 1, 10, false, false)]
        [DataRow(true, 1, 10, false, true)]
        [DataRow(false, 10, 10, false, true)]
        [DataRow(false, 1, 10, true, true)]
        [DataRow(false, 0, 0, false, false)]
        public void ComputeIsCompleted_UsesProviderHundredPercentOrMarker(
            bool providerIsCompleted,
            int unlockedCount,
            int totalCount,
            bool markerUnlocked,
            bool expected)
        {
            var actual = SqlNadoCacheBehavior.ComputeIsCompleted(
                providerIsCompleted,
                unlockedCount,
                totalCount,
                markerUnlocked);

            Assert.AreEqual(expected, actual);
        }

        [TestMethod]
        public void CacheStore_FriendGameLinksLoadOwnershipPlaytimeAndLastPlayed()
        {
            var store = File.ReadAllText(FindRepoFile("source", "Services", "Database", "SqlNadoCacheStore.cs"));
            var models = File.ReadAllText(FindRepoFile("source", "Services", "Friends", "FriendCacheModels.cs"));

            StringAssert.Contains(models, "public long PlaytimeForeverMinutes { get; set; }");
            StringAssert.Contains(models, "public DateTime? LastPlayedUtc { get; set; }");
            StringAssert.Contains(store, "public long PlaytimeForeverMinutes { get; set; }");
            StringAssert.Contains(store, "public string LastPlayedUtc { get; set; }");
            StringAssert.Contains(store, "fo.PlaytimeForeverMinutes AS PlaytimeForeverMinutes");
            StringAssert.Contains(store, "fo.LastPlayedUtc AS LastPlayedUtc");
            StringAssert.Contains(store, "PlaytimeForeverMinutes = Math.Max(0, row.PlaytimeForeverMinutes)");
            StringAssert.Contains(store, "LastPlayedUtc = ParseUtc(row.LastPlayedUtc)");
        }

        [TestMethod]
        public void CacheStore_FriendIdentitiesExposeAbsoluteAvatarPath()
        {
            var store = File.ReadAllText(FindRepoFile("source", "Services", "Database", "SqlNadoCacheStore.cs"));

            StringAssert.Contains(store, "AvatarPath = !string.IsNullOrWhiteSpace(row.AvatarPath)");
            StringAssert.Contains(store, "? MakeAbsolutePath(row.AvatarPath)");
            StringAssert.Contains(store, ": row.AvatarUrl");
        }

        [TestMethod]
        public void CacheStore_NormalFriendOwnershipCleanupPreservesProviderOnlyRows()
        {
            var models = File.ReadAllText(FindRepoFile("source", "Services", "Friends", "FriendCacheModels.cs"));
            var store = File.ReadAllText(FindRepoFile("source", "Services", "Database", "SqlNadoCacheStore.cs"));

            StringAssert.Contains(models, "public bool PruneStaleShared { get; set; }");
            StringAssert.Contains(store, "options?.IncludeProviderOnlyGames != true");
            StringAssert.Contains(store, "options?.PruneStaleShared == true");
            StringAssert.Contains(store, "seenSharedGameIds.Count > 0");
            StringAssert.Contains(store, "DeleteStaleSharedFriendOwnership(db, user.Id, seenSharedGameIds);");
            StringAssert.Contains(store, "private static void DeleteStaleSharedFriendOwnership");
            StringAssert.Contains(store, "Empty ownership is not authoritative");
            StringAssert.Contains(store, "g.PlayniteGameId IS NOT NULL");
            StringAssert.Contains(store, "TRIM(g.PlayniteGameId) <> ''");
        }

        [TestMethod]
        public void CacheStore_EmptyFriendRosterDoesNotDeactivateFriends()
        {
            var store = File.ReadAllText(FindRepoFile("source", "Services", "Database", "SqlNadoCacheStore.cs"));

            StringAssert.Contains(store, "if (seenExternalUserIds == null || seenExternalUserIds.Count == 0)");
            StringAssert.Contains(store, "An empty roster may be a soft-empty/transient provider result.");
            StringAssert.Contains(store, "MarkMissingFriendsInactive(db, providerKey, friendSource, seen, nowIso);");
        }

        [TestMethod]
        public void CacheStore_ExophaseStringKeysDoNotFallbackToStaleSharedMappings()
        {
            var store = File.ReadAllText(FindRepoFile("source", "Services", "Database", "SqlNadoCacheStore.cs"));

            StringAssert.Contains(store, "ShouldUseSharedFriendGameFallback(providerKey, providerGameKey, playniteGameId)");
            StringAssert.Contains(store, "return !IsExophaseProvider(providerKey) || string.IsNullOrWhiteSpace(providerGameKey);");
        }

        [TestMethod]
        public void CacheStore_FriendGameAndAchievementRowsShareImageResolution()
        {
            var store = File.ReadAllText(FindRepoFile("source", "Services", "Database", "SqlNadoCacheStore.cs"));

            StringAssert.Contains(store, "GameLogo = ResolveFriendGameIconPath(presentation, row.IconPath)");
            StringAssert.Contains(store, "GameCoverPath = ResolveFriendGameCoverPath(presentation, row.CoverPath)");
            StringAssert.Contains(store, "GameIconPath = ResolveFriendGameIconPath(presentation, row.IconPath)");
            StringAssert.Contains(store, "GameCoverPath = ResolveFriendGameCoverPath(presentation, row.CoverPath)");
        }

        [TestMethod]
        public void CacheStore_FriendAchievementRowsRequireCurrentOwnership()
        {
            var store = File.ReadAllText(FindRepoFile("source", "Services", "Database", "SqlNadoCacheStore.cs"));

            StringAssert.Contains(store, "INNER JOIN FriendOwnership fo ON fo.UserId = u.Id AND fo.GameId = g.Id");
        }

        [TestMethod]
        public void CacheStore_SelectedGameFriendCandidatesSkipCurrentUserNoAchievementGames()
        {
            var store = File.ReadAllText(FindRepoFile("source", "Services", "Database", "SqlNadoCacheStore.cs"));

            StringAssert.Contains(store, "AND ugp.HasAchievements != 0");
        }

        [TestMethod]
        public void CacheStore_RecentFriendCandidatesUseOwnershipRefreshDelta()
        {
            var store = File.ReadAllText(FindRepoFile("source", "Services", "Database", "SqlNadoCacheStore.cs"));
            var runtime = File.ReadAllText(FindRepoFile("source", "Services", "Refresh", "RefreshRuntime.cs")) +
                          File.ReadAllText(FindRepoFile("source", "Services", "Refresh", "FriendRefreshCoordinator.cs")) +
                          File.ReadAllText(FindRepoFile("source", "Services", "Refresh", "FriendRefreshWorkPolicy.cs"));

            // Recent-scope candidates are gated by explicit scrape state and ownership-refresh deltas,
            // not by a 2-week-playtime filter or a wall-clock last-scrape TTL.
            Assert.IsFalse(store.Contains(@"AND COALESCE(fo.Playtime2WeeksMinutes, 0) > 0"),
                "Recent scope must no longer filter on 2-week playtime in SQL.");
            Assert.IsFalse(store.Contains("fo.LastScrapedUtc < ?"));
            StringAssert.Contains(store, "fo.LastScrapedUtc IS NULL");
            StringAssert.Contains(store, "fo.LastOwnershipRefreshUtc > fo.LastScrapedUtc");
            Assert.IsFalse(runtime.Contains("RefreshTtl"), "The wall-clock friend refresh TTL must be gone.");

            // When ownership is freshly fetched, Steam recency is still the playtime delta since the last
            // successful scrape.
            StringAssert.Contains(runtime, "fresh.PlaytimeForeverMinutes > prev.PlaytimeForeverMinutes");
        }

        [TestMethod]
        public void CacheStore_DeleteFriendData_PreserveFriendRecord_SkipsUsersDelete()
        {
            var store = File.ReadAllText(FindRepoFile("source", "Services", "Database", "SqlNadoCacheStore.cs"));

            // Clear keeps the friend registered; only Remove/Ignore hard-delete the Users row.
            StringAssert.Contains(store, "public FriendCacheWriteResult DeleteFriendData(string providerKey, string externalUserId, bool preserveFriendRecord = false)");
            StringAssert.Contains(store, "if (!preserveFriendRecord)");
            StringAssert.Contains(store, "DELETE FROM Users WHERE Id = ? AND IsCurrentUser = 0;");
        }

        [TestMethod]
        public void ClearFriend_PreservesFriendRecord_WhileRemoveAndIgnoreDoNot()
        {
            var overview = File.ReadAllText(FindRepoFile("source", "Views", "FriendsOverviewControl.xaml.cs"));

            StringAssert.Contains(overview, "foreach (var account in GetConfigurableFriendAccounts(friend))");
            StringAssert.Contains(overview, "DeleteFriendData(account.ProviderKey, account.ExternalUserId, preserveFriendRecord: true)");
            // Ignore keeps the full delete (no preserve flag).
            StringAssert.Contains(overview, "DeleteFriendData(account.ProviderKey, account.ExternalUserId);");
        }

        [TestMethod]
        public void CacheStore_FriendGameSummary_UsesDisplayProviderKey()
        {
            var store = File.ReadAllText(FindRepoFile("source", "Services", "Database", "SqlNadoCacheStore.cs"));

            StringAssert.Contains(store, "g.ProviderPlatformKey AS ProviderPlatformKey");
            StringAssert.Contains(store, "ResolveDisplayProviderKey(row.ProviderKey, row.ProviderPlatformKey)");
            StringAssert.Contains(store, "ProviderRegistry.TryResolveProviderVisuals(displayProviderKey");
        }

        [TestMethod]
        public void FriendGameImages_DownloadedToPaths_NotPersistedAsUrl()
        {
            var store = File.ReadAllText(FindRepoFile("source", "Services", "Database", "SqlNadoCacheStore.cs"));
            var runtime = File.ReadAllText(FindRepoFile("source", "Services", "Refresh", "FriendRefreshCoordinator.cs"));

            // Header banner is downloaded with the normalized cache key and stored as local icon+cover paths.
            StringAssert.Contains(runtime, "var cacheKey = GetProviderGameCacheKey(appId, providerGameKey);");
            StringAssert.Contains(runtime, "DownloadDefinitionGameImageAsync(providerKey, providerGameKey, appId, definition.IconUrl, definition.GameName, cancel, progress)");
            StringAssert.Contains(runtime, "SaveProviderGameImagePaths(providerKey, cacheKey, appId, localPath, localPath);");
            StringAssert.Contains(runtime, "var gameKey = GetProviderGameCacheKey(source.AppId, source.ProviderGameKey)");
            StringAssert.Contains(store, "AND (ProviderGameKey = ? OR ProviderGameId = ?)");
            // The source URL is not persisted into the definition state.
            StringAssert.Contains(store, "// Image source URLs are not persisted; the header banner is downloaded to");
        }

        [TestMethod]
        public void CacheStore_ProviderOnlyPromotionMergesDefinitionsWithoutDuplicateApiNames()
        {
            var store = File.ReadAllText(FindRepoFile("source", "Services", "Database", "SqlNadoCacheStore.cs"));

            StringAssert.Contains(store, "MergeProviderOnlyDefinitionsIntoTarget(db, source.Id, target.Id, nowIso);");
            StringAssert.Contains(store, "private static void MergeProviderOnlyDefinitionsIntoTarget");
            StringAssert.Contains(store, "AND NOT EXISTS");
            StringAssert.Contains(store, "target.GameId = ?");
            StringAssert.Contains(store, "target.ApiName = AchievementDefinitions.ApiName");
            Assert.IsFalse(
                store.Contains("MoveProviderOnlyDefinitionsIfTargetIsEmpty"),
                "Promotion must merge missing definitions instead of skipping or failing when the target already has definitions.");
        }

        [TestMethod]
        public void ResolveFriendDefinition_PrefersStableApiName_OverLocalizedDisplayText()
        {
            var store = File.ReadAllText(FindRepoFile("source", "Services", "Database", "SqlNadoCacheStore.cs"));

            // The stable api name is tried before the display-name/description/icon fallbacks so a
            // friend's localized achievement text still matches the canonical definition.
            StringAssert.Contains(store, "var rowApiName = NormalizeMatchText(row.ApiName);");
            StringAssert.Contains(store, "string.Equals(NormalizeMatchText(def.ApiName), rowApiName, StringComparison.OrdinalIgnoreCase)");
            var apiNameIndex = store.IndexOf("var byApiName = definitions", StringComparison.Ordinal);
            var displayNameIndex = store.IndexOf("var exact = definitions", StringComparison.Ordinal);
            Assert.IsTrue(apiNameIndex > 0 && apiNameIndex < displayNameIndex,
                "ApiName match must precede the display-name match.");
        }

        [TestMethod]
        public void CacheStore_ExophaseMappedFriendAchievementsReuseCurrentUserDefinitions()
        {
            var store = File.ReadAllText(FindRepoFile("source", "Services", "Database", "SqlNadoCacheStore.cs"));

            StringAssert.Contains(store, "TryRefreshMappedCurrentUserDefinitionsForProxyGame(");
            StringAssert.Contains(store, "LoadMappedCurrentUserDefinitionsForProxyGame(db, game)");
            StringAssert.Contains(store, "ExophaseFriendPlatformMatcher.ResolveStoredGameFamilyKey");
            StringAssert.Contains(store, "CreateAchievementDetailFromDefinitionRow");
            StringAssert.Contains(store, "ShouldRefreshDefinitionsFromFriendRows(definitions, seededDefinitions)");
            Assert.IsFalse(store.Contains("UpdateAchievementDefinitionMetadataFromFriendRows"),
                "Exophase friend metadata must not overwrite mapped/native current-user definitions.");
        }

        [TestMethod]
        public void CacheStore_FriendAchievementRowsCoverFullDefinitionSchema()
        {
            var store = File.ReadAllText(FindRepoFile("source", "Services", "Database", "SqlNadoCacheStore.cs"));

            // The friend+game view shows the game's full schema with the friend's unlock state
            // joined per definition, not just the rows the friend scrape happened to store.
            StringAssert.Contains(store, "INNER JOIN AchievementDefinitions ad ON ad.GameId = g.Id");
            StringAssert.Contains(store, "LEFT JOIN UserAchievements ua ON ua.UserGameProgressId = ugp.Id");
            StringAssert.Contains(store, "AND ua.AchievementDefinitionId = ad.Id");
            StringAssert.Contains(store, "COALESCE(ua.Unlocked, 0) AS Unlocked");
        }

        [TestMethod]
        public void CacheStore_FriendAchievementRowsResolveMyUnlocksByPlayniteGameAndApiName()
        {
            var store = File.ReadAllText(FindRepoFile("source", "Services", "Database", "SqlNadoCacheStore.cs"));

            StringAssert.Contains(store, "WITH CurrentUnlocks AS");
            StringAssert.Contains(store, "GROUP BY cg.PlayniteGameId, cad.ApiName");
            StringAssert.Contains(store, "LEFT JOIN CurrentUnlocks cu ON cu.PlayniteGameId = g.PlayniteGameId");
            StringAssert.Contains(store, "AND cu.ApiName = ad.ApiName");
        }

        [TestMethod]
        public void CacheStore_FriendAchievementRowsApplyCustomDataForAllProviders()
        {
            var store = File.ReadAllText(FindRepoFile("source", "Services", "Database", "SqlNadoCacheStore.cs"));

            StringAssert.Contains(store, "var customDataByGameId = new Dictionary<Guid, ResolvedGameCustomData>();");
            StringAssert.Contains(store, "ApplyCustomDataToFriendAchievement(item, customData);");
            StringAssert.Contains(store, "GameCustomDataLookup.ResolveGameCustomData(");
            StringAssert.Contains(store, "_plugin?.GameCustomDataStore");
            StringAssert.Contains(store, "customData.AchievementCategoryOverrides");
            StringAssert.Contains(store, "customData.AchievementCategoryTypeOverrides");
            StringAssert.Contains(store, "AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(categoryOverride)");
            StringAssert.Contains(store, "AchievementCategoryTypeHelper.NormalizeOrDefault(categoryTypeOverride)");
        }

        [TestMethod]
        public void NormalizeMatchText_FoldsDiacritics()
        {
            var store = File.ReadAllText(FindRepoFile("source", "Services", "Database", "SqlNadoCacheStore.cs"));

            StringAssert.Contains(store, "NormalizationForm.FormD");
            StringAssert.Contains(store, "UnicodeCategory.NonSpacingMark");
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
    }
}
