using PlayniteAchievements.Providers.Settings;

namespace PlayniteAchievements.Providers.ShadPS4
{
    /// <summary>
    /// ShadPS4 emulator provider settings.
    /// </summary>
    public class ShadPS4Settings : ProviderSettingsBase
    {
        private string _gameDataPath = ShadPS4PathResolver.GetDefaultSettingsPath();

        /// <inheritdoc />
        public override string ProviderKey => "ShadPS4";

        /// <summary>
        /// Gets or sets the path to the ShadPS4 AppData root or legacy user/game_data folder.
        /// </summary>
        public string GameDataPath
        {
            get => _gameDataPath;
            set => SetValue(ref _gameDataPath, value);
        }
    }
}
