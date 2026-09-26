using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PlayniteAchievements.Models.Achievements;

namespace PlayniteAchievements.Models.Settings
{
    /// <summary>
    /// One-time appearance migrations, run before settings deserialization so their writes persist
    /// through the load. Each step is gated by its own flag and stamps it when done.
    ///
    /// Inline surfaces: seeds the transparent inline-surface resource overrides (GridSurface,
    /// ControlSurface) into existing user configs, gated by
    /// <see cref="PersistedSettings.InlineSurfaceTransparencySeeded"/>. After seeding, a user's own
    /// later choice -- including switching a surface back to Follow Playnite (which removes the
    /// entry) -- is respected and never re-seeded. Fresh installs default the flag true and seed
    /// the overrides in the plugin-reference constructor, so this step is a no-op for them.
    ///
    /// Common glow: clears the Common bit from the saved glow tier selections, gated by
    /// <see cref="PersistedSettings.CommonGlowTierCleared"/>. Common could not glow before, and the
    /// old soft-glow default of every tier saved that bit, so without this every existing config
    /// would start glowing Common.
    /// </summary>
    public static class AppearanceSettingsMigration
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
                var persisted = root["Persisted"] as JObject;
                if (persisted == null)
                {
                    return json;
                }

                var changed = SeedInlineSurfaceTransparency(persisted);
                changed |= ClearCommonGlowTier(persisted);
                return changed
                    ? root.ToString(Formatting.None)
                    : json;
            }
            catch (Exception)
            {
                return json;
            }
        }

        /// <summary>
        /// Adds the transparent inline-surface overrides for any seeded key the config does not
        /// already define, leaving existing user values untouched, then stamps the one-time flag.
        /// </summary>
        private static bool SeedInlineSurfaceTransparency(JObject persisted)
        {
            const string flagName = nameof(PersistedSettings.InlineSurfaceTransparencySeeded);

            var flag = persisted[flagName];
            if (flag != null && flag.Type == JTokenType.Boolean && flag.Value<bool>())
            {
                return false;
            }

            if (!(persisted[nameof(PersistedSettings.ResourceOverrides)] is JObject overrides))
            {
                overrides = new JObject();
                persisted[nameof(PersistedSettings.ResourceOverrides)] = overrides;
            }

            foreach (var pair in PersistedSettings.CreateDefaultResourceOverrides())
            {
                if (overrides[pair.Key] != null)
                {
                    continue;
                }

                overrides[pair.Key] = new JObject
                {
                    [nameof(ResourceOverrideSetting.Mode)] = (int)pair.Value.Mode,
                    [nameof(ResourceOverrideSetting.CustomValue)] = pair.Value.CustomValue
                };
            }

            persisted[flagName] = true;
            return true;
        }

        /// <summary>
        /// Removes the Common bit from the soft and ray glow tier selections where present, leaving
        /// every other bit as the user set it, then stamps the one-time flag.
        /// </summary>
        private static bool ClearCommonGlowTier(JObject persisted)
        {
            const string flagName = nameof(PersistedSettings.CommonGlowTierCleared);

            var flag = persisted[flagName];
            if (flag != null && flag.Type == JTokenType.Boolean && flag.Value<bool>())
            {
                return false;
            }

            ClearCommonBit(persisted, nameof(PersistedSettings.RarityGlowSoftTiers));
            ClearCommonBit(persisted, nameof(PersistedSettings.RarityGlowRayTiers));

            persisted[flagName] = true;
            return true;
        }

        private static void ClearCommonBit(JObject persisted, string propertyName)
        {
            var token = persisted[propertyName];
            if (token == null || token.Type != JTokenType.Integer)
            {
                return;
            }

            persisted[propertyName] = token.Value<int>() & ~(int)RaritySelection.Common;
        }
    }
}
