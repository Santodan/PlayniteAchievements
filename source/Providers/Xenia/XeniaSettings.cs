using System.Collections.Generic;
using Newtonsoft.Json;
using PlayniteAchievements.Providers.Settings;

namespace PlayniteAchievements.Providers.Xenia
{
    /// <summary>
    /// Xenia emulator provider settings.
    /// </summary>
    public class XeniaSettings : ProviderSettingsBase
    {
        private List<string> _accountPaths = new List<string>();
        private bool _useExophaseForRarity;

        /// <inheritdoc />
        public override string ProviderKey => "Xenia";

        /// <summary>
        /// Xenia account folders, one per Xenia build (stock, Canary, Netplay, ...).
        /// Every folder is scanned and per-game results are merged.
        /// </summary>
        public List<string> AccountPaths
        {
            get => _accountPaths;
            set => SetValue(ref _accountPaths, ProviderPathList.Normalize(value));
        }

        /// <summary>
        /// Reads the single-path setting from configs saved before <see cref="AccountPaths"/>; never written.
        /// </summary>
        [JsonProperty("AccountPath")]
        private string LegacyAccountPath
        {
            set => AccountPaths = ProviderPathList.FromLegacy(value);
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
