using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using PlayniteAchievements.Models.Settings;

namespace PlayniteAchievements.Models.Tests
{
    [TestClass]
    public class CaptureRaritySettingsMigrationTests
    {
        [TestMethod]
        public void MigrateFromJson_SeedsPerVariantThresholds_FromLegacySharedValues()
        {
            // A config that predates the per-variant split: the shared screenshot threshold was
            // raised to Rare (2) with the completion bypass turned off.
            const string json =
                @"{
                    ""Persisted"": {
                        ""UnlockScreenshotMinimumRarity"": 2,
                        ""UnlockScreenshotAlwaysCaptureCompletion"": false
                    }
                }";

            var persisted = MigratePersisted(json);

            foreach (var key in new[]
            {
                "UnlockScreenshotCleanMinimumRarity",
                "UnlockScreenshotWithToastMinimumRarity",
                "UnlockScreenshotFramedMinimumRarity"
            })
            {
                Assert.AreEqual(2, persisted[key].Value<int>(), key);
            }

            foreach (var key in new[]
            {
                "UnlockScreenshotCleanAlwaysCaptureCompletion",
                "UnlockScreenshotWithToastAlwaysCaptureCompletion",
                "UnlockScreenshotFramedAlwaysCaptureCompletion"
            })
            {
                Assert.IsFalse(persisted[key].Value<bool>(), key);
            }

            // Legacy keys are removed so the migration is a no-op on re-run.
            Assert.IsNull(persisted["UnlockScreenshotMinimumRarity"]);
            Assert.IsNull(persisted["UnlockScreenshotAlwaysCaptureCompletion"]);
        }

        [TestMethod]
        public void MigrateFromJson_DoesNotOverwriteExistingPerVariantValues()
        {
            // A partially-migrated config where the user already customized the clean variant.
            const string json =
                @"{
                    ""Persisted"": {
                        ""UnlockScreenshotMinimumRarity"": 3,
                        ""UnlockScreenshotCleanMinimumRarity"": 1
                    }
                }";

            var persisted = MigratePersisted(json);

            Assert.AreEqual(1, persisted["UnlockScreenshotCleanMinimumRarity"].Value<int>());
            Assert.AreEqual(3, persisted["UnlockScreenshotWithToastMinimumRarity"].Value<int>());
            Assert.AreEqual(3, persisted["UnlockScreenshotFramedMinimumRarity"].Value<int>());
        }

        [TestMethod]
        public void MigrateFromJson_IsNoOp_WhenNoLegacyKeys()
        {
            const string json = @"{ ""Persisted"": { ""GlobalLanguage"": ""english"" } }";

            var result = CaptureRaritySettingsMigration.MigrateFromJson(json);

            // Unchanged input is returned verbatim (no rewrite).
            Assert.AreEqual(json, result);
        }

        private static JObject MigratePersisted(string json)
        {
            return (JObject)JObject.Parse(CaptureRaritySettingsMigration.MigrateFromJson(json))["Persisted"];
        }
    }
}
