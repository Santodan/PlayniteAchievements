using PlayniteAchievements.Providers.Settings;

namespace PlayniteAchievements.Providers.Xenia
{
    /// <summary>
    /// Xenia emulator provider settings.
    /// </summary>
    public class XeniaSettings : ProviderSettingsBase
    {
        private string _accountPath;

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
    }
}
