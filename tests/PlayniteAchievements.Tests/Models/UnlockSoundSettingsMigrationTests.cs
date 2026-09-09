using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using PlayniteAchievements.Models.Settings;

namespace PlayniteAchievements.Models.Tests
{
    [TestClass]
    public class UnlockSoundSettingsMigrationTests
    {
        private string _root;
        private string _existingSound;

        [TestInitialize]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "PlayniteAchievementsTests", "UnlockSoundMigration", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _existingSound = Path.Combine(_root, "rare.wav");
            File.WriteAllBytes(_existingSound, new byte[] { 0 });
        }

        [TestCleanup]
        public void TearDown()
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (Exception)
            {
            }
        }

        [TestMethod]
        public void MigrateFromJson_SeedsVolumeSwitchAndExistingCustomPaths_WhenPackIsCustom()
        {
            var ups = WriteUpsConfig(new JObject
            {
                ["EnableAchievementSound"] = false,
                ["MusicVolume"] = 70,
                ["AchievementSoundPack"] = 2,
                ["RareAchievementSoundPath"] = _existingSound,
                ["CommonAchievementSoundPath"] = Path.Combine(_root, "missing.wav"),
            });

            var persisted = Migrate(@"{ ""Persisted"": { ""GlobalLanguage"": ""english"" } }", ups);

            Assert.IsTrue(persisted["UnlockSoundsSeededFromUniPlaySong"].Value<bool>());
            Assert.IsFalse(persisted["EnableUnlockSounds"].Value<bool>());
            Assert.AreEqual(70, persisted["UnlockSoundVolumePercent"].Value<int>());
            Assert.AreEqual(_existingSound, persisted["UnlockSounds"]["Rare"].Value<string>());
            Assert.IsNull(persisted["UnlockSounds"]["Common"]);
        }

        [TestMethod]
        public void MigrateFromJson_AcceptsThePackAsAnEnumName()
        {
            var ups = WriteUpsConfig(new JObject
            {
                ["AchievementSoundPack"] = "Custom",
                ["RareAchievementSoundPath"] = _existingSound,
            });

            var persisted = Migrate(@"{ ""Persisted"": { } }", ups);

            Assert.AreEqual(_existingSound, persisted["UnlockSounds"]["Rare"].Value<string>());
        }

        [TestMethod]
        public void MigrateFromJson_IgnoresCustomPathsWhenThePackWasNotCustom()
        {
            var ups = WriteUpsConfig(new JObject
            {
                ["MusicVolume"] = 40,
                ["AchievementSoundPack"] = 1,
                ["RareAchievementSoundPath"] = _existingSound,
            });

            var persisted = Migrate(@"{ ""Persisted"": { } }", ups);

            Assert.AreEqual(40, persisted["UnlockSoundVolumePercent"].Value<int>());
            Assert.IsNull(persisted["UnlockSounds"]);
        }

        [TestMethod]
        public void MigrateFromJson_ClampsVolumeAndKeepsAnExistingSlot()
        {
            var ups = WriteUpsConfig(new JObject
            {
                ["MusicVolume"] = 150,
                ["AchievementSoundPack"] = 2,
                ["RareAchievementSoundPath"] = _existingSound,
            });

            var persisted = Migrate(
                @"{ ""Persisted"": { ""UnlockSounds"": { ""Rare"": ""C:\\mine\\rare.wav"" } } }",
                ups);

            Assert.AreEqual(100, persisted["UnlockSoundVolumePercent"].Value<int>());
            Assert.AreEqual(@"C:\mine\rare.wav", persisted["UnlockSounds"]["Rare"].Value<string>());
        }

        [TestMethod]
        public void MigrateFromJson_OnlyFlipsTheFlag_WhenUniPlaySongConfigIsAbsent()
        {
            var persisted = Migrate(@"{ ""Persisted"": { } }", Path.Combine(_root, "nope", "config.json"));

            Assert.IsTrue(persisted["UnlockSoundsSeededFromUniPlaySong"].Value<bool>());
            Assert.IsNull(persisted["UnlockSoundVolumePercent"]);
            Assert.IsNull(persisted["EnableUnlockSounds"]);
        }

        [TestMethod]
        public void MigrateFromJson_StillFlipsTheFlag_WhenUniPlaySongConfigIsMalformed()
        {
            var ups = Path.Combine(_root, "config.json");
            File.WriteAllText(ups, "{ not json");

            var persisted = Migrate(@"{ ""Persisted"": { } }", ups);

            Assert.IsTrue(persisted["UnlockSoundsSeededFromUniPlaySong"].Value<bool>());
        }

        [TestMethod]
        public void MigrateFromJson_IsANoOp_WhenTheFlagIsAlreadySet()
        {
            var ups = WriteUpsConfig(new JObject { ["MusicVolume"] = 70 });
            const string json = @"{ ""Persisted"": { ""UnlockSoundsSeededFromUniPlaySong"": true, ""UnlockSoundVolumePercent"": 15 } }";

            var migrated = UnlockSoundSettingsMigration.MigrateFromJson(json, ups);

            Assert.AreSame(json, migrated);
        }

        [TestMethod]
        public void GetUniPlaySongConfigPath_UsesThePluginIdFolder()
        {
            Assert.IsNull(UnlockSoundSettingsMigration.GetUniPlaySongConfigPath(null));
            Assert.AreEqual(
                @"C:\Playnite\ExtensionsData\a1b2c3d4-e5f6-7890-abcd-ef1234567890\config.json",
                UnlockSoundSettingsMigration.GetUniPlaySongConfigPath(@"C:\Playnite\ExtensionsData"));
        }

        private string WriteUpsConfig(JObject config)
        {
            var path = Path.Combine(_root, "ups", "config.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, config.ToString());
            return path;
        }

        private static JObject Migrate(string json, string upsConfigPath)
        {
            return (JObject)JObject.Parse(UnlockSoundSettingsMigration.MigrateFromJson(json, upsConfigPath))["Persisted"];
        }
    }
}
