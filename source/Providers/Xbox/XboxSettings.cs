using PlayniteAchievements.Providers.Settings;

namespace PlayniteAchievements.Providers.Xbox
{
    /// <summary>
    /// Xbox provider settings. Authentication is handled via session manager.
    /// </summary>
    public class XboxSettings : ProviderSettingsBase
    {
        private bool _lowResIcons;
        private bool _useExophaseForRarity;

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

        /// <summary>
        /// When true, enriches Xbox achievement rarity from Exophase after native scanning.
        /// </summary>
        public bool UseExophaseForRarity
        {
            get => _useExophaseForRarity;
            set => SetValue(ref _useExophaseForRarity, value);
        }
    }
}
