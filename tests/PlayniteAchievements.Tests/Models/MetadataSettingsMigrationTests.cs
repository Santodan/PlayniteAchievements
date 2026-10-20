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
            Assert.AreEqual("Override", SteamMode(@"{ ""SteamHuntersCategoryMode"": ""Override"" }"));
            Assert.AreEqual("Merge", SteamMode(@"{ ""UseSteamHuntersForCategories"": true }"));
            Assert.IsNull(SteamMode(@"{ ""UseSteamHuntersForCategories"": false }"));
            // Merge was the setting's default, so a config that never saved it keeps SteamHunters.
            Assert.AreEqual("Merge", SteamMode(@"{ }"));
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

        private static string SteamMode(string steamJson)
        {
            var persisted = Migrate(@"{ ""ProviderSettings"": { ""Steam"": " + steamJson + " } }");
            var steam = persisted["MetadataPlatformChoices"]?["Steam"];
            if (steam == null)
            {
                return null;
            }

            Assert.AreEqual("SteamHunters", (string)steam["Categories"]);
            return (string)steam["CategoryMode"];
        }

        private static JObject Migrate(string persistedJson)
        {
            var migrated = MetadataSettingsMigration.MigrateFromJson(@"{ ""Persisted"": " + persistedJson + " }");
            return (JObject)JObject.Parse(migrated)["Persisted"];
        }
    }
}
