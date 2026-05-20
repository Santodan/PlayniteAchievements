using PlayniteAchievements.Providers.Settings;

namespace PlayniteAchievements.Providers.ShadPS4
{
    /// <summary>
    /// ShadPS4 emulator provider settings.
    /// </summary>
    public class ShadPS4Settings : ProviderSettingsBase
    {
        private string _gameDataPath = ShadPS4PathResolver.GetDefaultSettingsPath();
        private bool _useExophaseForRarity;

        /// <inheritdoc />
        public override string ProviderKey => "ShadPS4";

        /// <summary>
        /// Gets or sets the ShadPS4 root path.
        /// The resolver accepts emulator install roots, data roots, and legacy game_data paths.
        /// </summary>
        public string GameDataPath
        {
            get => _gameDataPath;
            set => SetValue(ref _gameDataPath, value);
        }

        /// <summary>
        /// When true, enriches ShadPS4 trophy rarity from Exophase after native scanning.
        /// </summary>
        public bool UseExophaseForRarity
        {
            get => _useExophaseForRarity;
            set => SetValue(ref _useExophaseForRarity, value);
        }
    }
}
