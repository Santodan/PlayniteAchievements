using PlayniteAchievements.Providers.Settings;

namespace PlayniteAchievements.Providers.Hypixel
{
    /// <summary>
    /// Hypixel provider settings. Achievement data is read from the public hypixel.net profile
    /// page of the configured player.
    /// </summary>
    public class HypixelSettings : ProviderSettingsBase
    {
        private string _username;

        /// <inheritdoc />
        public override string ProviderKey => "Hypixel";

        /// <summary>
        /// Minecraft username whose Hypixel profile is read.
        /// </summary>
        public string Username
        {
            get => _username;
            set => SetValue(ref _username, value);
        }
    }
}
