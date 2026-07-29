using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PlayniteAchievements.Models.Settings
{
    /// <summary>
    /// Moves the legacy flat ToastShow*/FrameShow* appearance booleans into the
    /// <see cref="PersistedSettings.NotificationStyle"/> object. Runs before settings
    /// deserialization; a config that already has a NotificationStyle (or none of the flat
    /// names) is returned unchanged, so the migration is idempotent and a no-op for fresh
    /// installs.
    /// </summary>
    public static class NotificationStyleSettingsMigration
    {
        private static readonly (string FlatName, string StyleName, bool Default)[] ToastFlags =
        {
            ("ToastShowHeader", nameof(NotificationSurfaceStyle.ShowHeader), true),
            ("ToastShowName", nameof(NotificationSurfaceStyle.ShowName), true),
            ("ToastShowDescription", nameof(NotificationSurfaceStyle.ShowDescription), true),
            ("ToastShowCategory", nameof(NotificationSurfaceStyle.ShowCategory), true),
            ("ToastShowGameName", nameof(NotificationSurfaceStyle.ShowGameName), true),
            ("ToastShowRarityBadge", nameof(NotificationSurfaceStyle.ShowRarityBadge), true),
            ("ToastShowRarityPercent", nameof(NotificationSurfaceStyle.ShowRarityPercent), true),
            ("ToastShowRarityGlow", nameof(NotificationSurfaceStyle.ShowRarityGlow), true),
            ("ToastRarityColoredName", nameof(NotificationSurfaceStyle.RarityColoredName), true),
            ("ToastShowUnlockTime", nameof(NotificationSurfaceStyle.ShowUnlockTime), false)
        };

        private static readonly (string FlatName, string StyleName, bool Default)[] FrameFlags =
        {
            ("FrameShowHeader", nameof(NotificationSurfaceStyle.ShowHeader), true),
            ("FrameShowName", nameof(NotificationSurfaceStyle.ShowName), true),
            ("FrameShowDescription", nameof(NotificationSurfaceStyle.ShowDescription), true),
            ("FrameShowCategory", nameof(NotificationSurfaceStyle.ShowCategory), true),
            ("FrameShowGameName", nameof(NotificationSurfaceStyle.ShowGameName), true),
            ("FrameShowRarityBadge", nameof(NotificationSurfaceStyle.ShowRarityBadge), true),
            ("FrameShowRarityPercent", nameof(NotificationSurfaceStyle.ShowRarityPercent), true),
            ("FrameShowRarityGlow", nameof(NotificationSurfaceStyle.ShowRarityGlow), true),
            ("FrameRarityColoredName", nameof(NotificationSurfaceStyle.RarityColoredName), true),
            ("FrameShowUnlockTime", nameof(NotificationSurfaceStyle.ShowUnlockTime), true)
        };

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

                return MigrateFlatAppearanceFlags(persisted)
                    ? root.ToString(Formatting.None)
                    : json;
            }
            catch (Exception)
            {
                return json;
            }
        }

        private static bool MigrateFlatAppearanceFlags(JObject persisted)
        {
            if (persisted[nameof(PersistedSettings.NotificationStyle)] != null)
            {
                return false;
            }

            var anyFlatPresent = false;
            foreach (var (flatName, _, _) in ToastFlags)
            {
                anyFlatPresent |= persisted[flatName] != null;
            }

            foreach (var (flatName, _, _) in FrameFlags)
            {
                anyFlatPresent |= persisted[flatName] != null;
            }

            if (!anyFlatPresent)
            {
                return false;
            }

            persisted[nameof(PersistedSettings.NotificationStyle)] = new JObject
            {
                [nameof(NotificationStyleSettings.Toast)] = BuildSurface(persisted, ToastFlags),
                [nameof(NotificationStyleSettings.Frame)] = BuildSurface(persisted, FrameFlags)
            };

            foreach (var (flatName, _, _) in ToastFlags)
            {
                persisted.Remove(flatName);
            }

            foreach (var (flatName, _, _) in FrameFlags)
            {
                persisted.Remove(flatName);
            }

            return true;
        }

        private static JObject BuildSurface(
            JObject persisted,
            (string FlatName, string StyleName, bool Default)[] flags)
        {
            var surface = new JObject();
            foreach (var (flatName, styleName, defaultValue) in flags)
            {
                var token = persisted[flatName];
                surface[styleName] = token != null && token.Type == JTokenType.Boolean
                    ? token.Value<bool>()
                    : defaultValue;
            }

            return surface;
        }
    }
}
