using System;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PlayniteAchievements.Models.Settings
{
    /// <summary>
    /// Seeds the in-house unlock sound settings once from an installed UniPlaySong configuration,
    /// so a user who had sounds set up there hears the same thing after the upgrade: its master
    /// switch, its volume, and its custom per-tier files (only when its pack setting was Custom,
    /// since it ignored those paths otherwise). Runs before settings deserialization and is gated
    /// by <see cref="PersistedSettings.UnlockSoundsSeededFromUniPlaySong"/>, which it stamps true
    /// whether or not a UniPlaySong config was found, so the seed never runs twice.
    /// </summary>
    public static class UnlockSoundSettingsMigration
    {
        public static readonly Guid UniPlaySongPluginId = new Guid("a1b2c3d4-e5f6-7890-abcd-ef1234567890");

        private static readonly string[] TierNames =
        {
            nameof(UnlockSoundSettings.Common),
            nameof(UnlockSoundSettings.Uncommon),
            nameof(UnlockSoundSettings.Rare),
            nameof(UnlockSoundSettings.UltraRare),
            nameof(UnlockSoundSettings.Hidden),
            nameof(UnlockSoundSettings.Capstone),
        };

        /// <summary>UniPlaySong's config.json under Playnite's extensions data root; null when unknown.</summary>
        public static string GetUniPlaySongConfigPath(string extensionsDataPath)
        {
            return string.IsNullOrWhiteSpace(extensionsDataPath)
                ? null
                : Path.Combine(extensionsDataPath, UniPlaySongPluginId.ToString(), "config.json");
        }

        public static string MigrateFromJson(string json, string uniPlaySongConfigPath)
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

                return Seed(persisted, uniPlaySongConfigPath)
                    ? root.ToString(Formatting.None)
                    : json;
            }
            catch (Exception)
            {
                return json;
            }
        }

        private static bool Seed(JObject persisted, string uniPlaySongConfigPath)
        {
            const string flagName = nameof(PersistedSettings.UnlockSoundsSeededFromUniPlaySong);

            var flag = persisted[flagName];
            if (flag != null && flag.Type == JTokenType.Boolean && flag.Value<bool>())
            {
                return false;
            }

            persisted[flagName] = true;

            var ups = TryReadUniPlaySongConfig(uniPlaySongConfigPath);
            if (ups == null)
            {
                return true;
            }

            var enabled = ups["EnableAchievementSound"];
            if (enabled != null && enabled.Type == JTokenType.Boolean)
            {
                persisted[nameof(PersistedSettings.EnableUnlockSounds)] = enabled.Value<bool>();
            }

            var volume = ups["MusicVolume"];
            if (volume != null && (volume.Type == JTokenType.Integer || volume.Type == JTokenType.Float))
            {
                persisted[nameof(PersistedSettings.UnlockSoundVolumePercent)] =
                    Math.Max(0, Math.Min(100, (int)Math.Round(volume.Value<double>())));
            }

            if (IsCustomPack(ups["AchievementSoundPack"]))
            {
                if (!(persisted[nameof(PersistedSettings.UnlockSounds)] is JObject sounds))
                {
                    sounds = new JObject();
                    persisted[nameof(PersistedSettings.UnlockSounds)] = sounds;
                }

                foreach (var tier in TierNames)
                {
                    var existing = sounds[tier]?.Type == JTokenType.String ? sounds[tier].Value<string>() : null;
                    if (!string.IsNullOrWhiteSpace(existing))
                    {
                        continue;
                    }

                    var path = ups[tier + "AchievementSoundPath"]?.Type == JTokenType.String
                        ? ups[tier + "AchievementSoundPath"].Value<string>()
                        : null;
                    if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                    {
                        sounds[tier] = path;
                    }
                }
            }

            return true;
        }

        /// <summary>UniPlaySong stores the pack as its enum name or ordinal; Custom is the third value.</summary>
        private static bool IsCustomPack(JToken pack)
        {
            if (pack == null)
            {
                return false;
            }

            if (pack.Type == JTokenType.String)
            {
                return string.Equals(pack.Value<string>(), "Custom", StringComparison.OrdinalIgnoreCase);
            }

            return pack.Type == JTokenType.Integer && pack.Value<int>() == 2;
        }

        private static JObject TryReadUniPlaySongConfig(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    return null;
                }

                return JObject.Parse(File.ReadAllText(path));
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
