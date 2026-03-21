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

                // Check if ProviderSettings already exists and has content
                var providerSettings = root["ProviderSettings"] as JObject;
                if (providerSettings != null && providerSettings.Count > 0)
                {
                    // Already migrated
                    return json;
                }

                // Check if we have evidence of old settings (SteamUserId populated)
                var steamUserId = root["SteamUserId"]?.ToString();
                if (string.IsNullOrEmpty(steamUserId))
                {
                    // No old settings to migrate
                    return json;
                }

                // Create or clear ProviderSettings
                if (providerSettings == null)
                {
                    providerSettings = new JObject();
                    root["ProviderSettings"] = providerSettings;
                }

                // Migrate each provider
                MigrateSteam(root, providerSettings);
                MigrateEpic(root, providerSettings);
                MigrateGog(root, providerSettings);
                MigratePsn(root, providerSettings);
                MigrateXbox(root, providerSettings);
                MigrateRetroAchievements(root, providerSettings);
                MigrateExophase(root, providerSettings);
                MigrateShadPS4(root, providerSettings);
                MigrateRpcs3(root, providerSettings);
                MigrateXenia(root, providerSettings);
                MigrateManual(root, providerSettings);

                // Remove all flat provider properties from the JSON
                RemoveFlatProperties(root);

                return root.ToString(Newtonsoft.Json.Formatting.None);
            }
            catch (Exception)
            {
                // If migration fails, return original JSON
                return json;
            }
        }

        private static void MigrateSteam(JObject root, JObject providerSettings)
        {
            var settings = new SteamSettings
            {
                IsEnabled = root["SteamEnabled"]?.Value<bool>() ?? true,
                SteamUserId = root["SteamUserId"]?.ToString(),
                SteamApiKey = root["SteamApiKey"]?.ToString()
            };
            providerSettings["Steam"] = JObject.Parse(settings.SerializeToJson());
        }

        private static void MigrateEpic(JObject root, JObject providerSettings)
        {
            var settings = new EpicSettings
            {
                IsEnabled = root["EpicEnabled"]?.Value<bool>() ?? true,
                AccountId = root["EpicAccountId"]?.ToString(),
                AccessToken = root["EpicAccessToken"]?.ToString(),
                RefreshToken = root["EpicRefreshToken"]?.ToString(),
                TokenType = root["EpicTokenType"]?.ToString(),
                TokenExpiryUtc = root["EpicTokenExpiryUtc"]?.Value<DateTime>() ?? default,
                RefreshTokenExpiryUtc = root["EpicRefreshTokenExpiryUtc"]?.Value<DateTime>() ?? default
            };
            providerSettings["Epic"] = JObject.Parse(settings.SerializeToJson());
        }

        private static void MigrateGog(JObject root, JObject providerSettings)
        {
            var settings = new GogSettings
            {
                IsEnabled = root["GogEnabled"]?.Value<bool>() ?? true,
                UserId = root["GogUserId"]?.ToString()
            };
            providerSettings["GOG"] = JObject.Parse(settings.SerializeToJson());
        }

        private static void MigratePsn(JObject root, JObject providerSettings)
        {
            var settings = new PsnSettings
            {
                IsEnabled = root["PsnEnabled"]?.Value<bool>() ?? true,
                Npsso = root["PsnNpsso"]?.ToString() ?? string.Empty
            };
            providerSettings["PSN"] = JObject.Parse(settings.SerializeToJson());
        }

        private static void MigrateXbox(JObject root, JObject providerSettings)
        {
            var settings = new XboxSettings
            {
                IsEnabled = root["XboxEnabled"]?.Value<bool>() ?? true,
                LowResIcons = root["XboxLowResIcons"]?.Value<bool>() ?? false
            };
            providerSettings["Xbox"] = JObject.Parse(settings.SerializeToJson());
        }

        private static void MigrateRetroAchievements(JObject root, JObject providerSettings)
        {
            var gameIdOverrides = new Dictionary<Guid, int>();
            var overridesObj = root["RaGameIdOverrides"] as JObject;
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
                IsEnabled = root["RetroAchievementsEnabled"]?.Value<bool>() ?? true,
                RaUsername = root["RaUsername"]?.ToString(),
                RaWebApiKey = root["RaWebApiKey"]?.ToString(),
                RaRarityStats = root["RaRarityStats"]?.ToString() ?? "casual",
                RaPointsMode = root["RaPointsMode"]?.ToString() ?? "points",
                HashIndexMaxAgeDays = root["HashIndexMaxAgeDays"]?.Value<int>() ?? 30,
                EnableArchiveScanning = root["EnableArchiveScanning"]?.Value<bool>() ?? true,
                EnableDiscHashing = root["EnableDiscHashing"]?.Value<bool>() ?? true,
                EnableRaNameFallback = root["EnableRaNameFallback"]?.Value<bool>() ?? true,
                RaGameIdOverrides = gameIdOverrides
            };
            providerSettings["RetroAchievements"] = JObject.Parse(settings.SerializeToJson());
        }

        private static void MigrateExophase(JObject root, JObject providerSettings)
        {
            var managedProviders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var managedArr = root["ExophaseManagedProviders"] as JArray;
            if (managedArr != null)
            {
                foreach (var item in managedArr)
                {
                    managedProviders.Add(item.ToString());
                }
            }

            var includedGames = new HashSet<Guid>();
            var includedArr = root["ExophaseIncludedGames"] as JArray;
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
            var slugObj = root["ExophaseSlugOverrides"] as JObject;
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
                IsEnabled = root["ExophaseEnabled"]?.Value<bool>() ?? false,
                UserId = root["ExophaseUserId"]?.ToString(),
                ManagedProviders = managedProviders,
                IncludedGames = includedGames,
                SlugOverrides = slugOverrides
            };
            providerSettings["Exophase"] = JObject.Parse(settings.SerializeToJson());
        }

        private static void MigrateShadPS4(JObject root, JObject providerSettings)
        {
            var settings = new ShadPS4Settings
            {
                IsEnabled = root["ShadPS4Enabled"]?.Value<bool>() ?? true,
                GameDataPath = root["ShadPS4GameDataPath"]?.ToString()
            };
            providerSettings["ShadPS4"] = JObject.Parse(settings.SerializeToJson());
        }

        private static void MigrateRpcs3(JObject root, JObject providerSettings)
        {
            var settings = new Rpcs3Settings
            {
                IsEnabled = root["Rpcs3Enabled"]?.Value<bool>() ?? true,
                ExecutablePath = root["Rpcs3ExecutablePath"]?.ToString()
            };
            providerSettings["RPCS3"] = JObject.Parse(settings.SerializeToJson());
        }

        private static void MigrateXenia(JObject root, JObject providerSettings)
        {
            var gameIdOverrides = new Dictionary<Guid, string>();
            var overridesObj = root["XeniaGameIdOverrides"] as JObject;
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
                IsEnabled = root["XeniaEnabled"]?.Value<bool>() ?? true,
                AccountPath = root["XeniaAccountPath"]?.ToString(),
                GameIdOverrides = gameIdOverrides
            };
            providerSettings["Xenia"] = JObject.Parse(settings.SerializeToJson());
        }

        private static void MigrateManual(JObject root, JObject providerSettings)
        {
            var achievementLinks = new Dictionary<Guid, ManualAchievementLink>();
            var linksObj = root["ManualAchievementLinks"] as JObject;
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
                IsEnabled = root["ManualEnabled"]?.Value<bool>() ?? true,
                ManualTrackingOverrideEnabled = root["ManualTrackingOverrideEnabled"]?.Value<bool>() ?? false,
                AchievementLinks = achievementLinks
            };
            providerSettings["Manual"] = JObject.Parse(settings.SerializeToJson());
        }

        private static void RemoveFlatProperties(JObject root)
        {
            // Steam
            root.Remove("SteamUserId");
            root.Remove("SteamApiKey");
            root.Remove("SteamEnabled");

            // Epic
            root.Remove("EpicAccountId");
            root.Remove("EpicAccessToken");
            root.Remove("EpicRefreshToken");
            root.Remove("EpicTokenType");
            root.Remove("EpicTokenExpiryUtc");
            root.Remove("EpicRefreshTokenExpiryUtc");
            root.Remove("EpicEnabled");

            // GOG
            root.Remove("GogUserId");
            root.Remove("GogEnabled");

            // PSN
            root.Remove("PsnNpsso");
            root.Remove("PsnEnabled");

            // Xbox
            root.Remove("XboxEnabled");
            root.Remove("XboxLowResIcons");

            // RetroAchievements
            root.Remove("RetroAchievementsEnabled");
            root.Remove("RaUsername");
            root.Remove("RaWebApiKey");
            root.Remove("RaRarityStats");
            root.Remove("RaPointsMode");
            root.Remove("HashIndexMaxAgeDays");
            root.Remove("EnableArchiveScanning");
            root.Remove("EnableDiscHashing");
            root.Remove("EnableRaNameFallback");
            root.Remove("RaGameIdOverrides");

            // Exophase
            root.Remove("ExophaseEnabled");
            root.Remove("ExophaseUserId");
            root.Remove("ExophaseManagedProviders");
            root.Remove("ExophaseIncludedGames");
            root.Remove("ExophaseSlugOverrides");

            // ShadPS4
            root.Remove("ShadPS4Enabled");
            root.Remove("ShadPS4GameDataPath");

            // RPCS3
            root.Remove("Rpcs3Enabled");
            root.Remove("Rpcs3ExecutablePath");

            // Xenia
            root.Remove("XeniaEnabled");
            root.Remove("XeniaAccountPath");
            root.Remove("XeniaGameIdOverrides");

            // Manual
            root.Remove("ManualEnabled");
            root.Remove("ManualTrackingOverrideEnabled");
            root.Remove("ManualAchievementLinks");
            root.Remove("LegacyManualImportPath");
        }
#else
        // Test project stub - migration not available in tests
        public static string MigrateFromJson(string json) => json;
#endif
    }
}
