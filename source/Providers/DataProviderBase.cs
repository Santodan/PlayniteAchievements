using PlayniteAchievements.Providers.Settings;

namespace PlayniteAchievements.Providers
{
    /// <summary>
    /// Base for data providers backed by a ProviderRegistry-managed settings object.
    /// Supplies the typed settings instance plus the GetSettings/ApplySettings members
    /// of IDataProvider.
    /// </summary>
    public abstract class DataProviderBase<TSettings> where TSettings : ProviderSettingsBase, new()
    {
        protected TSettings ProviderSettings { get; } = ProviderRegistry.Settings<TSettings>();

        public IProviderSettings GetSettings() => ProviderSettings;

        public void ApplySettings(IProviderSettings settings)
        {
            if (settings is TSettings typed)
            {
                ProviderSettings.CopyFrom(typed);
            }
        }
    }
}
