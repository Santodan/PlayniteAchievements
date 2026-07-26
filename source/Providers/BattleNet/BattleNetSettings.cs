using System;
using PlayniteAchievements.Providers.Settings;

namespace PlayniteAchievements.Providers.BattleNet
{
    public sealed class BattleNetSettings : ProviderSettingsBase
    {
        public const string DefaultRedirectUri = "http://127.0.0.1:55431/";
        public const string LegacyDefaultRedirectUri = "https://localhost";

        public override string ProviderKey => "BattleNet";

        private string _battleNetClientId;
        private string _battleNetClientSecret;
        private string _battleNetRedirectUri = DefaultRedirectUri;
        private string _battleNetAccessToken;
        private string _battleNetRefreshToken;
        private string _battleNetTokenType;
        private DateTime _battleNetTokenExpiryUtc;
        private string _battleNetAccountId;
        private string _battleNetBattleTag;
        private int _sc2RegionId = 1;
        private int _sc2RealmId = 1;
        private int _sc2ProfileId;
        private string _wowRegion;
        private string _wowRealmSlug;
        private string _wowCharacter;
        private bool _wowAggregateAccountCharacters = true;
        private bool _useDataForAzerothForWowRarity = true;

        public string BattleNetClientId
        {
            get => _battleNetClientId;
            set => SetValue(ref _battleNetClientId, value);
        }

        public string BattleNetClientSecret
        {
            get => _battleNetClientSecret;
            set => SetValue(ref _battleNetClientSecret, value);
        }

        public string BattleNetRedirectUri
        {
            get => _battleNetRedirectUri;
            set => SetValue(ref _battleNetRedirectUri, value);
        }

        public string BattleNetAccessToken
        {
            get => _battleNetAccessToken;
            set => SetValue(ref _battleNetAccessToken, value);
        }

        public string BattleNetRefreshToken
        {
            get => _battleNetRefreshToken;
            set => SetValue(ref _battleNetRefreshToken, value);
        }

        public string BattleNetTokenType
        {
            get => _battleNetTokenType;
            set => SetValue(ref _battleNetTokenType, value);
        }

        public DateTime BattleNetTokenExpiryUtc
        {
            get => _battleNetTokenExpiryUtc;
            set => SetValue(ref _battleNetTokenExpiryUtc, value);
        }

        public string BattleNetAccountId
        {
            get => _battleNetAccountId;
            set => SetValue(ref _battleNetAccountId, value);
        }

        public string BattleNetBattleTag
        {
            get => _battleNetBattleTag;
            set => SetValue(ref _battleNetBattleTag, value);
        }

        public int Sc2RegionId
        {
            get => _sc2RegionId;
            set => SetValue(ref _sc2RegionId, value);
        }

        public int Sc2RealmId
        {
            get => _sc2RealmId;
            set => SetValue(ref _sc2RealmId, value);
        }

        public int Sc2ProfileId
        {
            get => _sc2ProfileId;
            set => SetValue(ref _sc2ProfileId, value);
        }

        public string WowRegion
        {
            get => _wowRegion;
            set => SetValue(ref _wowRegion, value);
        }

        public string WowRealmSlug
        {
            get => _wowRealmSlug;
            set => SetValue(ref _wowRealmSlug, value);
        }

        public string WowCharacter
        {
            get => _wowCharacter;
            set => SetValue(ref _wowCharacter, value);
        }

        public bool WowAggregateAccountCharacters
        {
            get => _wowAggregateAccountCharacters;
            set => SetValue(ref _wowAggregateAccountCharacters, value);
        }

        /// <summary>
        /// When true, enriches World of Warcraft achievement rarity from Data for Azeroth after native scanning.
        /// </summary>
        public bool UseDataForAzerothForWowRarity
        {
            get => _useDataForAzerothForWowRarity;
            set => SetValue(ref _useDataForAzerothForWowRarity, value);
        }

        public static bool IsLegacyDefaultRedirectUri(string redirectUri)
        {
            return string.Equals(
                (redirectUri ?? string.Empty).Trim().TrimEnd('/'),
                LegacyDefaultRedirectUri,
                StringComparison.OrdinalIgnoreCase);
        }
    }
}
