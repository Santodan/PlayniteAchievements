using PlayniteAchievements.Providers.Settings;

namespace PlayniteAchievements.Providers.Steam
{
    /// <summary>
    /// Steam-specific provider settings.
    /// </summary>
    public class SteamSettings : ProviderSettingsBase
    {
        private string _steamUserId;
        private string _steamApiKey;

        /// <inheritdoc />
        public override string ProviderKey => "Steam";

        /// <summary>
        /// Gets or sets the Steam user ID (custom URL or numeric ID).
        /// </summary>
        public string SteamUserId
        {
            get => _steamUserId;
            set => SetValue(ref _steamUserId, value);
        }

        /// <summary>
        /// Gets or sets the Steam Web API key.
        /// </summary>
        public string SteamApiKey
        {
            get => _steamApiKey;
            set => SetValue(ref _steamApiKey, value);
        }
    }
}
