using System.Collections.Generic;
using PlayniteAchievements.Providers.Settings;

namespace PlayniteAchievements.Providers.Vita3K
{
    /// <summary>
    /// Vita3K emulator provider settings.
    /// </summary>
    public class Vita3KSettings : ProviderSettingsBase
    {
        private List<string> _executablePaths = new List<string>();

        /// <inheritdoc />
        public override string ProviderKey => "Vita3K";

        /// <summary>
        /// Paths to Vita3K executables (Vita3K.exe), one per install. A game whose launch action
        /// names a Vita3K emulator uses that install; otherwise these are tried in order.
        /// </summary>
        public List<string> ExecutablePaths
        {
            get => _executablePaths;
            set => SetValue(ref _executablePaths, ProviderPathList.Normalize(value));
        }
    }
}
