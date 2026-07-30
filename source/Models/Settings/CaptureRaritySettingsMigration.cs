using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PlayniteAchievements.Models.Settings
{
    /// <summary>
    /// Splits the pre-existing single screenshot capture threshold into the per-variant thresholds.
    /// Earlier configs stored one <c>UnlockScreenshotMinimumRarity</c> and one
    /// <c>UnlockScreenshotAlwaysCaptureCompletion</c> shared by the clean, with-notification, and
    /// framed screenshot variants. This seeds each variant's own key from those legacy values when
    /// the config does not already define the per-variant key, then removes the legacy keys.
    /// Because the legacy keys are removed and the per-variant keys are only seeded when absent, the
    /// migration runs exactly once and never overwrites a user's later per-variant choice. Fresh
    /// installs carry no legacy key, so this is a no-op for them.
    /// </summary>
    public static class CaptureRaritySettingsMigration
    {
        private const string LegacyMinimumRarity = "UnlockScreenshotMinimumRarity";
        private const string LegacyAlwaysCaptureCompletion = "UnlockScreenshotAlwaysCaptureCompletion";

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

                var changed = SeedVariants(
                    persisted,
                    LegacyMinimumRarity,
                    nameof(PersistedSettings.UnlockScreenshotCleanMinimumRarity),
                    nameof(PersistedSettings.UnlockScreenshotWithToastMinimumRarity),
                    nameof(PersistedSettings.UnlockScreenshotFramedMinimumRarity));

                changed |= SeedVariants(
                    persisted,
                    LegacyAlwaysCaptureCompletion,
                    nameof(PersistedSettings.UnlockScreenshotCleanAlwaysCaptureCompletion),
                    nameof(PersistedSettings.UnlockScreenshotWithToastAlwaysCaptureCompletion),
                    nameof(PersistedSettings.UnlockScreenshotFramedAlwaysCaptureCompletion));

                return changed ? root.ToString(Formatting.None) : json;
            }
            catch (Exception)
            {
                return json;
            }
        }

        /// <summary>
        /// Copies the legacy value into each per-variant key that the config does not already
        /// define, then removes the legacy key. Returns whether the config was modified.
        /// </summary>
        private static bool SeedVariants(
            JObject persisted,
            string legacyKey,
            string cleanKey,
            string withToastKey,
            string framedKey)
        {
            var legacy = persisted[legacyKey];
            if (legacy == null)
            {
                return false;
            }

            foreach (var variantKey in new[] { cleanKey, withToastKey, framedKey })
            {
                if (persisted[variantKey] == null)
                {
                    persisted[variantKey] = legacy.DeepClone();
                }
            }

            persisted.Remove(legacyKey);
            return true;
        }
    }
}
