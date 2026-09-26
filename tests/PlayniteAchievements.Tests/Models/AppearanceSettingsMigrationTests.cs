using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using PlayniteAchievements.Models.Settings;

namespace PlayniteAchievements.Models.Tests
{
    [TestClass]
    public class AppearanceSettingsMigrationTests
    {
        private const string GridSurfaceKey = "PlayAch.Brush.GridSurface";
        private const string ControlSurfaceKey = "PlayAch.Brush.ControlSurface";
        private const string WindowSurfaceKey = "PlayAch.Brush.WindowSurface";

        [TestMethod]
        public void MigrateFromJson_SeedsTransparentInlineSurfaces_WhenFlagAbsent()
        {
            // An existing config that predates the feature: no flag, no overrides.
            const string json = @"{ ""Persisted"": { ""GlobalLanguage"": ""english"" } }";

            var persisted = MigratePersisted(json);

            Assert.AreEqual(true, persisted["InlineSurfaceTransparencySeeded"].Value<bool>());
            AssertTransparent(persisted, GridSurfaceKey);
            AssertTransparent(persisted, ControlSurfaceKey);
            AssertTransparent(persisted, WindowSurfaceKey);
        }

        [TestMethod]
        public void MigrateFromJson_PreservesExistingOverrideValues()
        {
            // User had GridSurface customized; only the absent ControlSurface should be seeded.
            const string json =
                @"{
                    ""Persisted"": {
                        ""ResourceOverrides"": {
                            ""PlayAch.Brush.GridSurface"": { ""Mode"": 1, ""CustomValue"": ""#FF202830"" }
                        }
                    }
                }";

            var persisted = MigratePersisted(json);
            var overrides = (JObject)persisted["ResourceOverrides"];

            Assert.AreEqual(1, overrides[GridSurfaceKey]["Mode"].Value<int>());
            Assert.AreEqual("#FF202830", overrides[GridSurfaceKey]["CustomValue"].Value<string>());
            AssertTransparent(persisted, ControlSurfaceKey);
            Assert.AreEqual(true, persisted["InlineSurfaceTransparencySeeded"].Value<bool>());
        }

        [TestMethod]
        public void MigrateFromJson_DoesNotReseed_WhenFlagAlreadySet()
        {
            // After the one-time seed the flag is set; a surface the user later switched to
            // Follow Playnite (absent from the dict) must not be re-seeded.
            const string json =
                @"{
                    ""Persisted"": {
                        ""InlineSurfaceTransparencySeeded"": true,
                        ""CommonGlowTierCleared"": true,
                        ""ResourceOverrides"": {
                            ""PlayAch.Brush.ControlSurface"": { ""Mode"": 2, ""CustomValue"": ""#00000000"" }
                        }
                    }
                }";

            var result = AppearanceSettingsMigration.MigrateFromJson(json);

            // Unchanged input is returned verbatim (no rewrite).
            Assert.AreEqual(json, result);

            var persisted = (JObject)JObject.Parse(result)["Persisted"];
            var overrides = (JObject)persisted["ResourceOverrides"];
            Assert.IsNull(overrides[GridSurfaceKey]);
            Assert.IsNotNull(overrides[ControlSurfaceKey]);
        }

        [TestMethod]
        public void MigrateFromJson_ClearsCommonGlowBit_WhenFlagAbsent()
        {
            // The old soft default (every tier plus completion) and a hand-set ray selection.
            const string json =
                @"{ ""Persisted"": { ""RarityGlowSoftTiers"": 31, ""RarityGlowRayTiers"": 9 } }";

            var persisted = MigratePersisted(json);

            Assert.AreEqual(30, persisted["RarityGlowSoftTiers"].Value<int>());
            Assert.AreEqual(8, persisted["RarityGlowRayTiers"].Value<int>());
            Assert.AreEqual(true, persisted["CommonGlowTierCleared"].Value<bool>());
        }

        [TestMethod]
        public void MigrateFromJson_KeepsCommonGlowBit_WhenFlagAlreadySet()
        {
            // The user opted Common in after the one-time clear.
            const string json =
                @"{ ""Persisted"": { ""CommonGlowTierCleared"": true, ""RarityGlowSoftTiers"": 31 } }";

            var persisted = MigratePersisted(json);

            Assert.AreEqual(31, persisted["RarityGlowSoftTiers"].Value<int>());
        }

        [TestMethod]
        public void MigrateFromJson_OnlyStampsCommonGlowFlag_WhenTiersAbsent()
        {
            const string json = @"{ ""Persisted"": { ""GlobalLanguage"": ""english"" } }";

            var persisted = MigratePersisted(json);

            Assert.IsNull(persisted["RarityGlowSoftTiers"]);
            Assert.IsNull(persisted["RarityGlowRayTiers"]);
            Assert.AreEqual(true, persisted["CommonGlowTierCleared"].Value<bool>());
        }

        private static JObject MigratePersisted(string json)
        {
            return (JObject)JObject.Parse(AppearanceSettingsMigration.MigrateFromJson(json))["Persisted"];
        }

        private static void AssertTransparent(JObject persisted, string key)
        {
            var entry = ((JObject)persisted["ResourceOverrides"])[key];
            Assert.IsNotNull(entry, key);
            Assert.AreEqual((int)ResourceOverrideMode.Transparent, entry["Mode"].Value<int>(), key);
            Assert.AreEqual("#00000000", entry["CustomValue"].Value<string>(), key);
        }
    }
}
