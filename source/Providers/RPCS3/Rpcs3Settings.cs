using PlayniteAchievements.Providers.Settings;

namespace PlayniteAchievements.Providers.RPCS3
{
    /// <summary>
    /// RPCS3 emulator provider settings.
    /// </summary>
    public class Rpcs3Settings : ProviderSettingsBase
    {
        private string _executablePath;

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

        /// <inheritdoc />
        public override IProviderSettings Clone()
        {
            return new Rpcs3Settings
            {
                IsEnabled = IsEnabled,
                ExecutablePath = ExecutablePath
            };
        }

        /// <inheritdoc />
        public override void CopyFrom(IProviderSettings source)
        {
            if (source is Rpcs3Settings other)
            {
                IsEnabled = other.IsEnabled;
                ExecutablePath = other.ExecutablePath;
            }
        }
    }
}
