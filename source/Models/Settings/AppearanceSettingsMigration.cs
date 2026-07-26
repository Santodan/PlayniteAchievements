using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PlayniteAchievements.Models.Settings
{
    /// <summary>
    /// Seeds the transparent inline-surface resource overrides (GridSurface, ControlSurface) into
    /// existing user configs. Runs before settings deserialization so the seeded entries persist
    /// through the load, and is gated by the <see cref="PersistedSettings.InlineSurfaceTransparencySeeded"/>
    /// flag so it runs exactly once: after seeding it stamps the flag, after which a user's own
    /// later choice -- including switching a surface back to Follow Playnite (which removes the
    /// entry) -- is respected and never re-seeded. Fresh installs default the flag true and seed
    /// the overrides in the plugin-reference constructor, so this migration is a no-op for them.
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

                return SeedInlineSurfaceTransparency(persisted)
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
    }
}
