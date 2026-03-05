using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using PlayniteAchievements.Models.Settings;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PlayniteAchievements.Services.Tests
{
    [TestClass]
    public class LegacyManualLinkImporterTests
    {
        [TestMethod]
        public void Import_ImportsEligibleSteamManualRecord()
        {
            var gameId = Guid.NewGuid();
            var tempDir = CreateTempDirectory();

            try
            {
                WriteLegacyFile(
                    tempDir,
                    gameId,
                    CreateLegacyPayload(
                        isManual: true,
                        isIgnored: false,
                        sourceName: "Steam",
                        sourceUrl: "https://steamcommunity.com/stats/2084000/achievements",
                        items: new[]
                        {
                            CreateItem("ach_one", "2025-09-10T22:12:00-04:00")
                        }));

                var settings = new PersistedSettings();
                var existingGames = new HashSet<Guid> { gameId };
                var importer = CreateImporter(settings, existingGames, new HashSet<Guid>());

                var result = importer.Import(tempDir);

                Assert.AreEqual(1, result.Scanned);
                Assert.AreEqual(1, result.Imported);
                Assert.AreEqual(1, result.ImportedGameIds.Count);
                Assert.AreEqual(gameId, result.ImportedGameIds[0]);
                Assert.IsTrue(settings.ManualAchievementLinks.ContainsKey(gameId));

                var link = settings.ManualAchievementLinks[gameId];
                Assert.AreEqual("Steam", link.SourceKey);
                Assert.AreEqual("2084000", link.SourceGameId);
                Assert.IsTrue(link.UnlockTimes.ContainsKey("ach_one"));
                Assert.IsTrue(link.UnlockStates.ContainsKey("ach_one"));
                Assert.IsTrue(link.UnlockStates["ach_one"]);
                Assert.AreEqual(
                    DateTimeOffset.Parse("2025-09-11T02:12:00Z").UtcDateTime,
                    link.UnlockTimes["ach_one"].Value);
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public void Import_SkipsNonManualAndIgnored()
        {
            var nonManualGameId = Guid.NewGuid();
            var ignoredGameId = Guid.NewGuid();
            var tempDir = CreateTempDirectory();

            try
            {
                WriteLegacyFile(
                    tempDir,
                    nonManualGameId,
                    CreateLegacyPayload(
                        isManual: false,
                        isIgnored: false,
                        sourceName: "Steam",
                        sourceUrl: "https://steamcommunity.com/stats/100/achievements",
                        items: new[] { CreateItem("ach") }));

                WriteLegacyFile(
                    tempDir,
                    ignoredGameId,
                    CreateLegacyPayload(
                        isManual: true,
                        isIgnored: true,
                        sourceName: "Steam",
                        sourceUrl: "https://steamcommunity.com/stats/200/achievements",
                        items: new[] { CreateItem("ach") }));

                var settings = new PersistedSettings();
                var existingGames = new HashSet<Guid> { nonManualGameId, ignoredGameId };
                var importer = CreateImporter(settings, existingGames, new HashSet<Guid>());

                var result = importer.Import(tempDir);

                Assert.AreEqual(2, result.Scanned);
                Assert.AreEqual(0, result.Imported);
                Assert.AreEqual(1, result.SkippedNotManual);
                Assert.AreEqual(1, result.SkippedIgnored);
                Assert.AreEqual(0, settings.ManualAchievementLinks.Count);
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public void Import_SkipsWhenManualLinkAlreadyExists()
        {
            var gameId = Guid.NewGuid();
            var tempDir = CreateTempDirectory();

            try
            {
                WriteLegacyFile(
                    tempDir,
                    gameId,
                    CreateLegacyPayload(
                        isManual: true,
                        isIgnored: false,
                        sourceName: "Steam",
                        sourceUrl: "https://steamcommunity.com/stats/300/achievements",
                        items: new[] { CreateItem("ach") }));

                var settings = new PersistedSettings();
                settings.ManualAchievementLinks[gameId] = new ManualAchievementLink
                {
                    SourceKey = "Steam",
                    SourceGameId = "999"
                };

                var importer = CreateImporter(settings, new HashSet<Guid> { gameId }, new HashSet<Guid>());

                var result = importer.Import(tempDir);

                Assert.AreEqual(0, result.Imported);
                Assert.AreEqual(1, result.SkippedManualLinkExists);
                Assert.AreEqual("999", settings.ManualAchievementLinks[gameId].SourceGameId);
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public void Import_ReplacesNullManualLinkEntry()
        {
            var gameId = Guid.NewGuid();
            var tempDir = CreateTempDirectory();

            try
            {
                WriteLegacyFile(
                    tempDir,
                    gameId,
                    CreateLegacyPayload(
                        isManual: true,
                        isIgnored: false,
                        sourceName: "Steam",
                        sourceUrl: "https://steamcommunity.com/stats/301/achievements",
                        items: new[] { CreateItem("ach") }));

                var settings = new PersistedSettings();
                settings.ManualAchievementLinks[gameId] = null;

                var importer = CreateImporter(settings, new HashSet<Guid> { gameId }, new HashSet<Guid>());
                var result = importer.Import(tempDir);

                Assert.AreEqual(1, result.Imported);
                Assert.AreEqual(0, result.SkippedManualLinkExists);
                Assert.IsTrue(settings.ManualAchievementLinks.ContainsKey(gameId));
                Assert.IsNotNull(settings.ManualAchievementLinks[gameId]);
                Assert.AreEqual("Steam", settings.ManualAchievementLinks[gameId].SourceKey);
                Assert.AreEqual("301", settings.ManualAchievementLinks[gameId].SourceGameId);
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public void Import_ReplacesMalformedManualLinkEntry()
        {
            var gameId = Guid.NewGuid();
            var tempDir = CreateTempDirectory();

            try
            {
                WriteLegacyFile(
                    tempDir,
                    gameId,
                    CreateLegacyPayload(
                        isManual: true,
                        isIgnored: false,
                        sourceName: "Steam",
                        sourceUrl: "https://steamcommunity.com/stats/302/achievements",
                        items: new[] { CreateItem("ach") }));

                var settings = new PersistedSettings();
                settings.ManualAchievementLinks[gameId] = new ManualAchievementLink
                {
                    SourceKey = "Steam",
                    SourceGameId = ""
                };

                var importer = CreateImporter(settings, new HashSet<Guid> { gameId }, new HashSet<Guid>());
                var result = importer.Import(tempDir);

                Assert.AreEqual(1, result.Imported);
                Assert.AreEqual(0, result.SkippedManualLinkExists);
                Assert.AreEqual("302", settings.ManualAchievementLinks[gameId].SourceGameId);
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public void Import_UsesLatestPersistedSettingsInstance()
        {
            var gameId = Guid.NewGuid();
            var tempDir = CreateTempDirectory();

            try
            {
                WriteLegacyFile(
                    tempDir,
                    gameId,
                    CreateLegacyPayload(
                        isManual: true,
                        isIgnored: false,
                        sourceName: "Steam",
                        sourceUrl: "https://steamcommunity.com/stats/303/achievements",
                        items: new[] { CreateItem("ach") }));

                var originalPersisted = new PersistedSettings();
                originalPersisted.ManualAchievementLinks[gameId] = new ManualAchievementLink
                {
                    SourceKey = "Steam",
                    SourceGameId = "already-linked"
                };

                var latestPersisted = new PersistedSettings();
                var importer = new LegacyManualLinkImporter(
                    () => latestPersisted,
                    gameIdValue => gameIdValue == gameId,
                    _ => false,
                    logger: null);

                var result = importer.Import(tempDir);

                Assert.AreEqual(1, result.Imported);
                Assert.AreEqual(0, result.SkippedManualLinkExists);
                Assert.IsTrue(originalPersisted.ManualAchievementLinks.ContainsKey(gameId));
                Assert.IsTrue(latestPersisted.ManualAchievementLinks.ContainsKey(gameId));
                Assert.AreEqual("303", latestPersisted.ManualAchievementLinks[gameId].SourceGameId);
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public void Import_SkipsWhenCachedProviderDataExists()
        {
            var gameId = Guid.NewGuid();
            var tempDir = CreateTempDirectory();

            try
            {
                WriteLegacyFile(
                    tempDir,
                    gameId,
                    CreateLegacyPayload(
                        isManual: true,
                        isIgnored: false,
                        sourceName: "Steam",
                        sourceUrl: "https://steamcommunity.com/stats/400/achievements",
                        items: new[] { CreateItem("ach") }));

                var settings = new PersistedSettings();
                var importer = CreateImporter(
                    settings,
                    new HashSet<Guid> { gameId },
                    new HashSet<Guid> { gameId });

                var result = importer.Import(tempDir);

                Assert.AreEqual(0, result.Imported);
                Assert.AreEqual(1, result.SkippedCachedProviderData);
                Assert.AreEqual(0, settings.ManualAchievementLinks.Count);
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public void Import_SkipsUnknownSourceAndReportsBreakdown()
        {
            var gameId = Guid.NewGuid();
            var tempDir = CreateTempDirectory();

            try
            {
                WriteLegacyFile(
                    tempDir,
                    gameId,
                    CreateLegacyPayload(
                        isManual: true,
                        isIgnored: false,
                        sourceName: "UnknownPlatform",
                        sourceUrl: "https://example.com/stats/500/achievements",
                        items: new[] { CreateItem("ach") }));

                var settings = new PersistedSettings();
                var importer = CreateImporter(settings, new HashSet<Guid> { gameId }, new HashSet<Guid>());

                var result = importer.Import(tempDir);

                Assert.AreEqual(0, result.Imported);
                Assert.AreEqual(1, result.SkippedUnsupportedSource);
                Assert.IsTrue(result.UnsupportedSources.ContainsKey("UnknownPlatform"));
                Assert.AreEqual(1, result.UnsupportedSources["UnknownPlatform"]);
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public void Import_ExtractsSteamAppIdFromStatsUrlAndFallbackIconUrl()
        {
            var gameIdFromStatsUrl = Guid.NewGuid();
            var gameIdFromIconUrl = Guid.NewGuid();
            var tempDir = CreateTempDirectory();

            try
            {
                WriteLegacyFile(
                    tempDir,
                    gameIdFromStatsUrl,
                    CreateLegacyPayload(
                        isManual: true,
                        isIgnored: false,
                        sourceName: "Steam",
                        sourceUrl: "https://steamcommunity.com/profiles/12345/stats/1111?tab=achievements",
                        items: new[] { CreateItem("ach_stats") }));

                WriteLegacyFile(
                    tempDir,
                    gameIdFromIconUrl,
                    CreateLegacyPayload(
                        isManual: true,
                        isIgnored: false,
                        sourceName: "Steam",
                        sourceUrl: "https://steamcommunity.com/id/someone/achievements",
                        items: new[]
                        {
                            CreateItem(
                                "ach_icon",
                                urlUnlocked: "https://steamcdn-a.akamaihd.net/steamcommunity/public/images/apps/2222/icon.jpg")
                        }));

                var settings = new PersistedSettings();
                var importer = CreateImporter(
                    settings,
                    new HashSet<Guid> { gameIdFromStatsUrl, gameIdFromIconUrl },
                    new HashSet<Guid>());

                var result = importer.Import(tempDir);

                Assert.AreEqual(2, result.Imported);
                Assert.AreEqual("1111", settings.ManualAchievementLinks[gameIdFromStatsUrl].SourceGameId);
                Assert.AreEqual("2222", settings.ManualAchievementLinks[gameIdFromIconUrl].SourceGameId);
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public void Import_ImportsParseableDateUnlockedAndIgnoresInvalidDate()
        {
            var gameId = Guid.NewGuid();
            var tempDir = CreateTempDirectory();

            try
            {
                WriteLegacyFile(
                    tempDir,
                    gameId,
                    CreateLegacyPayload(
                        isManual: true,
                        isIgnored: false,
                        sourceName: "Steam",
                        sourceUrl: "https://steamcommunity.com/stats/7777/achievements",
                        items: new[]
                        {
                            CreateItem("valid_ach", "2024-12-04T23:42:00-05:00"),
                            CreateItem("invalid_ach", "not-a-date")
                        }));

                var settings = new PersistedSettings();
                var importer = CreateImporter(settings, new HashSet<Guid> { gameId }, new HashSet<Guid>());

                var result = importer.Import(tempDir);

                Assert.AreEqual(1, result.Imported);
                var link = settings.ManualAchievementLinks[gameId];
                Assert.IsTrue(link.UnlockTimes.ContainsKey("valid_ach"));
                Assert.IsFalse(link.UnlockTimes.ContainsKey("invalid_ach"));
                Assert.IsTrue(link.UnlockStates.ContainsKey("valid_ach"));
                Assert.IsTrue(link.UnlockStates["valid_ach"]);
                Assert.IsFalse(link.UnlockStates.ContainsKey("invalid_ach"));
                Assert.AreEqual(
                    DateTimeOffset.Parse("2024-12-05T04:42:00Z").UtcDateTime,
                    link.UnlockTimes["valid_ach"].Value);
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public void Import_TreatsLegacySentinelDateAsUnlockedWithNullDate()
        {
            var gameId = Guid.NewGuid();
            var tempDir = CreateTempDirectory();

            try
            {
                WriteLegacyFile(
                    tempDir,
                    gameId,
                    CreateLegacyPayload(
                        isManual: true,
                        isIgnored: false,
                        sourceName: "Steam",
                        sourceUrl: "https://steamcommunity.com/stats/8888/achievements",
                        items: new[]
                        {
                            CreateItem("sentinel", "1982-12-15T00:00:00-05:00"),
                            CreateItem("valid", "2024-12-04T23:42:00-05:00")
                        }));

                var settings = new PersistedSettings();
                var importer = CreateImporter(settings, new HashSet<Guid> { gameId }, new HashSet<Guid>());

                var result = importer.Import(tempDir);

                Assert.AreEqual(1, result.Imported);
                var link = settings.ManualAchievementLinks[gameId];
                Assert.IsTrue(link.UnlockStates.ContainsKey("sentinel"));
                Assert.IsTrue(link.UnlockStates["sentinel"]);
                Assert.IsFalse(link.UnlockTimes.ContainsKey("sentinel"));
                Assert.IsTrue(link.UnlockTimes.ContainsKey("valid"));
                Assert.IsTrue(link.UnlockStates.ContainsKey("valid"));
                Assert.IsTrue(link.UnlockStates["valid"]);
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public void Import_ResultCountersAndImportedGameIdsAreDeterministic()
        {
            var importedA = Guid.Parse("00000000-0000-0000-0000-00000000000a");
            var importedB = Guid.Parse("00000000-0000-0000-0000-00000000000b");
            var missingGame = Guid.NewGuid();
            var unresolvedGame = Guid.NewGuid();
            var parseFailureGame = Guid.NewGuid();
            var tempDir = CreateTempDirectory();

            try
            {
                WriteLegacyFile(
                    tempDir,
                    importedB,
                    CreateLegacyPayload(
                        isManual: true,
                        isIgnored: false,
                        sourceName: "Steam",
                        sourceUrl: "https://steamcommunity.com/stats/9002/achievements",
                        items: new[] { CreateItem("ach_b") }));

                WriteLegacyFile(
                    tempDir,
                    importedA,
                    CreateLegacyPayload(
                        isManual: true,
                        isIgnored: false,
                        sourceName: "Steam",
                        sourceUrl: "https://steamcommunity.com/stats/9001/achievements",
                        items: new[] { CreateItem("ach_a") }));

                WriteLegacyFile(
                    tempDir,
                    missingGame,
                    CreateLegacyPayload(
                        isManual: true,
                        isIgnored: false,
                        sourceName: "Steam",
                        sourceUrl: "https://steamcommunity.com/stats/9010/achievements",
                        items: new[] { CreateItem("ach_missing") }));

                WriteLegacyFile(
                    tempDir,
                    unresolvedGame,
                    CreateLegacyPayload(
                        isManual: true,
                        isIgnored: false,
                        sourceName: "Steam",
                        sourceUrl: "https://steamcommunity.com/id/no-stats",
                        items: new[] { CreateItem("ach_unresolved") }));

                File.WriteAllText(Path.Combine(tempDir, "invalid-guid.json"),
                    CreateLegacyPayload(
                        isManual: true,
                        isIgnored: false,
                        sourceName: "Steam",
                        sourceUrl: "https://steamcommunity.com/stats/9011/achievements",
                        items: new[] { CreateItem("ach_invalid_name") }));

                File.WriteAllText(Path.Combine(tempDir, $"{parseFailureGame}.json"), "{not_json");

                var settings = new PersistedSettings();
                var existingGames = new HashSet<Guid> { importedA, importedB, unresolvedGame };
                var importer = CreateImporter(settings, existingGames, new HashSet<Guid>());

                var result = importer.Import(tempDir);

                Assert.AreEqual(6, result.Scanned);
                Assert.AreEqual(2, result.Imported);
                Assert.AreEqual(1, result.SkippedGameMissing);
                Assert.AreEqual(1, result.SkippedUnresolvedSourceGameId);
                Assert.AreEqual(1, result.SkippedInvalidFileName);
                Assert.AreEqual(1, result.ParseFailures);
                CollectionAssert.AreEqual(new[] { importedA, importedB }, result.ImportedGameIds);
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        private static LegacyManualLinkImporter CreateImporter(
            PersistedSettings settings,
            HashSet<Guid> existingGames,
            HashSet<Guid> cachedGames)
        {
            return new LegacyManualLinkImporter(
                settings,
                gameId => existingGames.Contains(gameId),
                gameId => cachedGames.Contains(gameId),
                logger: null);
        }

        private static string CreateTempDirectory()
        {
            var path = Path.Combine(
                Path.GetTempPath(),
                "PlayniteAchievementsTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        private static void DeleteDirectory(string path)
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }

        private static void WriteLegacyFile(string folderPath, Guid gameId, string json)
        {
            File.WriteAllText(Path.Combine(folderPath, $"{gameId}.json"), json);
        }

        private static JObject CreateItem(
            string apiName,
            string dateUnlocked = null,
            string urlUnlocked = null,
            string urlLocked = null)
        {
            var item = new JObject
            {
                ["ApiName"] = apiName
            };

            if (dateUnlocked != null)
            {
                item["DateUnlocked"] = dateUnlocked;
            }

            if (urlUnlocked != null)
            {
                item["UrlUnlocked"] = urlUnlocked;
            }

            if (urlLocked != null)
            {
                item["UrlLocked"] = urlLocked;
            }

            return item;
        }

        private static string CreateLegacyPayload(
            bool isManual,
            bool isIgnored,
            string sourceName,
            string sourceUrl,
            IEnumerable<JObject> items)
        {
            var payload = new JObject
            {
                ["IsManual"] = isManual,
                ["IsIgnored"] = isIgnored,
                ["SourcesLink"] = new JObject
                {
                    ["Name"] = sourceName,
                    ["Url"] = sourceUrl
                },
                ["Items"] = new JArray(items ?? Enumerable.Empty<JObject>())
            };

            return payload.ToString();
        }
    }
}
