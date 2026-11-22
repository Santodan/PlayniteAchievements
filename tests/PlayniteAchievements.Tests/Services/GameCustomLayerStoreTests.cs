using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Cache;
using PlayniteAchievements.Services.Database;
using PlayniteAchievements.Services.GameCustomData;
using PlayniteAchievements.Services.Images;
using SqlNado;

namespace PlayniteAchievements.Tests.Services
{
    /// <summary>
    /// The custom layer tables are the only store of custom data: a save writes them and a load
    /// rebuilds the record from them, field for field.
    /// </summary>
    [TestClass]
    [DoNotParallelize]
    public class GameCustomLayerStoreTests
    {
        private static readonly DateTime Unlocked = new DateTime(2025, 3, 1, 10, 30, 0, DateTimeKind.Utc);

        [TestMethod]
        public void EveryFieldGroup_RoundTripsThroughTheTables()
        {
            WithDirectory(dir =>
            {
                var gameId = Guid.NewGuid();
                var rich = Rich(gameId);
                var expected = GameCustomDataNormalizer.NormalizeInternal(rich, gameId);

                new GameCustomDataStore(dir).Save(gameId, rich);
                var loaded = new GameCustomDataStore(dir).LoadOrDefault(gameId);

                Assert.AreEqual(Canonical(expected), Canonical(loaded));

                // Every group the record carries survived normalization, so the comparison covers it.
                foreach (var name in new[]
                {
                    nameof(GameCustomDataFile.ExcludedFromRefreshes), nameof(GameCustomDataFile.ExcludedFromSummaries),
                    nameof(GameCustomDataFile.ExcludedFromMetadata), nameof(GameCustomDataFile.RuleClaims),
                    nameof(GameCustomDataFile.UserPinnedFields), nameof(GameCustomDataFile.UseSeparateLockedIconsOverride),
                    nameof(GameCustomDataFile.Capstones), nameof(GameCustomDataFile.AchievementOrder),
                    nameof(GameCustomDataFile.AchievementCategoryOrder), nameof(GameCustomDataFile.AchievementCategoryImageOverrides),
                    nameof(GameCustomDataFile.AchievementCategoryKinds), nameof(GameCustomDataFile.AchievementTags),
                    nameof(GameCustomDataFile.GameSummaryCategory), nameof(GameCustomDataFile.FilteredAchievementApiNames),
                    nameof(GameCustomDataFile.SummaryFilteredAchievementApiNames), nameof(GameCustomDataFile.GoalAchievementApiNames),
                    nameof(GameCustomDataFile.AchievementOverrides), nameof(GameCustomDataFile.ProviderOverride),
                    nameof(GameCustomDataFile.MetadataLinks), nameof(GameCustomDataFile.ManualLink),
                    nameof(GameCustomDataFile.CustomAchievements), nameof(GameCustomDataFile.CustomProviderId),
                    nameof(GameCustomDataFile.LinkedFile), nameof(GameCustomDataFile.UnlockSounds),
                    nameof(GameCustomDataFile.NotificationBehaviorOverride), nameof(GameCustomDataFile.CaptureOverride)
                })
                {
                    Assert.IsNotNull(typeof(GameCustomDataFile).GetProperty(name).GetValue(loaded), name);
                }

                Assert.IsTrue(loaded.CapstonesMaterialized);
                Assert.IsTrue(loaded.AutoCapstoneGenerated);
                Assert.AreEqual(4, loaded.AchievementOverrides.Count);
                CollectionAssert.AreEqual(new[] { "mine", "earlier" }, loaded.CustomAchievements.Select(definition => definition.Id).ToList());
                CollectionAssert.AreEqual(new[] { "a5", "a4" }, loaded.SummaryFilteredAchievementApiNames);
                CollectionAssert.AreEqual(new[] { "a2", "custom:mine" }, loaded.Capstones.Select(capstone => capstone.ApiName).ToList());
            });
        }

        [TestMethod]
        public void AnUnmaterializedCapstoneList_RoundTrips()
        {
            WithDirectory(dir =>
            {
                var gameId = Guid.NewGuid();
                var record = new GameCustomDataFile
                {
                    PlayniteGameId = gameId,
                    AchievementOrder = new List<string> { "a" },
                    Capstones = new List<CapstoneAssignment> { new CapstoneAssignment { ApiName = "b" }, new CapstoneAssignment { ApiName = "a" } }
                };

                new GameCustomDataStore(dir).Save(gameId, record);

                Assert.AreEqual(Canonical(GameCustomDataNormalizer.NormalizeInternal(record, gameId)), Canonical(new GameCustomDataStore(dir).LoadOrDefault(gameId)));
            });
        }

        [TestMethod]
        public void TheColumnsHoldTheirFields_AndSettingsJsonTheRest()
        {
            WithDirectory(dir =>
            {
                var gameId = Guid.NewGuid();
                new GameCustomDataStore(dir).Save(gameId, Rich(gameId));

                using (var db = new SQLiteDatabase(Path.Combine(dir, SqlNadoSchemaManager.DatabaseFileName)))
                {
                    var settings = JObject.Parse(db.ExecuteScalar<string>(
                        "SELECT SettingsJson FROM CustomLayerGames WHERE PlayniteGameId = ?;", new object[] { gameId.ToString("D") }));
                    foreach (var columnField in new[]
                    {
                        "AchievementOrder", "GoalAchievementApiNames", "AchievementCategoryKinds", "AchievementCategoryOrder",
                        "AchievementCategoryImageOverrides", "ExcludedFromRefreshes", "ExcludedFromSummaries", "UseSeparateLockedIconsOverride",
                        "CapstonesMaterialized", "Capstones", "FilteredAchievementApiNames", "SummaryFilteredAchievementApiNames",
                        "AchievementOverrides", "CustomAchievements"
                    })
                    {
                        Assert.IsNull(settings[columnField], columnField + " lives in a column");
                    }

                    // The authored achievement's row holds it as defined; the override has its own row.
                    Assert.AreEqual("First", db.ExecuteScalar<string>(
                        "SELECT DisplayName FROM CustomLayerAchievements WHERE ApiName = 'a1';", Array.Empty<object>()));
                    Assert.AreEqual("C:/icons/a1.png", db.ExecuteScalar<string>(
                        "SELECT UnlockedIconPath FROM CustomLayerAchievements WHERE ApiName = 'a1';", Array.Empty<object>()));
                    Assert.AreEqual("Mine", db.ExecuteScalar<string>(
                        "SELECT DisplayName FROM CustomLayerAchievements WHERE ApiName = 'custom:mine' AND IsAuthored = 1;", Array.Empty<object>()));
                    Assert.AreEqual("Renamed", db.ExecuteScalar<string>(
                        "SELECT DisplayName FROM CustomLayerAchievements WHERE ApiName = 'custom:mine' AND IsAuthored = 0;", Array.Empty<object>()));
                    Assert.AreEqual(1L, db.ExecuteScalar<long>(
                        "SELECT CapstoneOrder FROM CustomLayerAchievements WHERE ApiName = 'custom:mine' AND IsAuthored = 0;", Array.Empty<object>()));
                    Assert.AreEqual((long)(AchievementField.Description | AchievementField.TrophyType | AchievementField.Tags | AchievementField.UnlockTime),
                        db.ExecuteScalar<long>("SELECT Cleared FROM CustomLayerAchievements WHERE ApiName = 'a2';", Array.Empty<object>()));
                }
            });
        }

        [TestMethod]
        public void AnApiNameSpelledTwoWays_ReadsBackInItsFirstSpelling()
        {
            WithDirectory(dir =>
            {
                var gameId = Guid.NewGuid();
                // One row per achievement ignoring case, as every reader compares them.
                var record = new GameCustomDataFile
                {
                    PlayniteGameId = gameId,
                    FilteredAchievementApiNames = new List<string> { "a1" },
                    AchievementOrder = new List<string> { "A1", "b2" }
                };

                new GameCustomDataStore(dir).Save(gameId, record);
                var loaded = new GameCustomDataStore(dir).LoadOrDefault(gameId);

                CollectionAssert.AreEqual(new[] { "a1" }, loaded.FilteredAchievementApiNames);
                CollectionAssert.AreEqual(new[] { "a1", "b2" }, loaded.AchievementOrder);
            });
        }

        [TestMethod]
        public void StoredPaths_ReadBackAsAbsolutePaths_AndStoreAsTheyWere()
        {
            var pluginRoot = Path.Combine(Path.GetTempPath(), "PlayniteAchievementsTests", "plugin");
            var cacheRoot = IconCacheLocation.DefaultRoot(pluginRoot);
            string ToStored(string path) => StoredPathMapping.ToStored(path, pluginRoot, cacheRoot);
            string FromStored(string path) => StoredPathMapping.FromStored(path, pluginRoot, cacheRoot);

            // Every form the database stores comes back from an absolute path unchanged.
            foreach (var stored in new[]
            {
                @"icon_cache\0182fb32-b892-4e15-bcb1-0a9899a8d397\custom\custom_auto-capstone.png",
                @"metadata\Exophase\icon.png",
                @"D:\Pictures\art.png",
                "https://example.com/icon.png",
                null
            })
            {
                Assert.AreEqual(stored, ToStored(FromStored(stored)), stored);
            }

            // Each form a record holds reads back as the absolute path of the same file.
            var underCache = Path.Combine(cacheRoot, "game", "custom", "a.png");
            foreach (var pair in new Dictionary<string, string>
            {
                [underCache] = underCache,
                [@"icon_cache\game\custom\a.png"] = underCache,
                [underCache.Replace('\\', '/')] = underCache,
                [Path.Combine(pluginRoot, "metadata", "icon.png")] = Path.Combine(pluginRoot, "metadata", "icon.png"),
                [@"D:\Pictures\art.png"] = @"D:\Pictures\art.png",
                ["C:/icons/a1.png"] = "C:/icons/a1.png",
                ["https://example.com/icon.png"] = "https://example.com/icon.png"
            })
            {
                Assert.AreEqual(pair.Value, FromStored(ToStored(pair.Key)), pair.Key);
            }
        }

        [TestMethod]
        public void Save_ReturnsTheRecordWithPathsAsALoadReadsThem()
        {
            WithDirectory(dir =>
            {
                var gameId = Guid.NewGuid();
                var store = new GameCustomDataStore(dir);
                var expected = Path.Combine(IconCacheLocation.CurrentRootOrDefault(dir), gameId.ToString("D"), "custom", "a.png");
                store.Save(gameId, new GameCustomDataFile
                {
                    PlayniteGameId = gameId,
                    AchievementOverrides = new Dictionary<string, AchievementOverride>
                    {
                        ["a"] = new AchievementOverride { UnlockedIconPath = @"icon_cache\" + gameId.ToString("D") + @"\custom\a.png" }
                    }
                });

                Assert.AreEqual(expected, store.LoadOrDefault(gameId).AchievementOverrides["a"].UnlockedIconPath, "the saved record");
                Assert.AreEqual(expected, new GameCustomDataStore(dir).LoadOrDefault(gameId).AchievementOverrides["a"].UnlockedIconPath, "a load");
            });
        }

        [TestMethod]
        public void TheFirstLayoutsTables_AreDropped_AndTheCustomDataMovesInAgain()
        {
            WithDirectory(dir =>
            {
                var gameId = Guid.NewGuid();
                var path = Path.Combine(dir, SqlNadoSchemaManager.DatabaseFileName);
                using (var db = new SQLiteDatabase(path))
                {
                    db.ExecuteNonQuery("CREATE TABLE CacheMetadata (Key TEXT PRIMARY KEY NOT NULL, Value TEXT NOT NULL);");
                    db.ExecuteNonQuery("INSERT INTO CacheMetadata VALUES (?, '2025-02-01T00:00:00.0000000Z');", AchievementLayerSql.CustomLayerMigratedKey);
                    db.ExecuteNonQuery("CREATE TABLE CustomLayerAchievements (Id INTEGER PRIMARY KEY, PlayniteGameId TEXT, ApiName TEXT, IsFiltered INTEGER, UpdatedUtc TEXT);");
                    db.ExecuteNonQuery("CREATE TABLE CustomLayerGames (PlayniteGameId TEXT PRIMARY KEY, SettingsJson TEXT);");
                    db.ExecuteNonQuery("CREATE TABLE CustomLayerCategories (PlayniteGameId TEXT, Category TEXT);");
                    db.ExecuteNonQuery("INSERT INTO CustomLayerGames VALUES (?, '{}');", gameId.ToString("D"));
                    db.ExecuteNonQuery("CREATE TABLE GameCustomData (PlayniteGameId TEXT PRIMARY KEY NOT NULL, PayloadJson TEXT NOT NULL, UpdatedUtc TEXT NOT NULL);");
                    db.ExecuteNonQuery("INSERT INTO GameCustomData VALUES (?, ?, '2025-01-01T00:00:00.0000000Z');",
                        gameId.ToString("D"), JsonConvert.SerializeObject(new GameCustomDataFile { PlayniteGameId = gameId, FilteredAchievementApiNames = new List<string> { "a" } }));
                }

                CollectionAssert.AreEqual(new[] { "a" }, new GameCustomDataStore(dir).LoadOrDefault(gameId).FilteredAchievementApiNames);
                Assert.AreEqual(1, Directory.GetDirectories(dir, "customdata-layer-*", SearchOption.AllDirectories).Length, "backed up before moving in");
                CollectionAssert.AreEqual(new[] { "a" }, new GameCustomDataStore(dir).LoadOrDefault(gameId).FilteredAchievementApiNames, "once");
            });
        }

        [TestMethod]
        public void Migration_MovesEveryPayload_KeepsTheLegacyTable_AndBacksUpFirst()
        {
            WithDirectory(dir =>
            {
                var rich = Guid.NewGuid();
                var unreadable = Guid.NewGuid();
                var path = Path.Combine(dir, SqlNadoSchemaManager.DatabaseFileName);
                var payload = JsonConvert.SerializeObject(Rich(rich));
                using (var db = new SQLiteDatabase(path))
                {
                    db.ExecuteNonQuery("CREATE TABLE GameCustomData (PlayniteGameId TEXT PRIMARY KEY NOT NULL, PayloadJson TEXT NOT NULL, UpdatedUtc TEXT NOT NULL);");
                    db.ExecuteNonQuery("INSERT INTO GameCustomData VALUES (?, ?, '2025-01-01T00:00:00.0000000Z');", rich.ToString("D"), payload);
                    db.ExecuteNonQuery("INSERT INTO GameCustomData VALUES (?, '{not json', '2025-01-01T00:00:00.0000000Z');", unreadable.ToString("D"));
                }

                var store = new GameCustomDataStore(dir);
                Assert.AreEqual(1, store.LoadAll().Count, "the unreadable payload is left behind");
                Assert.AreEqual(
                    Canonical(GameCustomDataNormalizer.NormalizeInternal(JsonConvert.DeserializeObject<GameCustomDataFile>(payload), rich)),
                    Canonical(new GameCustomDataStore(dir).LoadOrDefault(rich)));
                Assert.AreEqual(1, Directory.GetDirectories(dir, "customdata-layer-*", SearchOption.AllDirectories).Length);

                using (var db = new SQLiteDatabase(path))
                {
                    Assert.AreEqual(2L, db.ExecuteScalar<long>("SELECT COUNT(*) FROM GameCustomData;", Array.Empty<object>()));
                    Assert.AreEqual(payload, db.ExecuteScalar<string>("SELECT PayloadJson FROM GameCustomData WHERE PlayniteGameId = ?;", new object[] { rich.ToString("D") }));
                    Assert.IsNotNull(db.ExecuteScalar<string>("SELECT Value FROM CacheMetadata WHERE Key = ?;", new object[] { AchievementLayerSql.CustomLayerMigratedKey }));
                }

                // An edit in the tables, then the next start: nothing moves again.
                new GameCustomDataStore(dir).Update(rich, data => data.AchievementOrder = new List<string> { "edited" });
                Assert.AreEqual(1, new GameCustomDataStore(dir).LoadAll().Count);
                CollectionAssert.AreEqual(new[] { "edited" }, new GameCustomDataStore(dir).LoadOrDefault(rich).AchievementOrder);
                Assert.AreEqual(1, Directory.GetDirectories(dir, "customdata-layer-*", SearchOption.AllDirectories).Length);
            });
        }

        [TestMethod]
        public void Migration_MovesAgain_APayloadAnEarlierVersionWroteAfterIt()
        {
            WithDirectory(dir =>
            {
                var gameId = Guid.NewGuid();
                var path = Path.Combine(dir, SqlNadoSchemaManager.DatabaseFileName);
                using (var db = new SQLiteDatabase(path))
                {
                    db.ExecuteNonQuery("CREATE TABLE GameCustomData (PlayniteGameId TEXT PRIMARY KEY NOT NULL, PayloadJson TEXT NOT NULL, UpdatedUtc TEXT NOT NULL);");
                    db.ExecuteNonQuery("INSERT INTO GameCustomData VALUES (?, ?, '2025-01-01T00:00:00.0000000Z');",
                        gameId.ToString("D"), JsonConvert.SerializeObject(new GameCustomDataFile { PlayniteGameId = gameId, AchievementOrder = new List<string> { "first" } }));
                }

                CollectionAssert.AreEqual(new[] { "first" }, new GameCustomDataStore(dir).LoadOrDefault(gameId).AchievementOrder);

                // An earlier version run on this database edits the game in its own table.
                using (var db = new SQLiteDatabase(path))
                {
                    db.ExecuteNonQuery("UPDATE GameCustomData SET PayloadJson = ?, UpdatedUtc = ?;",
                        JsonConvert.SerializeObject(new GameCustomDataFile { PlayniteGameId = gameId, AchievementOrder = new List<string> { "second" } }),
                        DateTime.UtcNow.AddMinutes(1).ToString("O"));
                }

                CollectionAssert.AreEqual(new[] { "second" }, new GameCustomDataStore(dir).LoadOrDefault(gameId).AchievementOrder);
            });
        }

        [TestMethod]
        public void Save_WritesTheTablesBeforeItsEventsAndTheFeed()
        {
            WithDirectory(dir =>
            {
                var gameId = Guid.NewGuid();
                var path = Path.Combine(dir, SqlNadoSchemaManager.DatabaseFileName);
                var store = new GameCustomDataStore(dir);
                var feed = new AchievementDataFeed(null, null);
                store.AttachChangeFeed(feed);
                store.Save(gameId, new GameCustomDataFile { PlayniteGameId = gameId, AchievementOrder = new List<string> { "a" } });

                var observed = new List<string>();
                string Stored()
                {
                    using (var db = new SQLiteDatabase(path))
                    {
                        return string.Join(",", db.LoadObjects("SELECT ApiName FROM CustomLayerAchievements ORDER BY CustomOrder;").Select(row => (string)row[0]));
                    }
                }

                store.CustomDataWritten += (_, e) => observed.Add("written:" + Stored());
                store.CustomDataChanged += (_, e) => observed.Add("changed:" + Stored());
                feed.AchievementDataChanged += (_, change) => observed.Add("feed:" + Stored() + ":" + string.Join(",", change.GameIds));

                store.Update(gameId, data => data.AchievementOrder = new List<string> { "b", "a" });
                CollectionAssert.AreEqual(new[] { "written:b,a", "changed:b,a", "feed:b,a:" + gameId }, observed);

                // A save that moves no row raises its events but leaves the feed quiet.
                observed.Clear();
                store.Update(gameId, data => { });
                CollectionAssert.AreEqual(new[] { "written:b,a", "changed:b,a" }, observed);

                observed.Clear();
                store.Delete(gameId);
                CollectionAssert.AreEqual(new[] { "changed:", "feed::" + gameId }, observed);
            });
        }

        [TestMethod]
        public void ConcurrentEditsToTwoGames_KeepEveryEdit()
        {
            WithDirectory(dir =>
            {
                var store = new GameCustomDataStore(dir);
                var games = new[] { Guid.NewGuid(), Guid.NewGuid() };
                Task.WaitAll(games.Select(gameId => Task.Run(() =>
                {
                    for (var i = 0; i < 25; i++)
                    {
                        var name = "a" + i;
                        store.Update(gameId, data => data.AchievementOrder = (data.AchievementOrder ?? new List<string>()).Concat(new[] { name }).ToList());
                    }
                })).ToArray());

                var reloaded = new GameCustomDataStore(dir);
                foreach (var gameId in games)
                {
                    CollectionAssert.AreEqual(Enumerable.Range(0, 25).Select(i => "a" + i).ToList(), reloaded.LoadOrDefault(gameId).AchievementOrder);
                }
            });
        }

        [TestMethod]
        public void APackage_RoundTripsThroughTheTables()
        {
            WithDirectory(dir =>
            {
                var source = Guid.NewGuid();
                var target = Guid.NewGuid();
                var store = new GameCustomDataStore(dir);
                store.AttachManagedCustomIconService(new ManagedCustomIconService(new DiskImageService(logger: null, cacheRoot: dir), logger: null));
                // Icons are packaged from files; the field groups are what this checks.
                var record = Rich(source);
                record.AchievementOverrides["a1"].UnlockedIconPath = null;
                record.AchievementOverrides["a1"].LockedIconPath = null;
                record.AchievementCategoryImageOverrides = null;
                record.CustomAchievements[0].UnlockedIconPath = null;
                // An import keeps a custom provider only with the catalog attached.
                record.CustomProviderId = null;
                store.Save(source, record);

                var first = Path.Combine(dir, "first.pa");
                var second = Path.Combine(dir, "second.pa");
                store.ExportPortablePackage(source, first);
                store.ImportReplacePortable(target, first);
                store.ExportPortablePackage(target, second);

                Assert.AreEqual(Manifest(first), Manifest(second));
            });
        }

        /// <summary>The record as JSON with every object's properties in name order.</summary>
        private static string Canonical(GameCustomDataFile data) =>
            data == null ? string.Empty : Sorted(JToken.FromObject(data, JsonSerializer.Create(GameCustomLayer.JsonSettings))).ToString(Formatting.None);

        private static JToken Sorted(JToken token)
        {
            switch (token)
            {
                case JObject obj:
                    return new JObject(obj.Properties()
                        .OrderBy(property => property.Name, StringComparer.Ordinal)
                        .Select(property => new JProperty(property.Name, Sorted(property.Value))));
                case JArray array:
                    return new JArray(array.Select(Sorted));
                default:
                    return token;
            }
        }

        private static string Manifest(string packagePath)
        {
            using (var zip = ZipFile.OpenRead(packagePath))
            using (var reader = new StreamReader(zip.GetEntry(GameCustomDataStore.PortablePackageManifestEntryName).Open()))
            {
                var manifest = JObject.Parse(reader.ReadToEnd());
                manifest.Remove("PlayniteGameId");
                return manifest.ToString(Formatting.None);
            }
        }

        /// <summary>A record with a value in every field group.</summary>
        private static GameCustomDataFile Rich(Guid gameId) => new GameCustomDataFile
        {
            PlayniteGameId = gameId,
            ExcludedFromRefreshes = true,
            ExcludedFromSummaries = true,
            ExcludedFromMetadata = true,
            RuleClaims = new List<RuleClaim> { new RuleClaim { Field = "Exclusion", Written = "true", Prior = "false" } },
            UserPinnedFields = new List<string> { "Exclusion" },
            UseSeparateLockedIconsOverride = true,
            CapstonesMaterialized = true,
            Capstones = new List<CapstoneAssignment> { new CapstoneAssignment { ApiName = "a2" }, new CapstoneAssignment { ApiName = "custom:mine" } },
            AutoCapstoneGenerated = true,
            AchievementOrder = new List<string> { "a3", "a1", "custom:mine", "a2" },
            GoalAchievementApiNames = new List<string> { "a2", "a1" },
            FilteredAchievementApiNames = new List<string> { "a4" },
            SummaryFilteredAchievementApiNames = new List<string> { "a5", "a4" },
            AchievementCategoryKinds = new Dictionary<string, string> { ["Story"] = "Base", ["Extra/Part"] = "DLC", ["Old"] = "None" },
            AchievementCategoryOrder = new List<string> { "Extra/Part", "Story" },
            AchievementCategoryImageOverrides = new Dictionary<string, CategoryImageOverrideData> { ["Story"] = new CategoryImageOverrideData { Art = "C:/art/story.png" } },
            AchievementTags = new List<AchievementTagDefinition> { new AchievementTagDefinition { Key = "mine", Name = "Mine", Color = "#FF0000" } },
            GameSummaryCategory = new GameSummaryCategoryData { Label = "Story", ProviderLabel = "Story" },
            AchievementOverrides = new Dictionary<string, AchievementOverride>
            {
                ["a1"] = new AchievementOverride
                {
                    DisplayName = "First", Description = "Do it", Points = 5, TrophyType = "gold", Category = "Story", Tags = "Missable",
                    Hidden = true, Note = "line one\nline two", UnlockedIconPath = "C:/icons/a1.png", LockedIconPath = "C:/icons/a1.locked.png",
                    UnlockTimeUtc = Unlocked
                },
                ["a2"] = new AchievementOverride { ClearDescription = true, ClearTrophyType = true, ClearTags = true, ClearUnlockTime = true },
                ["a3"] = new AchievementOverride { Hidden = false, Note = "2025-01-01T00:00:00Z" },
                ["custom:mine"] = new AchievementOverride { DisplayName = "Renamed", Note = "authored note", UnlockTimeUtc = Unlocked.AddDays(1) }
            },
            ProviderOverride = new ProviderOverrideData { ProviderKey = "Steam", Value = "480" },
            MetadataLinks = new Dictionary<string, MetadataLinkData> { ["Exophase"] = new MetadataLinkData { Id = "some-game", Label = "Some Game", IsUserSet = true } },
            ManualLink = new ManualAchievementLink
            {
                SourceKey = "Steam", SourceGameId = "480",
                UnlockTimes = new Dictionary<string, DateTime?> { ["a1"] = Unlocked },
                UnlockStates = new Dictionary<string, bool> { ["a1"] = true },
                CreatedUtc = Unlocked, LastModifiedUtc = Unlocked
            },
            CustomAchievements = new List<CustomAchievementDefinition>
            {
                new CustomAchievementDefinition
                {
                    Id = "mine", DisplayName = "Mine", Description = "Mine to do", Unlocked = true, UnlockTimeUtc = Unlocked, Points = 3,
                    Category = "Extra/Part", Tags = "Missable", TrophyType = "bronze", Rarity = "Rare", GlobalPercentUnlocked = 4.5,
                    ProgressNum = 1, ProgressDenom = 2, UnlockedIconPath = "C:/icons/mine.png"
                },
                new CustomAchievementDefinition { Id = "earlier", DisplayName = "Earlier", Hidden = true, IsCapstone = true, CategoryKind = "Base" }
            },
            CustomProviderId = "my-provider",
            LinkedFile = new LinkedFileLink { FilePath = "C:/saves/progress.json", Schema = new LinkedFileSchema { Name = "Progress", MatchColumn = "id" }, LastReadUtc = Unlocked, LastReadStatus = "Read" },
            UnlockSounds = new UnlockSoundSettings(),
            NotificationBehaviorOverride = new GameNotificationBehaviorOverride(),
            CaptureOverride = new GameCaptureOverride()
        };

        private static void WithDirectory(Action<string> test)
        {
            var dir = Path.Combine(Path.GetTempPath(), "PlayniteAchievementsTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                test(dir);
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }
}
