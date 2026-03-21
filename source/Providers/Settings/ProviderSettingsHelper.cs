using System;
using PlayniteAchievements.Models.Settings;

namespace PlayniteAchievements.Providers.Settings
{
    /// <summary>
    /// Helper utility for loading and saving provider settings from/to PersistedSettings.
    /// </summary>
    public static class ProviderSettingsHelper
    {
        /// <summary>
        /// Loads provider settings from the PersistedSettings dictionary.
        /// </summary>
        /// <typeparam name="T">The type of provider settings to load.</typeparam>
        /// <param name="persisted">The PersistedSettings instance containing the settings dictionary.</param>
        /// <param name="providerKey">The provider key to look up in the dictionary.</param>
        /// <returns>A new instance of the provider settings populated from storage, or default values if not found.</returns>
        public static T Load<T>(PersistedSettings persisted, string providerKey) where T : ProviderSettingsBase, new()
        {
            var settings = new T();

            if (persisted?.ProviderSettings != null &&
                persisted.ProviderSettings.TryGetValue(providerKey, out var json) &&
                !string.IsNullOrEmpty(json))
            {
                settings.DeserializeFromJson(json);
            }

            return settings;
        }

        /// <summary>
        /// Saves provider settings to the PersistedSettings dictionary.
        /// </summary>
        /// <param name="persisted">The PersistedSettings instance to save to.</param>
        /// <param name="settings">The provider settings to save.</param>
        public static void Save(PersistedSettings persisted, ProviderSettingsBase settings)
        {
            if (persisted == null || settings == null)
            {
                return;
            }

            if (persisted.ProviderSettings == null)
            {
                persisted.ProviderSettings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            persisted.ProviderSettings[settings.ProviderKey] = settings.SerializeToJson();
        }
    }
}
