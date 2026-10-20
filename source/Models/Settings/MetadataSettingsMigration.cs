using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PlayniteAchievements.Providers.Metadata;

namespace PlayniteAchievements.Models.Settings
{
    /// <summary>
    /// One-time move of the per-provider metadata settings into the Metadata tab's platform choices,
    /// gated by <see cref="PersistedSettings.MetadataChoicesSeeded"/>. Each provider's
    /// UseExophaseForRarity becomes its Rarity choice, and the Steam SteamHuntersCategoryMode (or its
    /// older UseSteamHuntersForCategories checkbox) becomes Steam's Categories choice. Choices the
    /// config already holds are kept. Runs before deserialization so the writes persist.
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
                return root["Persisted"] is JObject persisted && Seed(persisted)
                    ? root.ToString(Formatting.None)
                    : json;
            }
            catch (Exception)
            {
                return json;
            }
        }

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

            var mode = ReadSteamHuntersMode(FindProperty(providers, "Steam"));
            if (mode != MetadataCategoryMode.Off)
            {
                var steam = Platform(platforms, "Steam");
                if (steam[nameof(MetadataChoices.Categories)] == null)
                {
                    steam[nameof(MetadataChoices.Categories)] = MetadataChoices.SteamHunters;
                    steam[nameof(MetadataChoices.CategoryMode)] = mode.ToString();
                }
            }

            persisted[flagName] = true;
            return true;
        }

        /// <summary>
        /// The saved mode; the older checkbox maps checked to Merge and unchecked to Off; Merge, the
        /// setting's default, when neither is saved.
        /// </summary>
        private static MetadataCategoryMode ReadSteamHuntersMode(JObject steam)
        {
            var mode = steam?["SteamHuntersCategoryMode"];
            if (mode?.Type == JTokenType.String &&
                Enum.TryParse(mode.Value<string>(), ignoreCase: true, out MetadataCategoryMode parsed))
            {
                return parsed;
            }

            if (mode?.Type == JTokenType.Integer && Enum.IsDefined(typeof(MetadataCategoryMode), mode.Value<int>()))
            {
                return (MetadataCategoryMode)mode.Value<int>();
            }

            var legacy = steam?["UseSteamHuntersForCategories"];
            if (legacy?.Type == JTokenType.Boolean)
            {
                return legacy.Value<bool>() ? MetadataCategoryMode.Merge : MetadataCategoryMode.Off;
            }

            return MetadataCategoryMode.Merge;
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
