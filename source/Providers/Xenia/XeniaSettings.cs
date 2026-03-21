using System;
using System.Collections.Generic;
using PlayniteAchievements.Providers.Settings;

namespace PlayniteAchievements.Providers.Xenia
{
    /// <summary>
    /// Xenia emulator provider settings.
    /// </summary>
    public class XeniaSettings : ProviderSettingsBase
    {
        private string _accountPath;
        private Dictionary<Guid, string> _gameIdOverrides = new Dictionary<Guid, string>();

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
        /// Per-game Xbox Title ID overrides for games where auto-detection fails.
        /// Key = Playnite Game ID, Value = Xbox Title ID (hex string, e.g., "584109DF").
        /// </summary>
        public Dictionary<Guid, string> GameIdOverrides
        {
            get => _gameIdOverrides;
            set => SetValue(ref _gameIdOverrides, value ?? new Dictionary<Guid, string>());
        }

        /// <inheritdoc />
        public override IProviderSettings Clone()
        {
            return new XeniaSettings
            {
                IsEnabled = IsEnabled,
                AccountPath = AccountPath,
                GameIdOverrides = GameIdOverrides != null ? new Dictionary<Guid, string>(GameIdOverrides) : new Dictionary<Guid, string>()
            };
        }

        /// <inheritdoc />
        public override void CopyFrom(IProviderSettings source)
        {
            if (source is XeniaSettings other)
            {
                IsEnabled = other.IsEnabled;
                AccountPath = other.AccountPath;
                GameIdOverrides = other.GameIdOverrides != null ? new Dictionary<Guid, string>(other.GameIdOverrides) : new Dictionary<Guid, string>();
            }
        }
    }
}
