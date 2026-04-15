using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Providers.Manual;
using System;
using System.Collections.Generic;

namespace PlayniteAchievements.Services
{
    internal sealed class GameCustomDataLegacyMigration
    {
        public GameCustomDataLegacyMigrationResult Parse(string rawJson)
        {
            var root = JObject.Parse(rawJson);
            var persisted = root["Persisted"] as JObject;
            if (persisted == null)
            {
                return new GameCustomDataLegacyMigrationResult(rawJson, new Dictionary<Guid, GameCustomDataFile>(), false);
            }

            var legacyByGame = new Dictionary<Guid, GameCustomDataFile>();
            CollectLegacyPersistedData(persisted, legacyByGame);

            var providerSettings = persisted["ProviderSettings"] as JObject;
            if (providerSettings != null)
            {
                CollectLegacyProviderData(providerSettings, legacyByGame);
            }

            var configChanged = RemoveMigratedFields(persisted, providerSettings);
            var cleanedJson = configChanged ? root.ToString(Formatting.None) : rawJson;
            return new GameCustomDataLegacyMigrationResult(cleanedJson, legacyByGame, configChanged);
        }

        private static void CollectLegacyPersistedData(
            JObject persisted,
            Dictionary<Guid, GameCustomDataFile> legacyByGame)
        {
            AddGuidSet(persisted["ExcludedGameIds"] as JArray, legacyByGame, (data, gameId) =>
            {
                data.ExcludedFromRefreshes = true;
            });

            AddGuidSet(persisted["ExcludedFromSummariesGameIds"] as JArray, legacyByGame, (data, gameId) =>
            {
                data.ExcludedFromSummaries = true;
            });

            AddGuidSet(persisted["SeparateLockedIconEnabledGameIds"] as JArray, legacyByGame, (data, gameId) =>
            {
                data.UseSeparateLockedIconsOverride = true;
            });

            AddGuidStringDictionary(persisted["ManualCapstones"] as JObject, legacyByGame, (data, value) =>
            {
                data.ManualCapstoneApiName = value;
            });

            AddGuidListDictionary(persisted["AchievementOrderOverrides"] as JObject, legacyByGame, (data, value) =>
            {
                data.AchievementOrder = value;
            });

            AddGuidStringMapDictionary(persisted["AchievementCategoryOverrides"] as JObject, legacyByGame, (data, value) =>
            {
                data.AchievementCategoryOverrides = value;
            });

            AddGuidStringMapDictionary(persisted["AchievementCategoryTypeOverrides"] as JObject, legacyByGame, (data, value) =>
            {
                data.AchievementCategoryTypeOverrides = value;
            });
        }

        private static void CollectLegacyProviderData(
            JObject providerSettings,
            Dictionary<Guid, GameCustomDataFile> legacyByGame)
        {
            var retroSettings = providerSettings["RetroAchievements"] as JObject;
            AddGuidIntDictionary(retroSettings?["RaGameIdOverrides"] as JObject, legacyByGame, (data, value) =>
            {
                data.RetroAchievementsGameIdOverride = value;
            });

            var exophaseSettings = providerSettings["Exophase"] as JObject;
            AddGuidSet(exophaseSettings?["IncludedGames"] as JArray, legacyByGame, (data, gameId) =>
            {
                data.ForceUseExophase = true;
            });
            AddGuidStringDictionary(exophaseSettings?["SlugOverrides"] as JObject, legacyByGame, (data, value) =>
            {
                data.ExophaseSlugOverride = value;
            });

            var manualSettings = providerSettings["Manual"] as JObject;
            AddGuidObjectDictionary(manualSettings?["AchievementLinks"] as JObject, legacyByGame, token =>
            {
                return token?.ToObject<ManualAchievementLink>();
            }, (data, value) =>
            {
                data.ManualLink = value;
            });
        }

        private static bool RemoveMigratedFields(JObject persisted, JObject providerSettings)
        {
            var changed = false;

            changed |= RemoveProperty(persisted, "ExcludedGameIds");
            changed |= RemoveProperty(persisted, "ExcludedFromSummariesGameIds");
            changed |= RemoveProperty(persisted, "SeparateLockedIconEnabledGameIds");
            changed |= RemoveProperty(persisted, "ManualCapstones");
            changed |= RemoveProperty(persisted, "AchievementOrderOverrides");
            changed |= RemoveProperty(persisted, "AchievementCategoryOverrides");
            changed |= RemoveProperty(persisted, "AchievementCategoryTypeOverrides");

            var retroSettings = providerSettings?["RetroAchievements"] as JObject;
            changed |= RemoveProperty(retroSettings, "RaGameIdOverrides");

            var exophaseSettings = providerSettings?["Exophase"] as JObject;
            changed |= RemoveProperty(exophaseSettings, "IncludedGames");
            changed |= RemoveProperty(exophaseSettings, "SlugOverrides");

            var manualSettings = providerSettings?["Manual"] as JObject;
            changed |= RemoveProperty(manualSettings, "AchievementLinks");
            return changed;
        }

        private static bool RemoveProperty(JObject obj, string propertyName)
        {
            if (obj == null || string.IsNullOrWhiteSpace(propertyName) || obj[propertyName] == null)
            {
                return false;
            }

            obj.Remove(propertyName);
            return true;
        }

        private static void AddGuidSet(
            JArray array,
            Dictionary<Guid, GameCustomDataFile> legacyByGame,
            Action<GameCustomDataFile, Guid> apply)
        {
            if (array == null || apply == null)
            {
                return;
            }

            foreach (var token in array)
            {
                if (!Guid.TryParse(token?.ToString(), out var gameId) || gameId == Guid.Empty)
                {
                    continue;
                }

                apply(GetOrAdd(legacyByGame, gameId), gameId);
            }
        }

        private static void AddGuidStringDictionary(
            JObject obj,
            Dictionary<Guid, GameCustomDataFile> legacyByGame,
            Action<GameCustomDataFile, string> apply)
        {
            if (obj == null || apply == null)
            {
                return;
            }

            foreach (var pair in obj)
            {
                if (!Guid.TryParse(pair.Key, out var gameId) || gameId == Guid.Empty)
                {
                    continue;
                }

                apply(GetOrAdd(legacyByGame, gameId), pair.Value?.ToString());
            }
        }

        private static void AddGuidIntDictionary(
            JObject obj,
            Dictionary<Guid, GameCustomDataFile> legacyByGame,
            Action<GameCustomDataFile, int> apply)
        {
            if (obj == null || apply == null)
            {
                return;
            }

            foreach (var pair in obj)
            {
                if (!Guid.TryParse(pair.Key, out var gameId) || gameId == Guid.Empty || pair.Value == null)
                {
                    continue;
                }

                apply(GetOrAdd(legacyByGame, gameId), pair.Value.Value<int>());
            }
        }

        private static void AddGuidListDictionary(
            JObject obj,
            Dictionary<Guid, GameCustomDataFile> legacyByGame,
            Action<GameCustomDataFile, List<string>> apply)
        {
            if (obj == null || apply == null)
            {
                return;
            }

            foreach (var pair in obj)
            {
                if (!Guid.TryParse(pair.Key, out var gameId) || gameId == Guid.Empty || pair.Value == null)
                {
                    continue;
                }

                apply(
                    GetOrAdd(legacyByGame, gameId),
                    pair.Value.ToObject<List<string>>() ?? new List<string>());
            }
        }

        private static void AddGuidStringMapDictionary(
            JObject obj,
            Dictionary<Guid, GameCustomDataFile> legacyByGame,
            Action<GameCustomDataFile, Dictionary<string, string>> apply)
        {
            if (obj == null || apply == null)
            {
                return;
            }

            foreach (var pair in obj)
            {
                if (!Guid.TryParse(pair.Key, out var gameId) || gameId == Guid.Empty || pair.Value == null)
                {
                    continue;
                }

                apply(
                    GetOrAdd(legacyByGame, gameId),
                    pair.Value.ToObject<Dictionary<string, string>>() ?? new Dictionary<string, string>());
            }
        }

        private static void AddGuidObjectDictionary<T>(
            JObject obj,
            Dictionary<Guid, GameCustomDataFile> legacyByGame,
            Func<JToken, T> map,
            Action<GameCustomDataFile, T> apply)
            where T : class
        {
            if (obj == null || map == null || apply == null)
            {
                return;
            }

            foreach (var pair in obj)
            {
                if (!Guid.TryParse(pair.Key, out var gameId) || gameId == Guid.Empty || pair.Value == null)
                {
                    continue;
                }

                var value = map(pair.Value);
                if (value == null)
                {
                    continue;
                }

                apply(GetOrAdd(legacyByGame, gameId), value);
            }
        }

        private static GameCustomDataFile GetOrAdd(
            Dictionary<Guid, GameCustomDataFile> legacyByGame,
            Guid gameId)
        {
            if (!legacyByGame.TryGetValue(gameId, out var data) || data == null)
            {
                data = GameCustomDataNormalizer.CreateDefault(gameId);
                legacyByGame[gameId] = data;
            }

            return data;
        }
    }

    internal sealed class GameCustomDataLegacyMigrationResult
    {
        public GameCustomDataLegacyMigrationResult(
            string cleanedJson,
            IReadOnlyDictionary<Guid, GameCustomDataFile> legacyByGame,
            bool configChanged)
        {
            CleanedJson = cleanedJson;
            LegacyByGame = legacyByGame ?? new Dictionary<Guid, GameCustomDataFile>();
            ConfigChanged = configChanged;
        }

        public string CleanedJson { get; }

        public IReadOnlyDictionary<Guid, GameCustomDataFile> LegacyByGame { get; }

        public bool ConfigChanged { get; }
    }
}
