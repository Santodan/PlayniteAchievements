using PlayniteAchievements.Providers.Settings;

namespace PlayniteAchievements.Providers.BattleNet
{
    public sealed class BattleNetSettings : ProviderSettingsBase
    {
        public override string ProviderKey => "BattleNet";

        private string _battleNetClientId;
        private string _battleNetClientSecret;
        private int _sc2RegionId = 1;
        private int _sc2RealmId = 1;
        private int _sc2ProfileId;
        private string _wowRegion;
        private string _wowRealmSlug;
        private string _wowCharacter;

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
    }
}
