using PlayniteAchievements.Providers.Settings;

namespace PlayniteAchievements.Providers.Xbox
{
    /// <summary>
    /// Xbox provider settings. Authentication is handled via session manager.
    /// </summary>
    public class XboxSettings : ProviderSettingsBase
    {
        private bool _lowResIcons;

        /// <inheritdoc />
        public override string ProviderKey => "Xbox";

        /// <summary>
        /// When true, requests smaller 128px icons from Xbox CDN to improve download speed.
        /// </summary>
        public bool LowResIcons
        {
            get => _lowResIcons;
            set => SetValue(ref _lowResIcons, value);
        }
    }
}
