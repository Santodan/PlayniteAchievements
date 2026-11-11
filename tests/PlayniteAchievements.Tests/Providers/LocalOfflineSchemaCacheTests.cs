using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using PlayniteAchievements.Providers.Local;
using PlayniteAchievements.Providers.Steam.Models;
using PlayniteAchievements.Services.InGameMonitoring;

namespace PlayniteAchievements.Tests.Providers
{
    [TestClass]
    public class LocalOfflineSchemaCacheTests
    {
        private string _directory;

        [TestInitialize]
        public void Initialize() => _directory = Path.Combine(Path.GetTempPath(), "LocalOfflineSchemaTests", Guid.NewGuid().ToString("N"));

        [TestCleanup]
        public void Cleanup()
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
        }

        private static SchemaAndPercentages Schema() => new SchemaAndPercentages
        {
            Achievements = new List<SchemaAchievement>
            {
                new SchemaAchievement
                {
                    Name = "ACH_FIRST", DisplayName = "First", Description = "Collect 10", Hidden = 1,
                    Icon = "https://example.invalid/icon.jpg", IconGray = "locked.png",
                    InternalKey = 7, GroupId = 2, Archived = true, MinProgress = 0, MaxProgress = 10,
                    GlobalPercent = 12.5, Points = 20
                },
                new SchemaAchievement { Name = "ACH_LOCKED", DisplayName = "Still locked" }
            },
            GlobalPercentages = new Dictionary<string, double> { ["ACH_FIRST"] = 12.5 },
            Groups = new Dictionary<int, SchemaAchievementGroup>
            {
                [2] = new SchemaAchievementGroup { GroupId = 2, Name = "DLC", DlcAppId = 456, Archived = true }
            },
            SchemaHash = 987
        };

        [TestMethod]
        public void RestartRetainsCompleteDefinitionsAndInternalMetadata()
        {
            new LocalOfflineSchemaCache(_directory).Save(123, "german", LocalSteamSchemaPreference.PreferSteam, Schema());
            var loaded = new LocalOfflineSchemaCache(_directory).Load(123, "german", LocalSteamSchemaPreference.PreferSteam);
            Assert.IsNotNull(loaded);
            Assert.AreEqual(2, loaded.Achievements.Count);
            var row = loaded.Achievements[0];
            Assert.AreEqual("First", row.DisplayName);
            Assert.AreEqual("Collect 10", row.Description);
            Assert.AreEqual(1, row.Hidden);
            Assert.AreEqual("locked.png", row.IconGray);
            Assert.AreEqual(7, row.InternalKey);
            Assert.AreEqual(2, row.GroupId);
            Assert.IsTrue(row.Archived);
            Assert.AreEqual(10d, row.MaxProgress);
            Assert.AreEqual(20, row.Points);
            Assert.AreEqual(12.5, loaded.GlobalPercentages["ACH_FIRST"]);
            Assert.AreEqual("DLC", loaded.Groups[2].Name);
            Assert.AreEqual(456, loaded.Groups[2].DlcAppId);
            Assert.AreEqual(987L, loaded.SchemaHash);
        }

        [TestMethod]
        public void CacheIsSeparatedByAppLanguageAndPreference()
        {
            var store = new LocalOfflineSchemaCache(_directory);
            store.Save(123, "german", LocalSteamSchemaPreference.PreferSteam, Schema());
            Assert.IsNull(store.Load(456, "german", LocalSteamSchemaPreference.PreferSteam));
            Assert.IsNull(store.Load(123, "english", LocalSteamSchemaPreference.PreferSteam));
            Assert.IsNull(store.Load(123, "german", LocalSteamSchemaPreference.PreferSteamHunters));
            Assert.IsNotNull(store.Load(123, "GERMAN", LocalSteamSchemaPreference.PreferSteam));
        }

        [TestMethod]
        public void PerGameCustomRowsAndIconResolutionCannotMutateSavedDefinitions()
        {
            var store = new LocalOfflineSchemaCache(_directory);
            var original = Schema();
            store.Save(123, "english", LocalSteamSchemaPreference.PreferSteam, original);
            original.Achievements.Clear();
            var loaded = store.Load(123, "english", LocalSteamSchemaPreference.PreferSteam);
            loaded.Achievements[0].Icon = "per-game-icon.png";
            var custom = loaded.WithAchievements(new List<SchemaAchievement> { new SchemaAchievement { Name = "CUSTOM" } });
            var nextGame = store.Load(123, "english", LocalSteamSchemaPreference.PreferSteam);
            Assert.AreEqual(1, custom.Achievements.Count);
            Assert.AreEqual(2, nextGame.Achievements.Count);
            Assert.AreEqual("https://example.invalid/icon.jpg", nextGame.Achievements[0].Icon);
        }

        [TestMethod]
        public void MissingCorruptOrUnsupportedCacheDoesNotProduceDefinitions()
        {
            var store = new LocalOfflineSchemaCache(_directory);
            Assert.IsNull(store.Load(123, "english", LocalSteamSchemaPreference.PreferSteam));
            store.Save(123, "english", LocalSteamSchemaPreference.PreferSteam, Schema());
            var path = Directory.GetFiles(Path.Combine(_directory, "local_schema_cache"), "*.json")[0];
            File.WriteAllText(path, "broken json");
            Assert.IsNull(store.Load(123, "english", LocalSteamSchemaPreference.PreferSteam));
            File.Delete(path);
            store.Save(123, "english", LocalSteamSchemaPreference.PreferSteam, Schema());
            File.WriteAllText(path, File.ReadAllText(path).Replace("\"Version\":1", "\"Version\":99"));
            Assert.IsNull(store.Load(123, "english", LocalSteamSchemaPreference.PreferSteam));
        }

        [TestMethod]
        public void EmptyOrInvalidRefreshDoesNotReplaceGoodCache()
        {
            var store = new LocalOfflineSchemaCache(_directory);
            store.Save(123, "english", LocalSteamSchemaPreference.PreferSteam, Schema());
            store.Save(123, "english", LocalSteamSchemaPreference.PreferSteam, new SchemaAndPercentages());
            var invalid = Schema();
            invalid.Achievements.Add(new SchemaAchievement { Name = "ach_first" });
            store.Save(123, "english", LocalSteamSchemaPreference.PreferSteam, invalid);
            Assert.AreEqual(2, store.Load(123, "english", LocalSteamSchemaPreference.PreferSteam).Achievements.Count);
        }

        [TestMethod]
        public void NameResolvedIdentitySurvivesRestartButNotGameOrLocalIdChanges()
        {
            var game = Guid.NewGuid();
            var store = new LocalOfflineSchemaCache(_directory);
            store.SaveResolvedAppId(game, "Game", 42, 123);
            store = new LocalOfflineSchemaCache(_directory);
            Assert.AreEqual(123, store.LoadResolvedAppId(game, "Game", 42));
            Assert.AreEqual(0, store.LoadResolvedAppId(Guid.NewGuid(), "Game", 42));
            Assert.AreEqual(0, store.LoadResolvedAppId(game, "Other game", 42));
            Assert.AreEqual(0, store.LoadResolvedAppId(game, "Game", 43));
        }

        [TestMethod]
        public void SettingsAreEnabledForExistingUsersAndEditsRemainTransactional()
        {
            var original = JsonConvert.DeserializeObject<LocalSettings>("{}");
            Assert.IsTrue(original.UseOfflineCachedSchemas);
            Assert.IsTrue(original.SkipUnavailableSchemaNetwork);
            var before = SantodanPollingConfiguration.Build(original, Guid.Empty, null, null, null, null, false);
            var edit = (LocalSettings)original.Clone();
            edit.UseOfflineCachedSchemas = false;
            edit.SkipUnavailableSchemaNetwork = false;
            Assert.IsTrue(original.UseOfflineCachedSchemas);
            original.CopyFrom(edit);
            Assert.IsFalse(original.UseOfflineCachedSchemas);
            Assert.IsFalse(original.SkipUnavailableSchemaNetwork);
            Assert.AreNotEqual(before, SantodanPollingConfiguration.Build(original, Guid.Empty, null, null, null, null, false));
        }
    }
}
