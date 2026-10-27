using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using PlayniteAchievements.Models.Settings;

namespace PlayniteAchievements.Tests.Models
{
    [TestClass]
    public class MetadataSettingsMigrationTests
    {
        [TestMethod]
        public void ExophaseToggles_BecomeRarityChoices()
        {
            var persisted = Migrate(@"{ ""ProviderSettings"": {
                ""Xbox"": { ""UseExophaseForRarity"": true },
                ""Xenia"": { ""UseExophaseForRarity"": false },
                ""Ubisoft"": { ""UseExophaseForRarity"": true },
                ""Steam"": { ""SteamHuntersCategoryMode"": ""Off"" } } }");

            var platforms = (JObject)persisted["MetadataPlatformChoices"];
            Assert.AreEqual("Exophase", (string)platforms["Xbox"]["Rarity"]);
            Assert.AreEqual("Exophase", (string)platforms["Ubisoft"]["Rarity"]);
            // Off, or never saved, keeps the platform's own rarity under the Exophase default.
            Assert.AreEqual(string.Empty, (string)platforms["Xenia"]["Rarity"]);
            Assert.AreEqual(string.Empty, (string)platforms["EA"]["Rarity"]);
            Assert.IsNull(platforms["Steam"]);
            Assert.IsTrue((bool)persisted["MetadataChoicesSeeded"]);
        }

        [TestMethod]
        public void SteamHuntersMode_BecomesSteamCategoryChoice()
        {
            Assert.AreEqual("SteamHunters", SteamCategories(@"{ ""SteamHuntersCategoryMode"": ""Override"" }"));
            Assert.AreEqual("SteamHunters", SteamCategories(@"{ ""SteamHuntersCategoryMode"": ""Merge"" }"));
            Assert.IsNull(SteamCategories(@"{ ""SteamHuntersCategoryMode"": ""Off"" }"));
            Assert.AreEqual("SteamHunters", SteamCategories(@"{ ""UseSteamHuntersForCategories"": true }"));
            Assert.IsNull(SteamCategories(@"{ ""UseSteamHuntersForCategories"": false }"));
            // Merge was the setting's default, so a config that never saved it keeps SteamHunters.
            Assert.AreEqual("SteamHunters", SteamCategories(@"{ }"));
        }

        [TestMethod]
        public void CategoryChoices_LeaveTagsToTheTrueGamingDefault()
        {
            const string json = @"{ ""Persisted"": {
                ""MetadataChoicesSeeded"": true,
                ""MetadataPlatformChoices"": { ""Steam"": { ""Categories"": ""SteamHunters"" } } } }";

            Assert.AreEqual(json, MetadataSettingsMigration.MigrateFromJson(json), "nothing is copied into Tags");
            var settings = new PersistedSettings
            {
                MetadataPlatformChoices = new System.Collections.Generic.Dictionary<string, MetadataChoices>
                {
                    ["Steam"] = new MetadataChoices { Categories = MetadataChoices.SteamHunters }
                }
            };
            Assert.AreEqual(MetadataChoices.TrueGaming, settings.ResolveMetadataChoices("Steam").Tags);
        }

        [TestMethod]
        public void ExistingChoices_AreKept_AndTheSeedRunsOnce()
        {
            const string json = @"{ ""Persisted"": {
                ""MetadataPlatformChoices"": { ""Xbox"": { ""Rarity"": """" } },
                ""ProviderSettings"": { ""Xbox"": { ""UseExophaseForRarity"": true } } } }";

            var migrated = MetadataSettingsMigration.MigrateFromJson(json);
            var persisted = (JObject)JObject.Parse(migrated)["Persisted"];
            Assert.AreEqual("", (string)persisted["MetadataPlatformChoices"]["Xbox"]["Rarity"]);
            Assert.AreEqual(migrated, MetadataSettingsMigration.MigrateFromJson(migrated));
        }

        [TestMethod]
        public void TrueGamingCategories_BecomeSteamHuntersOnTheDefaults_AndDefaultOnPlatforms()
        {
            var persisted = Migrate(@"{
                ""MetadataChoicesSeeded"": true,
                ""MetadataDefaults"": { ""Categories"": ""TrueGaming"", ""Tags"": ""TrueGaming"" },
                ""MetadataPlatformChoices"": {
                    ""Xbox"": { ""Categories"": ""TrueGaming"", ""Rarity"": """" },
                    ""Steam"": { ""Categories"": ""SteamHunters"" } } }");

            Assert.AreEqual("SteamHunters", (string)persisted["MetadataDefaults"]["Categories"]);
            Assert.AreEqual("TrueGaming", (string)persisted["MetadataDefaults"]["Tags"], "tags keep TrueGaming");
            Assert.IsNull(persisted["MetadataPlatformChoices"]["Xbox"]["Categories"]);
            Assert.AreEqual("", (string)persisted["MetadataPlatformChoices"]["Xbox"]["Rarity"]);
            Assert.AreEqual("SteamHunters", (string)persisted["MetadataPlatformChoices"]["Steam"]["Categories"]);
        }

        private static string SteamCategories(string steamJson)
        {
            var persisted = Migrate(@"{ ""ProviderSettings"": { ""Steam"": " + steamJson + " } }");
            var steam = persisted["MetadataPlatformChoices"]?["Steam"];
            Assert.IsNull(steam?["CategoryMode"]);
            return (string)steam?["Categories"];
        }

        private static JObject Migrate(string persistedJson)
        {
            var migrated = MetadataSettingsMigration.MigrateFromJson(@"{ ""Persisted"": " + persistedJson + " }");
            return (JObject)JObject.Parse(migrated)["Persisted"];
        }
    }
}
