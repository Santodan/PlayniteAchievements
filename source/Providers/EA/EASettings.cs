using PlayniteAchievements.Providers.Settings;
using System;

namespace PlayniteAchievements.Providers.EA
{
    public sealed class EASettings : ProviderSettingsBase
    {
        private string _playerId;
        private string _playerSubId;
        private string _displayName;
        private bool _useExophaseForRarity;

        public override string ProviderKey => "EA";

        /// <summary>
        /// EA player ID (the "pd" field from identity query).
        /// </summary>
        public string PlayerId
        {
            get => _playerId;
            set => SetValue(ref _playerId, value);
        }

        /// <summary>
        /// EA player sub ID (the "psd" field). Used as the key in achievement queries.
        /// </summary>
        public string PlayerSubId
        {
            get => _playerSubId;
            set => SetValue(ref _playerSubId, value);
        }

        /// <summary>
        /// EA display name.
        /// </summary>
        public string DisplayName
        {
            get => _displayName;
            set => SetValue(ref _displayName, value);
        }

        /// <summary>
        /// When true, enriches EA achievement rarity from Exophase after native scanning.
        /// </summary>
        public bool UseExophaseForRarity
        {
            get => _useExophaseForRarity;
            set => SetValue(ref _useExophaseForRarity, value);
        }
    }
}
