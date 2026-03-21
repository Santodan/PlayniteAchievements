namespace PlayniteAchievements.Providers.Settings
{
    /// <summary>
    /// Simple facade for loading and saving provider settings.
    /// </summary>
    public static class ProviderSettings
    {
        /// <summary>
        /// Loads provider settings of the specified type.
        /// </summary>
        /// <typeparam name="T">The provider settings type.</typeparam>
        /// <returns>The cached provider settings instance.</returns>
        public static T Load<T>() where T : ProviderSettingsBase, new()
        {
            return ProviderRegistry.Instance?.Settings<T>() ?? new T();
        }

        /// <summary>
        /// Saves provider settings to persisted storage.
        /// </summary>
        /// <typeparam name="T">The provider settings type.</typeparam>
        /// <param name="settings">The settings instance to save.</param>
        public static void Save<T>(T settings) where T : ProviderSettingsBase
        {
            ProviderRegistry.Instance?.Save(settings);
        }
    }
}
