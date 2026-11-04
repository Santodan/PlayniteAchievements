using System;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PlayniteAchievements.Models.Settings
{
    /// <summary>
    /// One-time move of the per-provider metadata settings into the Metadata tab's platform choices,
    /// gated by <see cref="PersistedSettings.MetadataChoicesSeeded"/>. Each provider's
    /// UseExophaseForRarity becomes its Rarity choice, and the Steam SteamHuntersCategoryMode (or its
    /// older UseSteamHuntersForCategories checkbox) becomes Steam's Categories choice. Choices the
    /// config already holds are kept. Tags are not seeded: they follow the TrueGaming default.
    /// TrueGaming category choices are dropped on every load. Runs before deserialization so the
    /// writes persist.
    /// </summary>
    public static class MetadataSettingsMigration
    {
        public static string MigrateFromJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return json;
            }

            try
            {
                var root = JObject.Parse(json);
                if (!(root["Persisted"] is JObject persisted))
                {
                    return json;
                }

                var changed = Seed(persisted);
                changed |= DropTrueGamingCategories(persisted);
                return changed ? root.ToString(Formatting.None) : json;
            }
            catch (Exception)
            {
                return json;
            }
        }

        /// <summary>
        /// TrueGaming supplies tags only. A defaults row that chose it for categories takes
        /// SteamHunters, and a platform row that chose it follows the defaults.
        /// </summary>
        private static bool DropTrueGamingCategories(JObject persisted)
        {
            const string field = nameof(MetadataChoices.Categories);
            var changed = false;
            if (persisted[nameof(PersistedSettings.MetadataDefaults)] is JObject defaults && IsTrueGaming(defaults[field]))
            {
                defaults[field] = MetadataChoices.SteamHunters;
                changed = true;
            }

            if (persisted[nameof(PersistedSettings.MetadataPlatformChoices)] is JObject platforms)
            {
                foreach (var platform in platforms.Properties().Select(property => property.Value).OfType<JObject>())
                {
                    if (IsTrueGaming(platform[field]))
                    {
                        platform.Remove(field);
                        changed = true;
                    }
                }
            }

            return changed;
        }

        private static bool IsTrueGaming(JToken value) =>
            value?.Type == JTokenType.String &&
            string.Equals(value.Value<string>(), MetadataChoices.TrueGaming, StringComparison.OrdinalIgnoreCase);

        private static bool Seed(JObject persisted)
        {
            const string flagName = nameof(PersistedSettings.MetadataChoicesSeeded);
            var flag = persisted[flagName];
            if (flag != null && flag.Type == JTokenType.Boolean && flag.Value<bool>())
            {
                return false;
            }

            if (!(persisted[nameof(PersistedSettings.MetadataPlatformChoices)] is JObject platforms))
            {
                platforms = new JObject();
                persisted[nameof(PersistedSettings.MetadataPlatformChoices)] = platforms;
            }

            var providers = persisted[nameof(PersistedSettings.ProviderSettings)] as JObject;
            foreach (var key in MetadataChoices.ExophaseRarityProviders)
            {
                // The toggle defaulted to off, and the defaults row now says Exophase, so a platform
                // that had it off (or never saved it) keeps its own rarity.
                var enabled = FindProperty(providers, key)?["UseExophaseForRarity"];
                var useExophase = enabled?.Type == JTokenType.Boolean && enabled.Value<bool>();
                SetIfAbsent(
                    Platform(platforms, key),
                    nameof(MetadataChoices.Rarity),
                    useExophase ? MetadataChoices.Exophase : string.Empty);
            }

            if (UsedSteamHunters(FindProperty(providers, "Steam")))
            {
                SetIfAbsent(Platform(platforms, "Steam"), nameof(MetadataChoices.Categories), MetadataChoices.SteamHunters);
            }

            persisted[flagName] = true;
            return true;
        }

        /// <summary>
        /// Whether the saved SteamHuntersCategoryMode (Off = 0, Merge = 1, Override = 2) or the older
        /// UseSteamHuntersForCategories checkbox turned SteamHunters on; on, the setting's default,
        /// when neither is saved.
        /// </summary>
        private static bool UsedSteamHunters(JObject steam)
        {
            var mode = steam?["SteamHuntersCategoryMode"];
            if (mode?.Type == JTokenType.String)
            {
                return !string.Equals(mode.Value<string>(), "Off", StringComparison.OrdinalIgnoreCase);
            }

            if (mode?.Type == JTokenType.Integer)
            {
                return mode.Value<int>() != 0;
            }

            var legacy = steam?["UseSteamHuntersForCategories"];
            return legacy?.Type != JTokenType.Boolean || legacy.Value<bool>();
        }

        private static JObject Platform(JObject platforms, string key)
        {
            var existing = FindProperty(platforms, key);
            if (existing != null)
            {
                return existing;
            }

            var created = new JObject();
            platforms[key] = created;
            return created;
        }

        private static void SetIfAbsent(JObject target, string name, string value)
        {
            if (target[name] == null || target[name].Type == JTokenType.Null)
            {
                target[name] = value;
            }
        }

        private static JObject FindProperty(JObject parent, string key)
        {
            if (parent == null)
            {
                return null;
            }

            foreach (var property in parent.Properties())
            {
                if (string.Equals(property.Name, key, StringComparison.OrdinalIgnoreCase))
                {
                    return property.Value as JObject;
                }
            }

            return null;
        }
    }
}
