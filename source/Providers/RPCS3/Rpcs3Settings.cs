using PlayniteAchievements.Providers.Settings;

namespace PlayniteAchievements.Providers.RPCS3
{
    /// <summary>
    /// RPCS3 emulator provider settings.
    /// </summary>
    public class Rpcs3Settings : ProviderSettingsBase
    {
        private string _executablePath;
        private bool _useExophaseForRarity;

        /// <inheritdoc />
        public override string ProviderKey => "RPCS3";

        /// <summary>
        /// Path to the RPCS3 executable (rpcs3.exe).
        /// </summary>
        public string ExecutablePath
        {
            get => _executablePath;
            set => SetValue(ref _executablePath, value ?? string.Empty);
        }

        /// <summary>
        /// When true, enriches RPCS3 trophy rarity from Exophase after native scanning.
        /// </summary>
        public bool UseExophaseForRarity
        {
            get => _useExophaseForRarity;
            set => SetValue(ref _useExophaseForRarity, value);
        }
    }
}
