using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using PlayniteAchievements.Providers.Settings;
#if !TEST
using PlayniteAchievements.Providers.Exophase;
using PlayniteAchievements.Providers.Epic;
using PlayniteAchievements.Providers.GOG;
using PlayniteAchievements.Providers.Manual;
using PlayniteAchievements.Providers.PSN;
using PlayniteAchievements.Providers.RetroAchievements;
using PlayniteAchievements.Providers.RPCS3;
using PlayniteAchievements.Providers.ShadPS4;
using PlayniteAchievements.Providers.Steam;
using PlayniteAchievements.Providers.Xenia;
using PlayniteAchievements.Providers.Xbox;
#endif

namespace PlayniteAchievements.Models.Settings
{
    /// <summary>
    /// Handles migration of provider settings from flat properties to the ProviderSettings dictionary.
    /// This class reads raw JSON to extract old flat properties and creates proper provider settings objects.
    /// </summary>
    public static class ProviderSettingsMigration
    {
#if !TEST
        /// <summary>
        /// Migrates flat provider properties from raw JSON to the ProviderSettings dictionary.
        /// Should be called before deserializing to PersistedSettings.
        /// </summary>
        /// <param name="json">The raw JSON settings string.</param>
        /// <returns>The JSON string with migrated provider settings, or the original if no migration needed.</returns>
        public static string MigrateFromJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return json;
            }

            try
            {
                var root = JObject.Parse(json);

                // Get the Persisted object where settings are stored
                var persisted = root["Persisted"] as JObject;
                if (persisted == null)
                {
                    // No Persisted section, nothing to migrate
                    return json;
                }

                // Check if ANY old flat properties exist that need migration
                if (!HasFlatPropertiesToMigrate(persisted))
                {
                    // No old settings to migrate
                    return json;
                }

                // Create ProviderSettings if it doesn't exist
                var providerSettings = persisted["ProviderSettings"] as JObject;
                if (providerSettings == null)
                {
                    providerSettings = new JObject();
                    persisted["ProviderSettings"] = providerSettings;
                }

                // Migrate each provider individually (skips if already in ProviderSettings)
                MigrateSteam(persisted, providerSettings);
                MigrateEpic(persisted, providerSettings);
                MigrateGog(persisted, providerSettings);
                MigratePsn(persisted, providerSettings);
                MigrateXbox(persisted, providerSettings);
                MigrateRetroAchievements(persisted, providerSettings);
                MigrateExophase(persisted, providerSettings);
                MigrateShadPS4(persisted, providerSettings);
                MigrateRpcs3(persisted, providerSettings);
                MigrateXenia(persisted, providerSettings);
                MigrateManual(persisted, providerSettings);

                // Remove all flat provider properties from the JSON
                RemoveFlatProperties(persisted);

                return root.ToString(Newtonsoft.Json.Formatting.None);
            }
            catch (Exception)
            {
                // If migration fails, return original JSON
                return json;
            }
        }

        /// <summary>
        /// Checks if there are any flat properties that need to be migrated.
        /// </summary>
        private static bool HasFlatPropertiesToMigrate(JObject persisted)
        {
            // Check for any of the old flat property names
            return persisted["SteamUserId"] != null ||
                   persisted["EpicAccountId"] != null ||
                   persisted["GogUserId"] != null ||
                   persisted["PsnNpsso"] != null ||
                   persisted["XboxEnabled"] != null ||
                   persisted["RetroAchievementsEnabled"] != null ||
                   persisted["RaUsername"] != null ||
                   persisted["ExophaseUserId"] != null ||
                   persisted["ShadPS4GameDataPath"] != null ||
                   persisted["Rpcs3ExecutablePath"] != null ||
                   persisted["XeniaAccountPath"] != null ||
                   persisted["ManualTrackingOverrideEnabled"] != null;
        }

        private static void MigrateSteam(JObject persisted, JObject providerSettings)
        {
            // Skip if already migrated
            if (providerSettings["Steam"] != null) return;

            var settings = new SteamSettings
            {
                IsEnabled = persisted["SteamEnabled"]?.Value<bool>() ?? true,
                SteamUserId = persisted["SteamUserId"]?.ToString(),
                SteamApiKey = persisted["SteamApiKey"]?.ToString()
            };
            providerSettings["Steam"] = JObject.Parse(settings.SerializeToJson());
        }

        private static void MigrateEpic(JObject persisted, JObject providerSettings)
        {
            // Skip if already migrated
            if (providerSettings["Epic"] != null) return;

            var settings = new EpicSettings
            {
                IsEnabled = persisted["EpicEnabled"]?.Value<bool>() ?? true,
                AccountId = persisted["EpicAccountId"]?.ToString(),
                AccessToken = persisted["EpicAccessToken"]?.ToString(),
                RefreshToken = persisted["EpicRefreshToken"]?.ToString(),
                TokenType = persisted["EpicTokenType"]?.ToString(),
                TokenExpiryUtc = persisted["EpicTokenExpiryUtc"]?.Value<DateTime>() ?? default,
                RefreshTokenExpiryUtc = persisted["EpicRefreshTokenExpiryUtc"]?.Value<DateTime>() ?? default
            };
            providerSettings["Epic"] = JObject.Parse(settings.SerializeToJson());
        }

        private static void MigrateGog(JObject persisted, JObject providerSettings)
        {
            // Skip if already migrated
            if (providerSettings["GOG"] != null) return;

            var settings = new GogSettings
            {
                IsEnabled = persisted["GogEnabled"]?.Value<bool>() ?? true,
                UserId = persisted["GogUserId"]?.ToString()
            };
            providerSettings["GOG"] = JObject.Parse(settings.SerializeToJson());
        }

        private static void MigratePsn(JObject persisted, JObject providerSettings)
        {
            // Skip if already migrated
            if (providerSettings["PSN"] != null) return;

            var settings = new PsnSettings
            {
                IsEnabled = persisted["PsnEnabled"]?.Value<bool>() ?? true,
                Npsso = persisted["PsnNpsso"]?.ToString() ?? string.Empty
            };
            providerSettings["PSN"] = JObject.Parse(settings.SerializeToJson());
        }

        private static void MigrateXbox(JObject persisted, JObject providerSettings)
        {
            // Skip if already migrated
            if (providerSettings["Xbox"] != null) return;

            var settings = new XboxSettings
            {
                IsEnabled = persisted["XboxEnabled"]?.Value<bool>() ?? true,
                LowResIcons = persisted["XboxLowResIcons"]?.Value<bool>() ?? false
            };
            providerSettings["Xbox"] = JObject.Parse(settings.SerializeToJson());
        }

        private static void MigrateRetroAchievements(JObject persisted, JObject providerSettings)
        {
            // Skip if already migrated
            if (providerSettings["RetroAchievements"] != null) return;

            var gameIdOverrides = new Dictionary<Guid, int>();
            var overridesObj = persisted["RaGameIdOverrides"] as JObject;
            if (overridesObj != null)
            {
                foreach (var kvp in overridesObj)
                {
                    if (Guid.TryParse(kvp.Key, out var gameId) && kvp.Value != null)
                    {
                        gameIdOverrides[gameId] = kvp.Value.Value<int>();
                    }
                }
            }

            var settings = new RetroAchievementsSettings
            {
                IsEnabled = persisted["RetroAchievementsEnabled"]?.Value<bool>() ?? true,
                RaUsername = persisted["RaUsername"]?.ToString(),
                RaWebApiKey = persisted["RaWebApiKey"]?.ToString(),
                RaRarityStats = persisted["RaRarityStats"]?.ToString() ?? "casual",
                RaPointsMode = persisted["RaPointsMode"]?.ToString() ?? "points",
                HashIndexMaxAgeDays = persisted["HashIndexMaxAgeDays"]?.Value<int>() ?? 30,
                EnableArchiveScanning = persisted["EnableArchiveScanning"]?.Value<bool>() ?? true,
                EnableDiscHashing = persisted["EnableDiscHashing"]?.Value<bool>() ?? true,
                EnableRaNameFallback = persisted["EnableRaNameFallback"]?.Value<bool>() ?? true,
                RaGameIdOverrides = gameIdOverrides
            };
            providerSettings["RetroAchievements"] = JObject.Parse(settings.SerializeToJson());
        }

        private static void MigrateExophase(JObject persisted, JObject providerSettings)
        {
            // Skip if already migrated
            if (providerSettings["Exophase"] != null) return;

            var managedProviders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var managedArr = persisted["ExophaseManagedProviders"] as JArray;
            if (managedArr != null)
            {
                foreach (var item in managedArr)
                {
                    managedProviders.Add(item.ToString());
                }
            }

            var includedGames = new HashSet<Guid>();
            var includedArr = persisted["ExophaseIncludedGames"] as JArray;
            if (includedArr != null)
            {
                foreach (var item in includedArr)
                {
                    if (Guid.TryParse(item.ToString(), out var gameId))
                    {
                        includedGames.Add(gameId);
                    }
                }
            }

            var slugOverrides = new Dictionary<Guid, string>();
            var slugObj = persisted["ExophaseSlugOverrides"] as JObject;
            if (slugObj != null)
            {
                foreach (var kvp in slugObj)
                {
                    if (Guid.TryParse(kvp.Key, out var gameId))
                    {
                        slugOverrides[gameId] = kvp.Value?.ToString();
                    }
                }
            }

            var settings = new ExophaseSettings
            {
                IsEnabled = persisted["ExophaseEnabled"]?.Value<bool>() ?? false,
                UserId = persisted["ExophaseUserId"]?.ToString(),
                ManagedProviders = managedProviders,
                IncludedGames = includedGames,
                SlugOverrides = slugOverrides
            };
            providerSettings["Exophase"] = JObject.Parse(settings.SerializeToJson());
        }

        private static void MigrateShadPS4(JObject persisted, JObject providerSettings)
        {
            // Skip if already migrated
            if (providerSettings["ShadPS4"] != null) return;

            var settings = new ShadPS4Settings
            {
                IsEnabled = persisted["ShadPS4Enabled"]?.Value<bool>() ?? true,
                GameDataPath = persisted["ShadPS4GameDataPath"]?.ToString()
            };
            providerSettings["ShadPS4"] = JObject.Parse(settings.SerializeToJson());
        }

        private static void MigrateRpcs3(JObject persisted, JObject providerSettings)
        {
            // Skip if already migrated
            if (providerSettings["RPCS3"] != null) return;

            var settings = new Rpcs3Settings
            {
                IsEnabled = persisted["Rpcs3Enabled"]?.Value<bool>() ?? true,
                ExecutablePath = persisted["Rpcs3ExecutablePath"]?.ToString()
            };
            providerSettings["RPCS3"] = JObject.Parse(settings.SerializeToJson());
        }

        private static void MigrateXenia(JObject persisted, JObject providerSettings)
        {
            // Skip if already migrated
            if (providerSettings["Xenia"] != null) return;

            var gameIdOverrides = new Dictionary<Guid, string>();
            var overridesObj = persisted["XeniaGameIdOverrides"] as JObject;
            if (overridesObj != null)
            {
                foreach (var kvp in overridesObj)
                {
                    if (Guid.TryParse(kvp.Key, out var gameId))
                    {
                        gameIdOverrides[gameId] = kvp.Value?.ToString();
                    }
                }
            }

            var settings = new XeniaSettings
            {
                IsEnabled = persisted["XeniaEnabled"]?.Value<bool>() ?? true,
                AccountPath = persisted["XeniaAccountPath"]?.ToString(),
                GameIdOverrides = gameIdOverrides,
                ShowHiddenAchievements = true
            };
            providerSettings["Xenia"] = JObject.Parse(settings.SerializeToJson());
        }

        private static void MigrateManual(JObject persisted, JObject providerSettings)
        {
            // Skip if already migrated
            if (providerSettings["Manual"] != null) return;

            var achievementLinks = new Dictionary<Guid, ManualAchievementLink>();
            var linksObj = persisted["ManualAchievementLinks"] as JObject;
            if (linksObj != null)
            {
                foreach (var kvp in linksObj)
                {
                    if (Guid.TryParse(kvp.Key, out var gameId) && kvp.Value != null)
                    {
                        achievementLinks[gameId] = kvp.Value.ToObject<ManualAchievementLink>();
                    }
                }
            }

            var settings = new ManualSettings
            {
                IsEnabled = persisted["ManualEnabled"]?.Value<bool>() ?? true,
                ManualTrackingOverrideEnabled = persisted["ManualTrackingOverrideEnabled"]?.Value<bool>() ?? false,
                AchievementLinks = achievementLinks
            };
            providerSettings["Manual"] = JObject.Parse(settings.SerializeToJson());
        }

        private static void RemoveFlatProperties(JObject persisted)
        {
            // Steam
            persisted.Remove("SteamUserId");
            persisted.Remove("SteamApiKey");
            persisted.Remove("SteamEnabled");

            // Epic
            persisted.Remove("EpicAccountId");
            persisted.Remove("EpicAccessToken");
            persisted.Remove("EpicRefreshToken");
            persisted.Remove("EpicTokenType");
            persisted.Remove("EpicTokenExpiryUtc");
            persisted.Remove("EpicRefreshTokenExpiryUtc");
            persisted.Remove("EpicEnabled");

            // GOG
            persisted.Remove("GogUserId");
            persisted.Remove("GogEnabled");

            // PSN
            persisted.Remove("PsnNpsso");
            persisted.Remove("PsnEnabled");

            // Xbox
            persisted.Remove("XboxEnabled");
            persisted.Remove("XboxLowResIcons");

            // RetroAchievements
            persisted.Remove("RetroAchievementsEnabled");
            persisted.Remove("RaUsername");
            persisted.Remove("RaWebApiKey");
            persisted.Remove("RaRarityStats");
            persisted.Remove("RaPointsMode");
            persisted.Remove("HashIndexMaxAgeDays");
            persisted.Remove("EnableArchiveScanning");
            persisted.Remove("EnableDiscHashing");
            persisted.Remove("EnableRaNameFallback");
            persisted.Remove("RaGameIdOverrides");

            // Exophase
            persisted.Remove("ExophaseEnabled");
            persisted.Remove("ExophaseUserId");
            persisted.Remove("ExophaseManagedProviders");
            persisted.Remove("ExophaseIncludedGames");
            persisted.Remove("ExophaseSlugOverrides");

            // ShadPS4
            persisted.Remove("ShadPS4Enabled");
            persisted.Remove("ShadPS4GameDataPath");

            // RPCS3
            persisted.Remove("Rpcs3Enabled");
            persisted.Remove("Rpcs3ExecutablePath");

            // Xenia
            persisted.Remove("XeniaEnabled");
            persisted.Remove("XeniaAccountPath");
            persisted.Remove("XeniaGameIdOverrides");

            // Manual
            persisted.Remove("ManualEnabled");
            persisted.Remove("ManualTrackingOverrideEnabled");
            persisted.Remove("ManualAchievementLinks");
            persisted.Remove("LegacyManualImportPath");
        }
#else
        // Test project stub - migration not available in tests
        public static string MigrateFromJson(string json) => json;
#endif
    }
}
