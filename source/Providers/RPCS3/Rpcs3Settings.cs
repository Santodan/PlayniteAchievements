using System.Collections.Generic;
using Newtonsoft.Json;
using PlayniteAchievements.Providers.Settings;

namespace PlayniteAchievements.Providers.RPCS3
{
    /// <summary>
    /// RPCS3 emulator provider settings.
    /// </summary>
    public class Rpcs3Settings : ProviderSettingsBase
    {
        private List<string> _executablePaths = new List<string>();
        private bool _useExophaseForRarity;

        /// <inheritdoc />
        public override string ProviderKey => "RPCS3";

        /// <summary>
        /// Paths to RPCS3 executables (rpcs3.exe), one per install. A game whose launch action
        /// names an RPCS3 emulator uses that install; otherwise these are tried in order.
        /// </summary>
        public List<string> ExecutablePaths
        {
            get => _executablePaths;
            set => SetValue(ref _executablePaths, ProviderPathList.Normalize(value));
        }

        /// <summary>
        /// Reads the single-path setting from configs saved before <see cref="ExecutablePaths"/>; never written.
        /// </summary>
        [JsonProperty("ExecutablePath")]
        private string LegacyExecutablePath
        {
            set => ExecutablePaths = ProviderPathList.FromLegacy(value);
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
