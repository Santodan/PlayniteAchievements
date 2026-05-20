using PlayniteAchievements.Providers.Settings;

namespace PlayniteAchievements.Providers.Xenia
{
    /// <summary>
    /// Xenia emulator provider settings.
    /// </summary>
    public class XeniaSettings : ProviderSettingsBase
    {
        private string _accountPath;
        private bool _useExophaseForRarity;

        /// <inheritdoc />
        public override string ProviderKey => "Xenia";

        /// <summary>
        /// Gets or sets the path to the Xenia account folder.
        /// </summary>
        public string AccountPath
        {
            get => _accountPath;
            set => SetValue(ref _accountPath, value);
        }

        /// <summary>
        /// When true, enriches Xenia achievement rarity from Exophase after native scanning.
        /// </summary>
        public bool UseExophaseForRarity
        {
            get => _useExophaseForRarity;
            set => SetValue(ref _useExophaseForRarity, value);
        }
    }
}
