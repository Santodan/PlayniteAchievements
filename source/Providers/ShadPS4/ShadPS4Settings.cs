using System.Collections.Generic;
using Newtonsoft.Json;
using PlayniteAchievements.Providers.Settings;

namespace PlayniteAchievements.Providers.ShadPS4
{
    /// <summary>
    /// ShadPS4 emulator provider settings.
    /// </summary>
    public class ShadPS4Settings : ProviderSettingsBase
    {
        private List<string> _gameDataPaths = ProviderPathList.FromLegacy(ShadPS4PathResolver.GetDefaultSettingsPath());
        private bool _useExophaseForRarity;

        /// <inheritdoc />
        public override string ProviderKey => "ShadPS4";

        /// <summary>
        /// ShadPS4 root paths, one per install. Each entry may be an emulator install root, a data
        /// root, or a legacy game_data path; titles are looked up across all of them in order.
        /// </summary>
        public List<string> GameDataPaths
        {
            get => _gameDataPaths;
            set => SetValue(ref _gameDataPaths, ProviderPathList.Normalize(value));
        }

        /// <summary>
        /// Reads the single-path setting from configs saved before <see cref="GameDataPaths"/>; never written.
        /// </summary>
        [JsonProperty("GameDataPath")]
        private string LegacyGameDataPath
        {
            set => GameDataPaths = ProviderPathList.FromLegacy(value);
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
